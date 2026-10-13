using System;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Threading;

namespace JarvisUtils.Modules.Widgets;

/// <summary>Снимок системных метрик (раз в секунду).</summary>
public sealed record SystemSnapshot(
    double Cpu,          // загрузка CPU, %
    double RamPercent,   // занято памяти, %
    double RamUsedGb,
    double RamTotalGb,
    double NetDownKBps,
    double NetUpKBps,
    double DiskFreeGb,   // свободно на системном диске
    int Battery,         // заряд, %; -1 — батареи нет
    TimeSpan Uptime)
{
    public static readonly SystemSnapshot Empty = new(0, 0, 0, 0, 0, 0, 0, -1, TimeSpan.Zero);
}

/// <summary>
/// Общий сэмплер метрик для всех виджетов. Работает в фоне и только пока есть
/// подписчики: без «своих» виджетов поток не крутится вовсе.
/// CPU — через GetSystemTimes (PerformanceCounter инициализируется секундами и тяжёл).
/// </summary>
public static class SystemMetrics
{
    private static readonly object Gate = new();
    private static int _subscribers;
    private static Thread? _thread;
    private static event Action<SystemSnapshot>? UpdatedCore;

    public static SystemSnapshot Current { get; private set; } = SystemSnapshot.Empty;

    /// <summary>Событие приходит с фонового потока.</summary>
    public static event Action<SystemSnapshot> Updated
    {
        add
        {
            lock (Gate)
            {
                UpdatedCore += value;
                if (++_subscribers == 1 && _thread == null)
                {
                    _thread = new Thread(Loop) { IsBackground = true, Name = "JarvisUtils.Metrics", Priority = ThreadPriority.BelowNormal };
                    _thread.Start();
                }
            }
        }
        remove
        {
            lock (Gate)
            {
                UpdatedCore -= value;
                _subscribers = Math.Max(0, _subscribers - 1);
            }
        }
    }

    private static void Loop()
    {
        long prevIdle = 0, prevTotal = 0, prevRx = 0, prevTx = 0;
        long prevTick = Environment.TickCount64;
        double diskFree = 0;
        int iteration = 0;

        while (true)
        {
            lock (Gate)
            {
                if (_subscribers == 0) { _thread = null; return; }
            }

            try
            {
                long now = Environment.TickCount64;
                double seconds = Math.Max(0.001, (now - prevTick) / 1000.0);
                prevTick = now;

                double cpu = 0;
                if (GetSystemTimes(out var idleFt, out var kernelFt, out var userFt))
                {
                    long idle = idleFt.Value, total = kernelFt.Value + userFt.Value; // kernel включает idle
                    if (prevTotal != 0 && total > prevTotal)
                        cpu = 100.0 * (1.0 - (double)(idle - prevIdle) / (total - prevTotal));
                    prevIdle = idle;
                    prevTotal = total;
                }

                var mem = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
                double ramTotal = 0, ramUsed = 0, ramPercent = 0;
                if (GlobalMemoryStatusEx(ref mem))
                {
                    ramTotal = mem.ullTotalPhys / 1073741824.0;
                    ramUsed = (mem.ullTotalPhys - mem.ullAvailPhys) / 1073741824.0;
                    ramPercent = mem.dwMemoryLoad;
                }

                long rx = 0, tx = 0;
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces()
                             .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback))
                {
                    var stats = nic.GetIPStatistics();
                    rx += stats.BytesReceived;
                    tx += stats.BytesSent;
                }
                double down = prevRx == 0 ? 0 : Math.Max(0, rx - prevRx) / 1024.0 / seconds;
                double up = prevTx == 0 ? 0 : Math.Max(0, tx - prevTx) / 1024.0 / seconds;
                prevRx = rx;
                prevTx = tx;

                if (iteration++ % 10 == 0) // диск меняется медленно
                {
                    var root = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
                    diskFree = new DriveInfo(root).AvailableFreeSpace / 1073741824.0;
                }

                int battery = -1;
                if (GetSystemPowerStatus(out var power) && power.BatteryFlag != 128 && power.BatteryLifePercent <= 100)
                    battery = power.BatteryLifePercent;

                var snapshot = new SystemSnapshot(Math.Clamp(cpu, 0, 100), ramPercent, ramUsed, ramTotal, down, up, diskFree,
                    battery, TimeSpan.FromMilliseconds(Environment.TickCount64));
                Current = snapshot;
                UpdatedCore?.Invoke(snapshot);
            }
            catch (Exception ex) { Core.Log.Warn($"метрики: {ex.Message}"); }

            Thread.Sleep(1000);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME
    {
        public uint Low, High;
        public long Value => ((long)High << 32) | Low;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public int BatteryLifeTime, BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")] private static extern bool GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user);
    [DllImport("kernel32.dll")] private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);
    [DllImport("kernel32.dll")] private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);
}
