using System.Net.NetworkInformation;
using Strata.Native;

namespace Strata.Widgets;

/// <summary>Samples CPU, memory and network once per second; widgets read the shared values.</summary>
public static class SystemStats
{
    public const int HistoryLength = 60;

    public static double Cpu { get; private set; }               // 0..1
    public static double Memory { get; private set; }            // 0..1
    public static ulong MemoryTotal { get; private set; }
    public static ulong MemoryUsed { get; private set; }
    public static double DownBytesPerSec { get; private set; }
    public static double UpBytesPerSec { get; private set; }

    public static readonly Queue<double> CpuHistory = new();
    public static readonly Queue<double> MemoryHistory = new();
    public static readonly Queue<double> DownHistory = new();
    public static readonly Queue<double> UpHistory = new();

    private static long _idle, _kernel, _user;
    private static long _rx, _tx;
    private static DateTime _netTime;
    private static DateTime _lastUpdate;

    private static TimeSpan? _fixedUptime;
    public static TimeSpan Uptime => _fixedUptime ?? TimeSpan.FromMilliseconds(Win32.GetTickCount64());

    /// <summary>Deterministic values for previews: no system sampling happens afterwards.</summary>
    public static void LoadFixture(IEnumerable<double> cpu, IEnumerable<double> memory, IEnumerable<double> down, IEnumerable<double> up,
        ulong memoryTotal, TimeSpan uptime)
    {
        void Fill(Queue<double> q, IEnumerable<double> values) { q.Clear(); foreach (var v in values) q.Enqueue(v); }
        Fill(CpuHistory, cpu);
        Fill(MemoryHistory, memory);
        Fill(DownHistory, down);
        Fill(UpHistory, up);
        Cpu = CpuHistory.LastOrDefault();
        Memory = MemoryHistory.LastOrDefault();
        DownBytesPerSec = DownHistory.LastOrDefault();
        UpBytesPerSec = UpHistory.LastOrDefault();
        MemoryTotal = memoryTotal;
        MemoryUsed = (ulong)(memoryTotal * Memory);
        _fixedUptime = uptime;
    }

    public static void Update()
    {
        Core.PrivacyGuard.ThrowIfPreview("live system counters");
        // Several callers may ask in the same second; sample once.
        if (DateTime.UtcNow - _lastUpdate < TimeSpan.FromMilliseconds(900)) return;
        _lastUpdate = DateTime.UtcNow;

        if (Win32.GetSystemTimes(out long idle, out long kernel, out long user))
        {
            long di = idle - _idle, dk = kernel - _kernel, du = user - _user;
            if (_kernel != 0 && dk + du > 0) Cpu = Math.Clamp(1.0 - (double)di / (dk + du), 0, 1);
            _idle = idle; _kernel = kernel; _user = user;
        }

        var mem = new Win32.MEMORYSTATUSEX { dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Win32.MEMORYSTATUSEX>() };
        if (Win32.GlobalMemoryStatusEx(ref mem))
        {
            MemoryTotal = mem.ullTotalPhys;
            MemoryUsed = mem.ullTotalPhys - mem.ullAvailPhys;
            Memory = mem.ullTotalPhys == 0 ? 0 : (double)MemoryUsed / mem.ullTotalPhys;
        }

        try
        {
            long rx = 0, tx = 0;
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                var s = nic.GetIPStatistics();
                rx += s.BytesReceived;
                tx += s.BytesSent;
            }
            var now = DateTime.UtcNow;
            if (_netTime != default)
            {
                double secs = Math.Max(0.1, (now - _netTime).TotalSeconds);
                DownBytesPerSec = Math.Max(0, (rx - _rx) / secs);
                UpBytesPerSec = Math.Max(0, (tx - _tx) / secs);
            }
            _rx = rx; _tx = tx; _netTime = now;
        }
        catch { /* adapters can disappear mid-enumeration */ }

        Push(CpuHistory, Cpu);
        Push(MemoryHistory, Memory);
        Push(DownHistory, DownBytesPerSec);
        Push(UpHistory, UpBytesPerSec);
    }

    private static void Push(Queue<double> q, double v)
    {
        q.Enqueue(v);
        while (q.Count > HistoryLength) q.Dequeue();
    }

    public static string FormatRate(double bytesPerSec)
    {
        if (bytesPerSec >= 1024 * 1024) return $"{bytesPerSec / (1024 * 1024):0.0} MB/s";
        if (bytesPerSec >= 1024) return $"{bytesPerSec / 1024:0} KB/s";
        return $"{bytesPerSec:0} B/s";
    }

    public static string FormatBytes(ulong bytes) => $"{bytes / (1024.0 * 1024 * 1024):0.0} GB";
}
