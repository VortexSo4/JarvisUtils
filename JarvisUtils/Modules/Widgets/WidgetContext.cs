using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Input;
using System.Windows.Threading;
using JarvisUtils.Core;

namespace JarvisUtils.Modules.Widgets;

/// <summary>
/// DataContext XAML «своего» виджета: живые данные + команды.
/// В XAML: {Binding Now, StringFormat=HH:mm}, {Binding Cpu}, {Binding Data[count]},
/// Command="{Binding Run}" CommandParameter="calc.exe".
/// </summary>
public sealed class WidgetContext : INotifyPropertyChanged, IDisposable
{
    private readonly IJarvisServices _services;
    private readonly DispatcherTimer _clock;
    private readonly Dispatcher _ui;
    private SystemSnapshot _sys = SystemMetrics.Current;
    private bool _metrics;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Обработчики On(name, …) из скрипта — для команды Invoke.</summary>
    internal Dictionary<string, Action<object?>> Handlers { get; } = new(StringComparer.OrdinalIgnoreCase);

    public WidgetContext(IJarvisServices services)
    {
        _services = services;
        _ui = Dispatcher.CurrentDispatcher;
        Run = new RelayCommand(p => RunTarget(p?.ToString()));
        Copy = new RelayCommand(p => { if (p != null) _services.Clipboard.SetText(p.ToString()!); });
        Type = new RelayCommand(p => { if (p != null) _services.Input.SendText(p.ToString()!); });
        Invoke = new RelayCommand(p =>
        {
            if (p?.ToString() is { } name && Handlers.TryGetValue(name, out var handler)) handler(null);
        });

        // тик ровно на границе секунды — часы с секундами не «плывут» относительно системных
        _clock = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(1000 - DateTime.Now.Millisecond + 5) };
        _clock.Tick += (_, _) =>
        {
            _clock.Interval = TimeSpan.FromMilliseconds(1000 - DateTime.Now.Millisecond + 5);
            Raise(nameof(Now));
        };
        _clock.Start();
    }

    /// <summary>Подписаться на метрики, только если XAML/скрипт ими пользуется.</summary>
    internal void EnableMetrics()
    {
        if (_metrics) return;
        _metrics = true;
        SystemMetrics.Updated += OnMetrics;
    }

    private void OnMetrics(SystemSnapshot s) => _ui.BeginInvoke(() =>
    {
        _sys = s;
        Raise(null); // все свойства разом — дешевле, чем по одному
    }, DispatcherPriority.Background);

    // ── данные ──
    public DateTime Now => DateTime.Now;
    public double Cpu => _sys.Cpu;
    public double CpuFraction => _sys.Cpu / 100.0;
    public double Ram => _sys.RamPercent;
    public double RamFraction => _sys.RamPercent / 100.0;
    public double RamUsedGb => _sys.RamUsedGb;
    public double RamTotalGb => _sys.RamTotalGb;
    public double NetDown => _sys.NetDownKBps;
    public double NetUp => _sys.NetUpKBps;
    public double DiskFreeGb => _sys.DiskFreeGb;
    public int Battery => _sys.Battery;
    public TimeSpan Uptime => _sys.Uptime;
    public ObservableData Data { get; } = new();

    // ── команды ──
    public ICommand Run { get; }
    public ICommand Copy { get; }
    public ICommand Type { get; }
    public ICommand Invoke { get; }

    internal void RunTarget(string? target)
    {
        if (string.IsNullOrWhiteSpace(target)) return;
        try
        {
            // «программа аргументы» или путь/URL целиком — shell сам разберётся
            target = target.Trim();
            string file = target, args = "";
            if (!target.StartsWith('"') && !System.IO.File.Exists(target) && !target.Contains("://"))
            {
                int space = target.IndexOf(' ');
                if (space > 0) { file = target[..space]; args = target[(space + 1)..]; }
            }
            Process.Start(new ProcessStartInfo(file.Trim('"'), args) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn($"виджет: запуск «{target}» не удался: {ex.Message}");
            _services.Overlay.ShowToast($"Не удалось запустить: {target}", "Виджеты");
        }
    }

    private void Raise(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public void Dispose()
    {
        _clock.Stop();
        if (_metrics) SystemMetrics.Updated -= OnMetrics;
        _metrics = false;
        Handlers.Clear();
    }
}

/// <summary>Словарь с уведомлениями — привязка {Binding Data[key]} обновляется при записи.</summary>
public sealed class ObservableData : INotifyPropertyChanged
{
    private readonly Dictionary<string, object?> _values = new(StringComparer.OrdinalIgnoreCase);

    public event PropertyChangedEventHandler? PropertyChanged;

    public object? this[string key]
    {
        get => _values.TryGetValue(key, out var v) ? v : null;
        set
        {
            if (_values.TryGetValue(key, out var old) && Equals(old, value)) return;
            _values[key] = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        }
    }

    public bool Contains(string key) => _values.ContainsKey(key);
}

public sealed class RelayCommand(Action<object?> execute) : ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter)
    {
        try { execute(parameter); }
        catch (Exception ex) { Log.Error($"виджет: команда: {ex}"); }
    }
}
