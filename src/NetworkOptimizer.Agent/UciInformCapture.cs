using System.Runtime.InteropServices;
using Google.Protobuf;
using NetworkOptimizer.AgentProtocol;
using NetworkOptimizer.Monitoring.UciInform;

namespace NetworkOptimizer.Agent;

/// <summary>
/// Passive tap for UniFi Cable Internet (UCI) inform frames on an on-gateway agent. The UCI's
/// DOCSIS tables exist only inside the encrypted informs it POSTs through the gateway, and the
/// Network app drops them after reading its state. This opens an AF_PACKET socket with a kernel
/// BPF filter on the UCI's source MAC, rebuilds each POST, and relays the raw body over the
/// tunnel. It never sits on the management path, holds no key, and parses no DOCSIS: the server
/// decrypts. Raw libc calls rather than <see cref="System.Net.Sockets.Socket"/>, whose AF_PACKET
/// support depends on the runtime's protocol mapping; these calls behave the same everywhere.
/// </summary>
public sealed class UciInformCapture
{
    /// <summary>The capability string, shared by agent and server.</summary>
    public const string Capability = "uci-inform";

    private const int AfPacket = 17;
    private const int SockRaw = 3;
    private const int EthPAllNetworkOrder = 0x0300; // htons(ETH_P_ALL)
    private const int SolSocket = 1;
    private const int SoRcvtimeo = 20;
    private const int SoAttachFilter = 26;
    private const int PacketOutgoing = 4;
    private const int EAgain = 11;  // also EWOULDBLOCK on Linux
    private const int EIntr = 4;
    private const int MaxMacs = 16;
    private const int RecvBufferBytes = 262144;

    private readonly object _gate = new();
    private IReadOnlyList<string> _macs = Array.Empty<string>();
    private int _configVersion;
    private readonly UciInformReassembler _reassembler = new();
    private readonly HashSet<string> _announced = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Live path: the current tunnel's TrySend, set while a tunnel is up. Null drops the frame.</summary>
    public volatile Func<AgentMessage, bool>? LiveSend;

