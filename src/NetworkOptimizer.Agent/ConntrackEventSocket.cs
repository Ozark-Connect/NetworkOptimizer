using System.Diagnostics;
using System.Runtime.InteropServices;
using NetworkOptimizer.Monitoring.Conntrack;

namespace NetworkOptimizer.Agent;

/// <summary>
/// The handshake timer's feed: a ctnetlink socket on the conntrack NEW and UPDATE groups, open while the
/// server's conntrack config (or a Live Traffic lease) asks for flow latency. A kernel BPF filter (<see cref="ConntrackEventFilter"/>)
/// keeps only the events a handshake needs. Measured on a UXG-Fiber at 300 new connections a second:
/// 1.3 % of one core, about a fifth of what <c>conntrack -E</c> costs for the same events.
/// </summary>
public sealed class ConntrackEventSocket
{
    public const string Capability = "flow-latency";

    private const string EventsSysctl = "/proc/sys/net/netfilter/nf_conntrack_events";
    private const int AfNetlink = 16, SockRaw = 3, NetlinkNetfilter = 12;
    private const int SolSocket = 1, SoRcvtimeo = 20, SoAttachFilter = 26, SoRcvbufforce = 33;
    private const uint GroupNew = 1, GroupUpdate = 2;
    private const int ENoBufs = 105, EAgain = 11, EIntr = 4;
    private const int ReceiveBufferBytes = 4 << 20;

    // A receive that returns sooner than this found the event already queued: its read time may be late.
    private static readonly long QueuedTicks = Stopwatch.Frequency / 20_000;

    // A flood (SYN or DNS) makes an event per packet. Past this rate the socket closes for a while, which
    // also stops the kernel building the events: about a fifth of a core at the measured ~20 us an event.
    private const int MaxEventsPerSecond = 10_000;
    private static readonly TimeSpan FloodPause = TimeSpan.FromSeconds(60);

    private volatile bool _wanted;
    private readonly HandshakeTimer _timer;

    public ConntrackEventSocket(HandshakeTimer timer) => _timer = timer;

    /// <summary>Whether the socket is open right now.</summary>
    public bool Active { get; private set; }

    /// <summary>
    /// Whether the kernel delivers conntrack events at all. Some Gateway kernels are built without them
    /// (the UDR7's): there is nothing to time there.
    /// </summary>
    public static bool Supported()
    {
        try
        {
            return File.Exists(EventsSysctl) && File.ReadAllText(EventsSysctl).Trim() == "1";
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Opens or closes the socket; the runner calls this every pass with the config and lease state.</summary>
    public void SetWanted(bool wanted) => _wanted = wanted;

    /// <summary>Asked to run. The batches report this rather than <see cref="Active"/>, which lags it by up to a second.</summary>
    public bool Wanted => _wanted;

    /// <summary>Runs on a dedicated thread for the life of the agent.</summary>
    public Task RunAsync(CancellationToken ct) =>
        Task.Factory.StartNew(() => Run(ct), ct, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    private void Run(CancellationToken ct)
    {
        var fd = -1;
        var buffer = new byte[256 * 1024];
        var events = 0;
        var windowStart = Stopwatch.GetTimestamp();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (fd >= 0 && Stopwatch.GetElapsedTime(windowStart) >= TimeSpan.FromSeconds(1))
                {
                    var rate = events;
                    events = 0;
                    windowStart = Stopwatch.GetTimestamp();
                    if (rate > MaxEventsPerSecond)
                    {
                        close(fd);
                        fd = -1;
                        Active = false;
                        _timer.OnLoss();
                        Console.WriteLine($"Flow latency: paused for {FloodPause.TotalSeconds:0} s ({rate} conntrack events in one second)");
                        ct.WaitHandle.WaitOne(FloodPause);
                        continue;
                    }
                }
                if (!_wanted)
                {
                    if (fd >= 0)
                    {
                        close(fd);
                        fd = -1;
                        Active = false;
                        // Events stop with the socket, so handshakes in progress cannot finish. Finished
                        // round trips stay for the next viewer: the flows they belong to may still be open.
                        _timer.OnLoss();
                        Console.WriteLine("Flow latency: stopped (no longer asked for)");
                    }
                    ct.WaitHandle.WaitOne(TimeSpan.FromSeconds(1));
                    continue;
                }
                if (fd < 0)
                {
                    fd = Open();
                    if (fd < 0)
                    {
                        ct.WaitHandle.WaitOne(TimeSpan.FromSeconds(30));
                        continue;
                    }
                    Active = true;
                    Console.WriteLine("Flow latency: started");
                }

                var before = Stopwatch.GetTimestamp();
                var n = (int)recv(fd, buffer, buffer.Length, 0);
                var after = Stopwatch.GetTimestamp();
                if (n <= 0)
                {
                    var errno = n < 0 ? Marshal.GetLastPInvokeError() : 0;
                    if (errno == ENoBufs) _timer.OnLoss();
                    else if (n < 0 && errno != EAgain && errno != EIntr)
                    {
                        Console.Error.WriteLine($"Flow latency: receive failed (errno {errno}); reopening");
                        close(fd);
                        fd = -1;
                        Active = false;
                        _timer.OnLoss();
                        ct.WaitHandle.WaitOne(TimeSpan.FromSeconds(5));
                    }
                    continue;
                }
                var at = (double)after / Stopwatch.Frequency;
                var queued = after - before < QueuedTicks;
                ConntrackEventParser.ParseDatagram(buffer.AsSpan(0, n), e =>
                {
                    events++;
                    _timer.OnEvent(e, at, queued);
                });
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Flow latency stopped: {ex.Message}");
        }
        finally
        {
            if (fd >= 0) close(fd);
            Active = false;
        }
    }

    private static int Open()
    {
        var fd = socket(AfNetlink, SockRaw, NetlinkNetfilter);
        if (fd < 0)
        {
            Console.Error.WriteLine($"Flow latency: cannot open a netlink socket (errno {Marshal.GetLastPInvokeError()})");
            return -1;
        }

        // The filter goes on before the bind, so no unfiltered event is ever queued.
        var program = ConntrackEventFilter.Build();
        var filterBytes = ConntrackEventFilter.Serialize(program);
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
                Console.Error.WriteLine($"Flow latency: cannot attach the event filter (errno {Marshal.GetLastPInvokeError()})");
                close(fd);
                return -1;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(filterPtr);
        }

        // Kernel memory is taken only as events queue; a burst then waits instead of overflowing.
        setsockopt(fd, SolSocket, SoRcvbufforce, BitConverter.GetBytes(ReceiveBufferBytes), 4);

        // struct sockaddr_nl { family, pad, pid, groups }
        var addr = new byte[12];
        BitConverter.TryWriteBytes(addr.AsSpan(0, 2), (ushort)AfNetlink);
        BitConverter.TryWriteBytes(addr.AsSpan(8, 4), GroupNew | GroupUpdate);
        if (bind(fd, addr, addr.Length) != 0)
        {
            Console.Error.WriteLine($"Flow latency: cannot join the conntrack event groups (errno {Marshal.GetLastPInvokeError()})");
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
    private static extern nint recv(int fd, byte[] buf, nint len, int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int close(int fd);
}
