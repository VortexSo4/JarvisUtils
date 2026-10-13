using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using JarvisUtils.Core;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;

namespace JarvisUtils.Modules.Widgets;

/// <summary>
/// API, доступное скрипту виджета (члены этого класса — «глобальные» имена скрипта).
/// Скрипт выполняется ОДИН раз при загрузке и регистрирует таймеры/обработчики:
/// <code>
/// Every(1, () => Data["left"] = (deadline - Now).ToString(@"mm\:ss"));
/// On("start", () => running = true);   // кнопка x:Name="start" или Command=Invoke
/// </code>
/// Всё выполняется на UI-потоке: долгие операции — через Task.Run.
/// </summary>
public sealed class WidgetApi
{
    private const int SlowHandlerMs = 60;

    private readonly WidgetContext _context;
    private readonly WidgetModel _model;
    private readonly Action _save;
    private readonly IJarvisServices _services;
    private readonly Action<string> _reportError;
    private readonly List<DispatcherTimer> _timers = [];
    private readonly List<Action> _cleanup = [];

    internal WidgetApi(WidgetContext context, FrameworkElement root, WidgetModel model, IJarvisServices services, Action save, Action<string> reportError)
    {
        _context = context;
        Root = root;
        _model = model;
        _services = services;
        _save = save;
        _reportError = reportError;
    }

    /// <summary>Корневой элемент XAML виджета.</summary>
    public FrameworkElement Root { get; }
    /// <summary>Значения для привязок {Binding Data[key]}.</summary>
    public ObservableData Data => _context.Data;
    public DateTime Now => DateTime.Now;
    /// <summary>Последний снимок системных метрик (Cpu, RamPercent, NetDownKBps…).</summary>
    public SystemSnapshot Sys
    {
        get { _context.EnableMetrics(); return SystemMetrics.Current; }
    }

    /// <summary>Элемент XAML по x:Name.</summary>
    public T? Find<T>(string name) where T : class => Root.FindName(name) as T ?? LogicalTreeHelper.FindLogicalNode(Root, name) as T;

    /// <summary>Повторять действие каждые seconds секунд (первый вызов — сразу).</summary>
    public void Every(double seconds, Action action)
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(Math.Max(0.05, seconds)) };
        timer.Tick += (_, _) => Guard("Every", action, timer);
        _timers.Add(timer);
        timer.Start();
        Guard("Every", action, timer);
    }

    /// <summary>
    /// Обработчик по имени: клик по кнопке с x:Name=name (если есть)
    /// и Command="{Binding Invoke}" CommandParameter="name".
    /// </summary>
    public void On(string name, Action action)
    {
        _context.Handlers[name] = _ => Guard(name, action, null);
        if (Find<ButtonBase>(name) is { } button)
        {
            RoutedEventHandler click = (_, _) => Guard(name, action, null);
            button.Click += click;
            _cleanup.Add(() => button.Click -= click);
        }
    }

    public void Run(string target) => _context.RunTarget(target);
    public void Copy(string text) => _services.Clipboard.SetText(text);
    /// <summary>Напечатать текст в активное окно.</summary>
    public void Type(string text) => _services.Input.SendText(text);
    public void Toast(string message) => _services.Overlay.ShowToast(message, _model.Name);
    public void Log(string message) => Core.Log.Info($"виджет «{_model.Name}»: {message}");

    /// <summary>Сохранить значение между перезапусками.</summary>
    public void Save(string key, object? value)
    {
        if (value == null) _model.Store.Remove(key);
        else _model.Store[key] = Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "";
        _save();
    }

    public string? Load(string key) => _model.Store.TryGetValue(key, out var v) ? v : null;
    public string Load(string key, string fallback) => Load(key) ?? fallback;
    public double LoadNumber(string key, double fallback = 0) =>
        double.TryParse(Load(key), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : fallback;

    private void Guard(string where, Action action, DispatcherTimer? timer)
    {
        var sw = Stopwatch.StartNew();
        try { action(); }
        catch (Exception ex)
        {
            timer?.Stop(); // падающий таймер не спамит ошибками каждую секунду
            _reportError($"{where}: {ex.GetBaseException().Message}");
            return;
        }
        // UI-поток держит хуки клавиатуры — тормозящий скрипт тормозит ввод всей системы
        if (sw.ElapsedMilliseconds > SlowHandlerMs)
            Core.Log.Warn($"виджет «{_model.Name}»: {where} занял {sw.ElapsedMilliseconds} мс — выносите тяжёлое в Task.Run");
    }

    internal void Dispose()
    {
        foreach (var t in _timers) t.Stop();
        _timers.Clear();
        foreach (var c in _cleanup) c();
        _cleanup.Clear();
    }
}

/// <summary>Компиляция скриптов виджетов: в фоне, с кешем по тексту.</summary>
internal static class WidgetScriptCompiler
{
    // Сборки Roslyn-скриптов не выгружаются — кеш не даёт плодить их при перезагрузке виджетов
    private static readonly ConcurrentDictionary<string, Task<ScriptRunner<object>>> Cache = new();
    private static ScriptOptions? _options;

    public static Task<ScriptRunner<object>> CompileAsync(string code) =>
        Cache.GetOrAdd(code, c => Task.Run(() =>
        {
            var script = CSharpScript.Create(c, Options, typeof(WidgetApi));
            var errors = script.Compile().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
            if (errors.Count > 0)
            {
                Cache.TryRemove(c, out _);
                throw new WidgetScriptException(string.Join("\n", errors.Take(5).Select(Describe)));
            }
            return script.CreateDelegate();
        }));

    private static string Describe(Diagnostic d)
    {
        var line = d.Location.GetLineSpan().StartLinePosition;
        return $"строка {line.Line + 1}: {d.GetMessage(System.Globalization.CultureInfo.GetCultureInfo("ru-RU"))}";
    }

    private static ScriptOptions Options => _options ??= ScriptOptions.Default
        .WithReferences(AppDomain.CurrentDomain.GetAssemblies().Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location)))
        .WithImports("System", "System.Linq", "System.Collections.Generic", "System.IO", "System.Threading.Tasks",
            "System.Windows", "System.Windows.Controls", "System.Windows.Media", "JarvisUtils.Modules.Widgets");
}

public sealed class WidgetScriptException(string message) : Exception(message);