    /// <summary>
    /// Whether this host can open an AF_PACKET socket (Linux, root or CAP_NET_RAW). Answered once
    /// at startup for the hello's capability list.
    /// </summary>
    public static bool Available()
    {
        // The sock_fprog and timeval layouts below are the 64-bit ones; every gateway RID is 64-bit.
        if (!OperatingSystem.IsLinux() || IntPtr.Size != 8) return false;
        try
        {
            var fd = socket(AfPacket, SockRaw, EthPAllNetworkOrder);
            if (fd < 0) return false;
            close(fd);
            return true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    public void UpdateConfig(UciCaptureConfig config)
    {
        var macs = config.Macs
            .Select(m => UciBpfFilter.ParseMac(m))
            .Where(m => m != null)
            .Select(m => UciBpfFilter.FormatMac(m!))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxMacs)
            .ToList();
        lock (_gate)
        {
            if (macs.SequenceEqual(_macs, StringComparer.OrdinalIgnoreCase)) return;
            _macs = macs;
            _configVersion++;
        }
        Console.WriteLine(macs.Count > 0
            ? $"UCI inform capture: watching {string.Join(", ", macs)}"
            : "UCI inform capture: off (no UCI configured)");
    }

    /// <summary>Runs the capture on a dedicated thread for the life of the agent.</summary>
    public Task RunAsync(CancellationToken ct) =>
        Task.Factory.StartNew(() => Run(ct), ct, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    private void Run(CancellationToken ct)
    {
        var fd = -1;
        var appliedVersion = -1;
        var buffer = new byte[RecvBufferBytes];
        var addr = new byte[20]; // struct sockaddr_ll
        try
        {
            while (!ct.IsCancellationRequested)
            {
                IReadOnlyList<string> macs;
                int version;
                lock (_gate)
                {
                    macs = _macs;
                    version = _configVersion;
                }

                if (macs.Count == 0)
                {
                    if (fd >= 0) { close(fd); fd = -1; appliedVersion = -1; }
                    ct.WaitHandle.WaitOne(TimeSpan.FromSeconds(5));
                    continue;
                }

                if (fd < 0 || appliedVersion != version)
                {
                    if (fd >= 0) { close(fd); fd = -1; }
                    fd = OpenFiltered(macs);
                    if (fd < 0)
                    {
                        ct.WaitHandle.WaitOne(TimeSpan.FromSeconds(30));
                        continue;
                    }
                    appliedVersion = version;
                }

                var addrLen = addr.Length;
                var n = (int)recvfrom(fd, buffer, buffer.Length, 0, addr, ref addrLen);
                var now = DateTime.UtcNow;
                if (n <= 0)
                {
                    // Timeout (EAGAIN) is the normal idle case; it lets the loop see cancellation
                    // and config changes. Any other error reopens the socket after a pause rather
                    // than spinning on a dead one.
                    var errno = n < 0 ? Marshal.GetLastPInvokeError() : 0;
                    if (n < 0 && errno != EAgain && errno != EIntr)
                    {
                        Console.Error.WriteLine($"UCI inform capture: receive failed (errno {errno}); reopening");
                        close(fd);
                        fd = -1;
                        ct.WaitHandle.WaitOne(TimeSpan.FromSeconds(5));
                    }
                    _reassembler.Sweep(now);
                    continue;
                }

                // sll_pkttype (offset 10): skip what this host sent; the UCI's frames arrive inbound.
                if (addrLen >= 11 && addr[10] == PacketOutgoing)
                    continue;

                foreach (var inform in _reassembler.OnFrame(buffer.AsSpan(0, n), now))
                    Relay(inform);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"UCI inform capture stopped: {ex.Message}");
        }
        finally
        {
            if (fd >= 0) close(fd);
        }
    }

    private void Relay(CapturedInform inform)
    {
        if (_announced.Add(inform.SourceMac))
            Console.WriteLine($"UCI inform capture: first inform from {inform.SourceMac} ({inform.Body.Length} bytes)");

        var message = new AgentMessage
        {
            UciInformFrame = new UciInformFrame
            {
                SourceMac = inform.SourceMac,
                Frame = ByteString.CopyFrom(inform.Body),
                CapturedAtUnixMs = new DateTimeOffset(inform.CapturedAt).ToUnixTimeMilliseconds(),
            }
        };
        // Live only: a snapshot lost while the tunnel is down is superseded by the next inform,
        // and the modem keeps its own event log ring, so nothing is spooled.
        LiveSend?.Invoke(message);
    }

    private static int OpenFiltered(IReadOnlyList<string> macs)
    {
        var fd = socket(AfPacket, SockRaw, EthPAllNetworkOrder);
        if (fd < 0)
        {
            Console.Error.WriteLine($"UCI inform capture: cannot open a packet socket (errno {Marshal.GetLastPInvokeError()})");
            return -1;
        }

        var program = UciBpfFilter.Build(macs.Select(m => UciBpfFilter.ParseMac(m)!).ToList());
        var filterBytes = UciBpfFilter.Serialize(program);
        var filterPtr = Marshal.AllocHGlobal(filterBytes.Length);
        try
        {
            Marshal.Copy(filterBytes, 0, filterPtr, filterBytes.Length);
            // struct sock_fprog { unsigned short len; struct sock_filter *filter; } - 16 bytes on
            // 64-bit (the pointer is 8-aligned). The kernel copies the program during the call.
            var fprog = new byte[16];
            BitConverter.TryWriteBytes(fprog.AsSpan(0, 2), (ushort)program.Count);
            BitConverter.TryWriteBytes(fprog.AsSpan(8, 8), filterPtr.ToInt64());
            if (setsockopt(fd, SolSocket, SoAttachFilter, fprog, fprog.Length) != 0)
            {
                Console.Error.WriteLine($"UCI inform capture: cannot attach the packet filter (errno {Marshal.GetLastPInvokeError()})");
                close(fd);
                return -1;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(filterPtr);
        }

        // struct timeval { long tv_sec; long tv_usec; } - 1 s, so the loop wakes to check for
        // cancellation and config changes.
        var timeout = new byte[16];
        BitConverter.TryWriteBytes(timeout.AsSpan(0, 8), 1L);
        setsockopt(fd, SolSocket, SoRcvtimeo, timeout, timeout.Length);
        return fd;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int socket(int domain, int type, int protocol);

    [DllImport("libc", SetLastError = true)]
    private static extern int setsockopt(int fd, int level, int optname, byte[] optval, int optlen);

    [DllImport("libc", SetLastError = true)]
    private static extern nint recvfrom(int fd, byte[] buf, nint len, int flags, byte[] srcAddr, ref int addrLen);

    [DllImport("libc", SetLastError = true)]
    private static extern int close(int fd);
}
