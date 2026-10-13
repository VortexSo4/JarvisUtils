using System;
using System.Drawing;
using System.Windows.Forms;
using JarvisUtils.Core;
using JarvisUtils.UI.Settings;

namespace JarvisUtils.UI.Tray;

public sealed class TrayHost : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly ITrayMenuBuilder _trayMenu; // действия модулей (тумблеры модулей — в настройках, не в трее)
    private readonly ModuleRunner _runner;
    private readonly ContextMenuStrip _menu;
    private readonly Icon _icon;
    private SettingsWindow? _settingsWindow;

    public TrayHost(ITrayMenuBuilder trayMenu, ModuleRunner runner)
    {
        _trayMenu = trayMenu;
        _runner = runner;

        _icon = LoadIcon();
        _notifyIcon = new NotifyIcon { Icon = _icon, Text = "JarvisUtils", Visible = true };

        // ── Меню: только "Выход" ──
        _menu = new ContextMenuStrip
        {
            Renderer = new DarkRenderer(),
            ShowImageMargin = false,
            Font = new Font("Segoe UI", 9.5f)
        };
        // Заполняем меню один раз, без перестроения при каждом открытии
        BuildSimpleMenu();
        _notifyIcon.ContextMenuStrip = _menu;

        // ── Клики по иконке ──
        _notifyIcon.MouseClick += (s, e) =>
        {
            if (e.Button == MouseButtons.Left)
                OpenSettings();
        };
        _notifyIcon.DoubleClick += (_, _) => OpenSettings(); // опционально – оставлено для удобства
    }

    private void BuildSimpleMenu()
    {
        _menu.Items.Clear();
        var fore = Color.FromArgb(230, 235, 245);

        // Действия модулей (новая заметка, закрепить скриншот…). Тумблеры модулей
        // сюда не попадают — меню трея короткое, включение модулей живёт в настройках.
        foreach (var entry in _trayMenu.Entries)
        {
            if (entry is not TrayButtonItem button) continue;
            var item = new ToolStripMenuItem(button.Header) { ForeColor = fore };
            item.Click += (_, _) => button.OnClick();
            _menu.Items.Add(item);
        }
        if (_menu.Items.Count > 0) _menu.Items.Add(new ToolStripSeparator());

        var settings = new ToolStripMenuItem("Настройки") { ForeColor = fore };
        settings.Click += (_, _) => OpenSettings();
        _menu.Items.Add(settings);

        var exit = new ToolStripMenuItem("Выход") { ForeColor = fore };
        exit.Click += (_, _) => System.Windows.Application.Current.Shutdown();
        _menu.Items.Add(exit);
    }

    /// <summary>Та же иконка, что у exe и окон (App/JarvisUtils.ico), в размере для трея.</summary>
    private static Icon LoadIcon()
    {
        var info = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/JarvisUtils;component/App/JarvisUtils.ico"));
        using var stream = info!.Stream;
        return new Icon(stream, SystemInformation.SmallIconSize);
    }

    public void OpenSettings()
    {
        if (_settingsWindow == null || !_settingsWindow.IsLoaded)
        {
            _settingsWindow = new SettingsWindow(_runner);
            _settingsWindow.Show();
            _settingsWindow.Activate();
        }
        else _settingsWindow.Activate();
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _icon.Dispose();
    }

    private sealed class DarkRenderer() : ToolStripProfessionalRenderer(new DarkColors())
    {
        private sealed class DarkColors : ProfessionalColorTable
        {
            private static readonly Color Bg = Color.FromArgb(24, 26, 34);
            private static readonly Color Line = Color.FromArgb(40, 44, 58);
            private static readonly Color Sel = Color.FromArgb(38, 42, 54);
            public override Color ToolStripDropDownBackground => Bg;
            public override Color MenuBorder => Line;
            public override Color MenuItemBorder => Line;
            public override Color MenuItemSelected => Sel;
            public override Color MenuItemSelectedGradientBegin => Sel;
            public override Color MenuItemSelectedGradientEnd => Sel;
            public override Color MenuItemPressedGradientBegin => Sel;
            public override Color MenuItemPressedGradientEnd => Sel;
            public override Color SeparatorDark => Line;
            public override Color ImageMarginGradientBegin => Bg;
            public override Color ImageMarginGradientMiddle => Bg;
            public override Color ImageMarginGradientEnd => Bg;
        }
    }
}