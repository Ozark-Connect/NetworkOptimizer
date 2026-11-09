using System.Net;
using System.Runtime.InteropServices;
using NetworkOptimizer.Monitoring.Conntrack;
using NetworkOptimizer.Monitoring.LiveTraffic;
using NetworkOptimizer.Monitoring.UciInform;

namespace NetworkOptimizer.Agent;

/// <summary>
/// Passive Live DNS tap on an on-gateway agent: an AF_PACKET socket with a kernel BPF filter for
/// "udp port 53", open only while a Live Traffic lease asks for DNS. Keeps the client-facing leg
/// of each lookup (see <see cref="DnsQueryTracker"/>), so a client's query to the gateway, to a
/// DNS server on another VLAN, or to an outside resolver is attributed to that client. A query to
/// a DNS server on the client's own VLAN is switched and never reaches the gateway.
/// </summary>
public sealed class DnsCapture
{
    public const string Capability = "live-dns";

    private const int AfPacket = 17;
    private const int SockRaw = 3;
    private const int EthPAllNetworkOrder = 0x0300; // htons(ETH_P_ALL)
    private const int SolSocket = 1;
    private const int SoRcvtimeo = 20;
    private const int SoAttachFilter = 26;
    private const int ArphrdEther = 1;
    private const int EAgain = 11;
    private const int EIntr = 4;
    private const int RecvBufferBytes = 8192;

    private readonly object _gate = new();
    private readonly DnsQueryTracker _tracker = new();
    private volatile bool _wanted;

    /// <summary>The host view the runner last built; says which addresses are site clients.</summary>
    public volatile ConntrackHostView? HostView;

    /// <summary>Whether the socket is open right now.</summary>
    public bool Active { get; private set; }

    /// <summary>Whether this host can open an AF_PACKET socket. Same probe as the UCI capture.</summary>
    public static bool Available() => UciInformCapture.Available();

    /// <summary>Opens or closes the capture; the runner calls this every pass with the lease state.</summary>
    public void SetWanted(bool wanted) => _wanted = wanted;

    /// <summary>The lookups finished since the last call.</summary>
    public List<LiveDnsRecord> Drain()
    {
        lock (_gate) return _tracker.Drain(DateTime.UtcNow);
    }

    /// <summary>Runs the capture on a dedicated thread for the life of the agent.</summary>
    public Task RunAsync(CancellationToken ct) =>
        Task.Factory.StartNew(() => Run(ct), ct, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    private void Run(CancellationToken ct)
    {
        var fd = -1;
        var buffer = new byte[RecvBufferBytes];
        var addr = new byte[20]; // struct sockaddr_ll
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (!_wanted)
                {
                    if (fd >= 0)
                    {
                        close(fd);
                        fd = -1;
                        Active = false;
                        lock (_gate) _tracker.Clear();
                        Console.WriteLine("Live DNS capture stopped (lease ended)");
                    }
                    ct.WaitHandle.WaitOne(TimeSpan.FromSeconds(1));
                    continue;
                }

                if (fd < 0)
                {
                    fd = OpenFiltered();
                    if (fd < 0)
                    {
                        ct.WaitHandle.WaitOne(TimeSpan.FromSeconds(30));
                        continue;
                    }
                    Active = true;
                    Console.WriteLine("Live DNS capture started");
                }

                var addrLen = addr.Length;
                var n = (int)recvfrom(fd, buffer, buffer.Length, 0, addr, ref addrLen);
                if (n <= 0)
                {
                    var errno = n < 0 ? Marshal.GetLastPInvokeError() : 0;
                    if (n < 0 && errno != EAgain && errno != EIntr)
                    {
                        Console.Error.WriteLine($"Live DNS capture: receive failed (errno {errno}); reopening");
                        close(fd);
                        fd = -1;
                        Active = false;
                        ct.WaitHandle.WaitOne(TimeSpan.FromSeconds(5));
                    }
                    continue;
                }

                // sll_hatype (offset 8): only Ethernet frames have the layout the filter and the
                // parser read. A tunnel or PPP interface hands over bare IP and is skipped.
                if (addrLen >= 10 && BitConverter.ToUInt16(addr, 8) != ArphrdEther) continue;
                if (DnsFrameParser.Parse(buffer.AsSpan(0, n)) is not { } packet) continue;
                var view = HostView;
                if (view == null) continue;
                lock (_gate) _tracker.OnPacket(packet, DateTime.UtcNow, ip => IsClient(view, ip));
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Live DNS capture stopped: {ex.Message}");
        }
        finally
        {
            if (fd >= 0) close(fd);
            Active = false;
        }
    }

    private static bool IsClient(ConntrackHostView view, IPAddress ip) =>
        view.IsInConnectedSubnet(ip) && !view.IsHostAddress(ip, out _);

    private static int OpenFiltered()
    {
        // Protocol 0 receives nothing until the bind below, so no unfiltered frame is ever queued.
        var fd = socket(AfPacket, SockRaw, 0);
        if (fd < 0)
        {
            Console.Error.WriteLine($"Live DNS capture: cannot open a packet socket (errno {Marshal.GetLastPInvokeError()})");
            return -1;
        }

        var program = DnsBpfFilter.Build();
        var filterBytes = UciBpfFilter.Serialize(program);
        var filterPtr = Marshal.AllocHGlobal(filterBytes.Length);
        try
        {
            Marshal.Copy(filterBytes, 0, filterPtr, filterBytes.Length);
            // struct sock_fprog { unsigned short len; struct sock_filter *filter; } - 16 bytes on 64-bit.
            var fprog = new byte[16];
            BitConverter.TryWriteBytes(fprog.AsSpan(0, 2), (ushort)program.Count);
            BitConverter.TryWriteBytes(fprog.AsSpan(8, 8), filterPtr.ToInt64());
            if (setsockopt(fd, SolSocket, SoAttachFilter, fprog, fprog.Length) != 0)
            {
                Console.Error.WriteLine($"Live DNS capture: cannot attach the packet filter (errno {Marshal.GetLastPInvokeError()})");
                close(fd);
                return -1;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(filterPtr);
        }

        // Outgoing frames stay in: the gateway resolver's answers to clients leave the gateway.
        var sll = new byte[20];
        BitConverter.TryWriteBytes(sll.AsSpan(0, 2), (ushort)AfPacket);
        BitConverter.TryWriteBytes(sll.AsSpan(2, 2), (ushort)EthPAllNetworkOrder);
        if (bind(fd, sll, sll.Length) != 0)
        {
            Console.Error.WriteLine($"Live DNS capture: cannot bind the packet socket (errno {Marshal.GetLastPInvokeError()})");
            close(fd);
            return -1;
        }

        // 1 s receive timeout, so the loop wakes to see the lease end.
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
    private static extern int bind(int fd, byte[] addr, int addrLen);

    [DllImport("libc", SetLastError = true)]
    private static extern nint recvfrom(int fd, byte[] buf, nint len, int flags, byte[] srcAddr, ref int addrLen);

    [DllImport("libc", SetLastError = true)]
    private static extern int close(int fd);
}
