using System.Diagnostics;
using System.Runtime.InteropServices;

namespace HarryDataServer.Infrastructure;

/// <summary>Point-in-time machine and process resource figures for the System tab.</summary>
public sealed record SystemMetricsSnapshot
{
    /// <summary>Whole-machine CPU utilisation, 0..100.</summary>
    public double CpuPercent { get; init; }

    public double RamUsedGb { get; init; }
    public double RamTotalGb { get; init; }
    /// <summary>Whole-machine memory load, 0..100.</summary>
    public double RamPercent { get; init; }

    /// <summary>This server process CPU, normalised over all cores, 0..100.</summary>
    public double ServerCpuPercent { get; init; }
    public double ServerRamMb { get; init; }

    public bool MySqlRunning { get; init; }
    public double MySqlCpuPercent { get; init; }
    public double MySqlRamMb { get; init; }
}

/// <summary>
/// Samples whole-machine CPU/RAM and per-process CPU/RAM using only Win32 P/Invoke
/// (<c>GetSystemTimes</c>, <c>GlobalMemoryStatusEx</c>) plus <see cref="Process"/> — no
/// extra NuGet package. CPU figures are delta-based, so the first <see cref="Sample"/>
/// call after construction returns 0 % and the next call reports the load since it.
/// Not thread-safe: call from a single thread (the UI timer).
/// </summary>
public sealed class SystemMetricsSampler
{
    private readonly int _cpuCount = Math.Max(1, Environment.ProcessorCount);

    private ulong _prevIdle, _prevKernel, _prevUser;
    private bool _hasSystemBaseline;

    // Per-process CPU baselines keyed by process id (own process + mysqld).
    private readonly Dictionary<int, (TimeSpan Cpu, DateTime At)> _procBaseline = new();

    public SystemMetricsSnapshot Sample()
    {
        var (usedGb, totalGb, ramPct) = SampleMemory();
        var (serverCpu, serverRamMb) = SampleOwnProcess();
        var (mysqlRunning, mysqlCpu, mysqlRamMb) = SampleMySql();

        return new SystemMetricsSnapshot
        {
            CpuPercent = SampleSystemCpu(),
            RamUsedGb = usedGb,
            RamTotalGb = totalGb,
            RamPercent = ramPct,
            ServerCpuPercent = serverCpu,
            ServerRamMb = serverRamMb,
            MySqlRunning = mysqlRunning,
            MySqlCpuPercent = mysqlCpu,
            MySqlRamMb = mysqlRamMb,
        };
    }

    private double SampleSystemCpu()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user))
            return 0;

        var i = ToU(idle);
        var k = ToU(kernel); // kernel time already includes idle time
        var u = ToU(user);

        if (!_hasSystemBaseline)
        {
            _prevIdle = i; _prevKernel = k; _prevUser = u;
            _hasSystemBaseline = true;
            return 0;
        }

        var dIdle = i - _prevIdle;
        var dTotal = (k - _prevKernel) + (u - _prevUser);
        _prevIdle = i; _prevKernel = k; _prevUser = u;

        if (dTotal == 0)
            return 0;
        return Clamp(100.0 * (dTotal - dIdle) / dTotal);
    }

    private static (double UsedGb, double TotalGb, double Percent) SampleMemory()
    {
        var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref status))
            return (0, 0, 0);

        const double gb = 1024d * 1024d * 1024d;
        var total = status.ullTotalPhys / gb;
        var used = (status.ullTotalPhys - status.ullAvailPhys) / gb;
        return (used, total, Clamp(status.dwMemoryLoad));
    }

    private (double Cpu, double RamMb) SampleOwnProcess()
    {
        try
        {
            using var self = Process.GetCurrentProcess();
            return (SampleProcessCpu(self), self.WorkingSet64 / (1024d * 1024d));
        }
        catch { return (0, 0); }
    }

    private (bool Running, double Cpu, double RamMb) SampleMySql()
    {
        Process[] procs;
        try { procs = Process.GetProcessesByName("mysqld"); }
        catch { return (false, 0, 0); }

        if (procs.Length == 0)
            return (false, 0, 0);

        double cpu = 0, ramMb = 0;
        try
        {
            foreach (var p in procs)
            {
                cpu += SampleProcessCpu(p);
                ramMb += p.WorkingSet64 / (1024d * 1024d);
            }
        }
        finally
        {
            foreach (var p in procs) p.Dispose();
        }

        return (true, Clamp(cpu), ramMb);
    }

    private double SampleProcessCpu(Process p)
    {
        var now = DateTime.UtcNow;
        TimeSpan cpu;
        try { cpu = p.TotalProcessorTime; }
        catch { return 0; }

        if (!_procBaseline.TryGetValue(p.Id, out var prev))
        {
            _procBaseline[p.Id] = (cpu, now);
            return 0;
        }

        var dCpuMs = (cpu - prev.Cpu).TotalMilliseconds;
        var dWallMs = (now - prev.At).TotalMilliseconds;
        _procBaseline[p.Id] = (cpu, now);

        if (dWallMs <= 0)
            return 0;
        return Clamp(dCpuMs / (dWallMs * _cpuCount) * 100.0);
    }

    private static double Clamp(double v) => v < 0 ? 0 : v > 100 ? 100 : v;

    private static ulong ToU(FILETIME ft) => ((ulong)(uint)ft.dwHighDateTime << 32) | (uint)ft.dwLowDateTime;

    // ===== Win32 =====

    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME
    {
        public int dwLowDateTime;
        public int dwHighDateTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out FILETIME idleTime, out FILETIME kernelTime, out FILETIME userTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);
}
