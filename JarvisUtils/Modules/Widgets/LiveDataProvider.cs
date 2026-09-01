using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Windows.Input;

namespace JarvisUtils.Modules.Widgets;

public sealed class LiveDataProvider
{
    private PerformanceCounter? _cpuCounter;
    private PerformanceCounter? _ramCounter;
    private float _cpu, _ram;
    private readonly Thread _thread;
    private readonly Dictionary<string, bool> _clicks = new();
    private readonly HashSet<Key> _keysDown = new();

    public DateTime Time => DateTime.Now;
    public float Cpu => _cpu;
    public float Ram => _ram;
    public float Gpu => 0f; // Заглушка, требует специфичных драйверов

    public LiveDataProvider()
    {
        try
        {
            _cpuCounter = new PerformanceCounter("Processor", "% Processor Time", "_Total");
            _ramCounter = new PerformanceCounter("Memory", "Available MBytes");
        }
        catch { /* нет прав или не загружены счётчики */ }

        _thread = new Thread(UpdateLoop) { IsBackground = true, Name = "WidgetLiveData" };
        _thread.Start();
    }

    private void UpdateLoop()
    {
        while (true)
        {
            try
            {
                if (_cpuCounter != null) _cpu = _cpuCounter.NextValue();
                if (_ramCounter != null) _ram = _ramCounter.NextValue();
            }
            catch { }
            Thread.Sleep(1000);
        }
    }

    public void RegisterClick(string elementId)
    {
        _clicks[elementId] = true;
    }

    public bool WasClicked(string elementId)
    {
        if (_clicks.TryGetValue(elementId, out var val) && val)
        {
            _clicks[elementId] = false;
            return true;
        }
        return false;
    }

    public void SetKey(Key key, bool down)
    {
        if (down) _keysDown.Add(key); else _keysDown.Remove(key);
    }

    public bool IsKeyDown(string key)
    {
        return Enum.TryParse(key, true, out Key k) && _keysDown.Contains(k);
    }
}