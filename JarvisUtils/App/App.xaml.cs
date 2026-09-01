using System;
using System.Threading;
using System.Windows;
using JarvisUtils.Core;
using JarvisUtils.Modules.Calculator;
using JarvisUtils.Modules.CaseConverter;
using JarvisUtils.Modules.ClipboardFavorites;
using JarvisUtils.Modules.CodeMerger;
using JarvisUtils.Modules.LayoutFixer;
using JarvisUtils.Modules.PlainTextPaste;
using JarvisUtils.Modules.ScreenshotOcr;
using JarvisUtils.Modules.TextExpander;
using JarvisUtils.Modules.Widgets;
using JarvisUtils.UI.Tray;

namespace JarvisUtils.App;

public partial class App : Application
{
    private Mutex? _mutex;
    private HookManager? _hooks;
    private TrayHost? _tray;
    private ModuleRunner? _runner;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        _mutex = new Mutex(true, "JarvisUtils.SingleInstance", out bool isNew);
        if (!isNew)
        {
            MessageBox.Show("JarvisUtils уже запущен.", "JarvisUtils", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, args) => { Log.Error($"UI: {args.Exception}"); args.Handled = true; };
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log.Error($"AppDomain: {args.ExceptionObject}");
        Log.Info("JarvisUtils запущен");

        // ── Инфраструктура Core ──
        _hooks = new HookManager();
        _hooks.Install(); // UI-поток: коллбэки хуков приходят сюда же

        var hotkeys = new HotkeyManager();
        hotkeys.Initialize();

        var input = new InputSimulator();
        var services = new JarvisServices(
            hotkeys,
            new ClipboardService(),
            input,
            new SelectionService(input),
            new OverlayService(hotkeys, _hooks),
            new SettingsStore(),
            new TrayMenuRegistry(),
            _hooks);

        // ── Модули (новый модуль = один класс + строка здесь) ──
        _runner = new ModuleRunner(services);
        _runner.Start([
            new PlainTextPasteModule(),
            new LayoutFixerModule(),
            new CaseConverterModule(),
            new TextExpanderModule(),
            new ClipboardFavoritesModule(),
            new ScreenshotOcrModule(),
            new CodeMergerModule(),
            new CalculatorModule(),
            new WidgetsModule()
        ]);

        _tray = new TrayHost(services.TrayMenu, _runner);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            _runner?.Shutdown();
            _tray?.Dispose();
            _hooks?.Dispose();
            _mutex?.ReleaseMutex();
        }
        catch { }
        Log.Flush();
        base.OnExit(e);
    }
}