using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shell;
using JarvisUtils.Core;
using Microsoft.Win32;

namespace JarvisUtils.Modules.CodeMerger;

// ─── Настройки ──────────────────────────────────────────────────────
public sealed class CodeMergerSettings
{
    public string Hotkey { get; set; } = "Ctrl+Alt+M";
    public List<string> Patterns { get; set; } = ["*.cs", "*.xaml"];
    public List<string> ExcludeDirs { get; set; } = ["bin", "obj", ".git", ".vs"];
    public string OutputFileName { get; set; } = "merged_output.txt";
    public bool Recursive { get; set; } = true;
    public bool IncludeHeaders { get; set; } = true;
    public int MaxFileKb { get; set; } = 512;
    public List<string> RecentDirs { get; set; } = [];
}

internal sealed record MergeResult(string OutputPath, int FileCount, long TotalBytes);

// ─── Основной модуль ────────────────────────────────────────────────
public sealed class CodeMergerModule : JarvisModuleBase, IHotkeyConfigurable, IModuleSettingsEditor
{
    private const int MaxHistory = 12;

    private CodeMergerSettings _settings = null!;
    private MergeLauncherWindow? _launcher;

    public override string DisplayName => "Слияние исходников";

    public override void Initialize(IJarvisServices services)
    {
        base.Initialize(services);
    }

    protected override void RegisterHotkeys()
    {
        if (_settings == null) return;
        RegisterHotkey(_settings.Hotkey, ShowLauncher);
    }

    protected override void OnEnabled()
    {
        _settings = LoadSettings<CodeMergerSettings>();
        _settings.Patterns ??= ["*.cs", "*.xaml"];
        _settings.ExcludeDirs ??= ["bin", "obj", ".git", ".vs"];
        _settings.RecentDirs ??= [];
        if (string.IsNullOrWhiteSpace(_settings.OutputFileName))
            _settings.OutputFileName = "merged_output.txt";

        RestartHotkeys();
    }

    protected override void OnDisabled()
    {
        _launcher?.ForceClose();
    }

    public override void Shutdown()
    {
        _launcher?.ForceClose();
        base.Shutdown();
    }

    public IReadOnlyList<HotkeySlot> HotkeySlots =>
    [
        new("Открыть окно слияния",
            () => _settings.Hotkey,
            g => { _settings.Hotkey = g; SaveSettings(_settings); RestartHotkeys(); })
    ];

    private void ShowLauncher()
    {
        _launcher?.ForceClose();
        var window = new MergeLauncherWindow(this);
        _launcher = window;
        window.Closed += (_, _) => { if (ReferenceEquals(_launcher, window)) _launcher = null; };
        window.Show();
    }

    internal IReadOnlyList<string> RecentDirs => _settings.RecentDirs;

    internal void PushRecent(string directory)
    {
        var norm = Normalize(directory);
        _settings.RecentDirs.RemoveAll(d => string.Equals(Normalize(d), norm, StringComparison.OrdinalIgnoreCase));
        _settings.RecentDirs.Insert(0, norm);
        if (_settings.RecentDirs.Count > MaxHistory)
            _settings.RecentDirs.RemoveRange(MaxHistory, _settings.RecentDirs.Count - MaxHistory);
        SaveSettings(_settings);
    }

    internal void RemoveRecent(string directory)
    {
        var norm = Normalize(directory);
        _settings.RecentDirs.RemoveAll(d => string.Equals(Normalize(d), norm, StringComparison.OrdinalIgnoreCase));
        SaveSettings(_settings);
    }

    private static string Normalize(string d) =>
        Path.GetFullPath(d).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    internal MergeResult? MergeFiles(string directory)
    {
        try
        {
            directory = Path.GetFullPath(directory);
            var outName = string.IsNullOrWhiteSpace(_settings.OutputFileName) ? "merged_output.txt" : _settings.OutputFileName.Trim();
            
            // Сохраняем в AppData\Merges
            var mergeDir = Path.Combine(AppPaths.DataDir, "Merges");
            Directory.CreateDirectory(mergeDir);
            var outputFileName = GenerateOutputFileName(directory, outName);
            var output = Path.Combine(mergeDir, outputFileName);
            
            var option = _settings.Recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;

            var files = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pattern in _settings.Patterns.Where(p => !string.IsNullOrWhiteSpace(p)))
            {
                try
                {
                    foreach (var f in Directory.EnumerateFiles(directory, pattern.Trim(), option))
                        if (!IsExcluded(directory, f))
                            files.Add(Path.GetFullPath(f));
                }
                catch (Exception ex) { Log.Warn($"merge: перебор «{pattern}»: {ex.Message}"); }
            }
            files.Remove(output);

            // 0 => безлимит
            long limitBytes = _settings.MaxFileKb > 0 ? _settings.MaxFileKb * 1024L : long.MaxValue;
            var sb = new StringBuilder();
            var count = 0;
            long bytes = 0;
            foreach (var file in files)
            {
                string content;
                try
                {
                    var info = new FileInfo(file);
                    if (info.Length > limitBytes) continue;
                    content = File.ReadAllText(file);
                    bytes += info.Length;
                }
                catch { continue; }

                if (_settings.IncludeHeaders)
                    sb.Append("[file: ").Append(Path.GetRelativePath(directory, file)).Append("]").Append('\n');
                sb.Append(content.TrimEnd('\r', '\n')).Append('\n').Append('\n');
                count++;
            }

            if (count == 0) return null;
            File.WriteAllText(output, sb.ToString(), new UTF8Encoding(true));
            Log.Info($"merge: {count} файлов → {output}");
            return new MergeResult(output, count, bytes);
        }
        catch (Exception ex)
        {
            Log.Error($"merge: {ex}");
            return null;
        }
    }

    private string GenerateOutputFileName(string directory, string baseName)
    {
        var dirName = new DirectoryInfo(directory).Name;
        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var ext = Path.GetExtension(baseName);
        var nameWithoutExt = Path.GetFileNameWithoutExtension(baseName);
        return $"{dirName}_{timestamp}_{nameWithoutExt}{ext}";
    }

    private bool IsExcluded(string root, string file)
    {
        var rel = Path.GetRelativePath(root, file);
        var segments = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        for (int i = 0; i < segments.Length - 1; i++)
            foreach (var ex in _settings.ExcludeDirs)
                if (!string.IsNullOrWhiteSpace(ex) && string.Equals(segments[i], ex.Trim(), StringComparison.OrdinalIgnoreCase))
                    return true;
        return false;
    }

    // ─── Редактор настроек (IModuleSettingsEditor) ──────────────────────
    public FrameworkElement BuildEditor()
    {
        var root = new StackPanel { Margin = new Thickness(2, 4, 2, 2) };

        root.Children.Add(new TextBlock
        {
            Text = "Склеивает файлы проекта в один текстовый файл с заголовками [file: …]. ",
            Foreground = JarvisTheme.ForegroundBrush(0.55),
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 4)
        });

        root.Children.Add(Caption("Шаблоны файлов"));
        root.Children.Add(new ChipsListEditor(
            "напр. *.cs, Enter — добавить",
            () => _settings.Patterns,
            v => { _settings.Patterns = v; SaveSettings(_settings); }).Root);

        root.Children.Add(Caption("Исключаемые папки"));
        root.Children.Add(new ChipsListEditor(
            "напр. bin, Enter — добавить",
            () => _settings.ExcludeDirs,
            v => { _settings.ExcludeDirs = v; SaveSettings(_settings); }).Root);

        var nameBox = DarkBox(_settings.OutputFileName);
        nameBox.TextChanged += (_, _) => { _settings.OutputFileName = nameBox.Text.Trim(); SaveSettings(_settings); };
        root.Children.Add(TwoColumns("Имя итогового файла", nameBox, new Thickness(0, 12, 0, 0)));

        var limitBox = DarkBox(_settings.MaxFileKb.ToString());
        limitBox.Width = 80;
        limitBox.HorizontalAlignment = HorizontalAlignment.Left;
        limitBox.PreviewTextInput += (_, e) => e.Handled = !e.Text.All(char.IsDigit);
        limitBox.TextChanged += (_, _) =>
        {
            if (int.TryParse(limitBox.Text, out var kb) && kb >= 0)
            {
                _settings.MaxFileKb = kb;
                SaveSettings(_settings);
            }
        };
        root.Children.Add(TwoColumns("Лимит размера файла, КБ (0 = безлимит)", limitBox, new Thickness(0, 8, 0, 0)));

        root.Children.Add(Check("Обходить подкаталоги",
            _settings.Recursive, v => { _settings.Recursive = v; SaveSettings(_settings); }));
        root.Children.Add(Check("Заголовки [file: …] перед каждым файлом",
            _settings.IncludeHeaders, v => { _settings.IncludeHeaders = v; SaveSettings(_settings); }));

        return root;
    }

    private static TextBlock Caption(string text) => new()
    {
        Text = text,
        Foreground = JarvisTheme.ForegroundBrush(0.55),
        FontSize = 11,
        Margin = new Thickness(0, 10, 0, 0)
    };

    private static Grid TwoColumns(string label, Control editor, Thickness margin)
    {
        var grid = new Grid { Margin = margin };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var text = new TextBlock
        {
            Text = label,
            Foreground = JarvisTheme.ForegroundBrush(0.75),
            FontSize = 12.5,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetColumn(text, 0);
        Grid.SetColumn(editor, 1);
        grid.Children.Add(text);
        grid.Children.Add(editor);
        return grid;
    }

    private static CheckBox Check(string label, bool value, Action<bool> set)
    {
        var box = new CheckBox
        {
            Content = label,
            IsChecked = value,
            Foreground = JarvisTheme.ForegroundBrush(0.85),
            FontSize = 12.5,
            Margin = new Thickness(0, 10, 0, 0),
            Cursor = Cursors.Hand
        };
        box.Click += (_, _) => set(box.IsChecked == true);
        return box;
    }

    private static TextBox DarkBox(string text) => new()
    {
        Text = text,
        Foreground = JarvisTheme.ForegroundBrush(0.92),
        Background = JarvisTheme.Solid(Color.FromRgb(0x0B, 0x0D, 0x13), 0.85),
        BorderBrush = JarvisTheme.Solid(JarvisTheme.Accent, 0.25),
        BorderThickness = new Thickness(1),
        CaretBrush = JarvisTheme.AccentBrush,
        SelectionBrush = JarvisTheme.Solid(JarvisTheme.Accent, 0.35),
        FontSize = 12.5,
        Padding = new Thickness(7, 5, 7, 5)
    };
}

// ─── Редактор списка «чипсов» ─────────────────────────────────────
internal sealed class ChipsListEditor
{
    private readonly Func<List<string>> _get;
    private readonly Action<List<string>> _set;
    private readonly WrapPanel _chips = new() { Margin = new Thickness(0, 3, 0, 5) };
    private readonly TextBox _addBox;
    private readonly TextBlock _watermark;

    public FrameworkElement Root { get; }

    public ChipsListEditor(string placeholder, Func<List<string>> get, Action<List<string>> set)
    {
        _get = get;
        _set = set;

        _watermark = new TextBlock
        {
            Text = placeholder,
            Foreground = JarvisTheme.ForegroundBrush(0.35),
            FontSize = 12.5,
            IsHitTestVisible = false,
            Margin = new Thickness(9, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };

        _addBox = new TextBox
        {
            Foreground = JarvisTheme.ForegroundBrush(0.92),
            Background = JarvisTheme.Solid(Color.FromRgb(0x0B, 0x0D, 0x13), 0.85),
            BorderBrush = JarvisTheme.Solid(JarvisTheme.Accent, 0.25),
            BorderThickness = new Thickness(1),
            CaretBrush = JarvisTheme.AccentBrush,
            SelectionBrush = JarvisTheme.Solid(JarvisTheme.Accent, 0.35),
            FontSize = 12.5,
            Padding = new Thickness(7, 5, 7, 5)
        };
        _addBox.TextChanged += (_, _) =>
            _watermark.Visibility = string.IsNullOrEmpty(_addBox.Text) ? Visibility.Visible : Visibility.Collapsed;
        _addBox.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { e.Handled = true; Add(_addBox.Text); }
        };

        var field = new Grid();
        field.Children.Add(_watermark);
        field.Children.Add(_addBox);

        var addBtn = CodeMergerUi.Flat(new TextBlock
        {
            Text = "Добавить",
            FontSize = 12,
            Foreground = JarvisTheme.ForegroundBrush(0.9)
        });
        addBtn.Padding = new Thickness(12, 5, 12, 5);
        addBtn.Margin = new Thickness(6, 0, 0, 0);
        addBtn.Background = new SolidColorBrush(CodeMergerUi.Tint(JarvisTheme.Accent, 0x29));
        CodeMergerUi.HoverTint(addBtn, CodeMergerUi.Tint(JarvisTheme.Accent, 0x29), CodeMergerUi.Tint(JarvisTheme.Accent, 0x4D));
        addBtn.Click += (_, _) => Add(_addBox.Text);

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(field, 0);
        Grid.SetColumn(addBtn, 1);
        row.Children.Add(field);
        row.Children.Add(addBtn);

        var panel = new StackPanel();
        panel.Children.Add(_chips);
        panel.Children.Add(row);
        Root = panel;

        Rebuild();
    }

    private void Rebuild()
    {
        _chips.Children.Clear();
        var values = _get().Where(v => !string.IsNullOrWhiteSpace(v))
                            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (values.Count == 0)
        {
            _chips.Children.Add(new TextBlock
            {
                Text = "— ничего не задано —",
                Foreground = JarvisTheme.ForegroundBrush(0.35),
                FontSize = 11.5,
                FontStyle = FontStyles.Italic,
                Margin = new Thickness(2, 3, 0, 3)
            });
            return;
        }
        foreach (var v in values) _chips.Children.Add(BuildChip(v));
    }

    private UIElement BuildChip(string value)
    {
        var remove = new TextBlock
        {
            Text = "✕",
            FontSize = 10.5,
            Foreground = JarvisTheme.ForegroundBrush(0.45),
            Margin = new Thickness(7, 0, 1, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = Cursors.Hand,
            ToolTip = "Убрать"
        };
        remove.MouseEnter += (_, _) => remove.Foreground = JarvisTheme.AccentBrush;
        remove.MouseLeave += (_, _) => remove.Foreground = JarvisTheme.ForegroundBrush(0.45);
        remove.MouseLeftButtonUp += (_, e) => { e.Handled = true; Remove(value); };

        var text = new TextBlock
        {
            Text = value,
            FontSize = 12.5,
            Foreground = JarvisTheme.ForegroundBrush(0.92),
            MaxWidth = 220,
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = value
        };

        var brush = new SolidColorBrush(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));
        var chip = new Border
        {
            Background = brush,
            BorderBrush = JarvisTheme.Solid(JarvisTheme.Accent, 0.30),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(11),
            Padding = new Thickness(10, 4, 7, 4),
            Margin = new Thickness(0, 0, 6, 6),
            Child = new StackPanel { Orientation = Orientation.Horizontal }
        };
        ((StackPanel)chip.Child).Children.Add(text);
        ((StackPanel)chip.Child).Children.Add(remove);
        chip.MouseEnter += (_, _) => brush.BeginAnimation(SolidColorBrush.ColorProperty,
            new ColorAnimation(CodeMergerUi.Tint(JarvisTheme.Accent, 0x26), TimeSpan.FromMilliseconds(90)));
        chip.MouseLeave += (_, _) => brush.BeginAnimation(SolidColorBrush.ColorProperty,
            new ColorAnimation(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF), TimeSpan.FromMilliseconds(130)));
        return chip;
    }

    private void Add(string? raw)
    {
        var value = raw?.Trim();
        if (string.IsNullOrEmpty(value)) return;
        var list = _get().Where(v => !string.IsNullOrWhiteSpace(v)).ToList();
        if (list.Any(v => string.Equals(v, value, StringComparison.OrdinalIgnoreCase))) { _addBox.Clear(); return; }
        list.Add(value);
        _set(list);
        _addBox.Clear();
        Rebuild();
    }

    private void Remove(string value)
    {
        _set(_get().Where(v => !string.Equals(v, value, StringComparison.OrdinalIgnoreCase)).ToList());
        Rebuild();
    }
}

// ─── Общий UI-хелпер для модуля ──────────────────────────────────
internal static class CodeMergerUi
{
    public static Color Tint(Color c, byte alpha) => Color.FromArgb(alpha, c.R, c.G, c.B);

    public static Button Flat(object content, string? tooltip = null, double width = double.NaN, double height = double.NaN, double fontSize = 14)
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
        border.SetBinding(Border.BackgroundProperty, new Binding("Background") { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) });
        border.SetBinding(Border.PaddingProperty, new Binding("Padding") { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) });
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter);

        return new Button
        {
            Template = new ControlTemplate(typeof(Button)) { VisualTree = border },
            Content = content,
            FontSize = fontSize,
            Width = width,
            Height = height,
            Focusable = false,
            IsTabStop = false,
            Cursor = Cursors.Hand,
            Background = new SolidColorBrush(Colors.Transparent),
            Foreground = JarvisTheme.ForegroundBrush(0.85),
            ToolTip = tooltip
        };
    }

    public static void HoverTint(Button btn, Color idle, Color hover)
    {
        if (btn.Background is not SolidColorBrush brush || brush.IsFrozen) return;
        btn.MouseEnter += (_, _) => brush.BeginAnimation(SolidColorBrush.ColorProperty,
            new ColorAnimation(hover, TimeSpan.FromMilliseconds(90)));
        btn.MouseLeave += (_, _) => brush.BeginAnimation(SolidColorBrush.ColorProperty,
            new ColorAnimation(idle, TimeSpan.FromMilliseconds(130)));
    }
}

// ─── Окно запуска слияния ────────────────────────────────────────
internal sealed class MergeLauncherWindow : Window
{
    private const int MaxRows = 9;
    private static readonly Color RowSelected = Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF);
    private const string DefaultHint = "Enter — склеить · ↑↓ — история · Esc — закрыть";

    private readonly CodeMergerModule _module;
    private readonly TextBox _input;
    private readonly TextBlock _watermark;
    private readonly TextBlock _statusHint;
    private readonly StackPanel _historyPanel;
    private readonly Border _historyHost;
    private readonly Button _folderBtn;
    private readonly Button _historyBtn;
    private readonly Button _runBtn;
    private readonly TranslateTransform _slide = new();
    private readonly List<SolidColorBrush> _rowBrushes = [];

    private List<string> _filtered = [];
    private int _listSel = -1;
    private bool _showAll;
    private bool _historyVisible; // <-- новое поле
    private bool _running;
    private bool _keepOnDeactivate;

    public MergeLauncherWindow(CodeMergerModule module)
    {
        _module = module;

        ShowInTaskbar = false;
        Topmost = true;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Width = 640;
        Background = Brushes.Transparent;
        Opacity = 0;
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 0,
            ResizeBorderThickness = new Thickness(0),
            GlassFrameThickness = new Thickness(-1),
            UseAeroCaptionButtons = false,
            CornerRadius = new CornerRadius(0)
        });

        var wa = SystemParameters.WorkArea;
        Left = Math.Round(wa.Left + (wa.Width - Width) / 2);
        Top = Math.Round(wa.Top + Math.Max(16, wa.Height * 0.14));

        _watermark = new TextBlock
        {
            Text = "Путь к каталогу проекта…",
            Foreground = JarvisTheme.ForegroundBrush(0.35),
            FontSize = 16,
            IsHitTestVisible = false,
            VerticalAlignment = VerticalAlignment.Center
        };
        _input = new TextBox
        {
            FontSize = 16,
            Foreground = JarvisTheme.ForegroundBrush(0.95),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            CaretBrush = JarvisTheme.AccentBrush,
            SelectionBrush = JarvisTheme.Solid(JarvisTheme.Accent, 0.35),
            Margin = new Thickness(0, 10, 0, 10),
            VerticalContentAlignment = VerticalAlignment.Center
        };
        _input.TextChanged += OnInputTextChanged;
        _input.PreviewKeyDown += OnInputKeyDown;

        var field = new Grid();
        field.Children.Add(_watermark);
        field.Children.Add(_input);

        _folderBtn = CodeMergerUi.Flat("📁", "Выбрать папку в Проводнике", 34, 34, 15);
        CodeMergerUi.HoverTint(_folderBtn, Colors.Transparent, Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));
        _folderBtn.Click += (_, _) => PickFolder();

        _historyBtn = CodeMergerUi.Flat("🕘", "История каталогов", 34, 34, 15);
        CodeMergerUi.HoverTint(_historyBtn, Colors.Transparent, Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));
        _historyBtn.Click += (_, _) => ToggleHistory();

        _runBtn = CodeMergerUi.Flat("▶", "Склеить и открыть папку с результатом", 40, 34, 13);
        _runBtn.Background = new SolidColorBrush(CodeMergerUi.Tint(JarvisTheme.Accent, 0x2E));
        _runBtn.Foreground = JarvisTheme.ForegroundBrush(0.95);
        CodeMergerUi.HoverTint(_runBtn, CodeMergerUi.Tint(JarvisTheme.Accent, 0x2E), CodeMergerUi.Tint(JarvisTheme.Accent, 0x55));
        _runBtn.Click += (_, _) => Run(CurrentPath());

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 2, 0)
        };
        buttons.Children.Add(_folderBtn);
        buttons.Children.Add(_historyBtn);
        buttons.Children.Add(_runBtn);

        var row = new Grid { Margin = new Thickness(16, 6, 12, 4) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(field, 0);
        Grid.SetColumn(buttons, 1);
        row.Children.Add(field);
        row.Children.Add(buttons);

        _historyPanel = new StackPanel { Margin = new Thickness(8, 6, 8, 2) };
        _historyHost = new Border
        {
            BorderThickness = new Thickness(0, 1, 0, 0),
            BorderBrush = JarvisTheme.Solid(JarvisTheme.Accent, 0.15),
            Child = _historyPanel,
            Visibility = Visibility.Collapsed
        };

        _statusHint = new TextBlock
        {
            Text = DefaultHint,
            Foreground = JarvisTheme.ForegroundBrush(0.4),
            FontSize = 10.5,
            Margin = new Thickness(18, 6, 16, 10)
        };

        var content = new StackPanel();
        content.Children.Add(row);
        content.Children.Add(_historyHost);
        content.Children.Add(_statusHint);

        var shell = new Border
        {
            Background = JarvisTheme.Solid(JarvisTheme.Bg, 0.62),
            BorderThickness = new Thickness(1),
            BorderBrush = JarvisTheme.Solid(JarvisTheme.Accent, 0.25),
            CornerRadius = new CornerRadius(10),
            RenderTransform = _slide
        };
        var dock = new DockPanel { LastChildFill = true };
        dock.Children.Add(AccentTopLine());
        dock.Children.Add(content);
        shell.Child = dock;
        Content = shell;

        // Deactivated обработчик удалён

        Loaded += OnLoaded;

        var last = module.RecentDirs.FirstOrDefault();
        if (!string.IsNullOrEmpty(last)) _input.Text = last;
        UpdateSuggestions();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _input.Focus();
        Keyboard.Focus(_input);
        _input.SelectAll();

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)) { EasingFunction = ease });
        _slide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-14, 0, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease });
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        try { WindowHelper.ApplyGlassBackdrop(this); }
        catch { }
    }

    private void OnInputTextChanged(object? sender, TextChangedEventArgs e)
    {
        _watermark.Visibility = string.IsNullOrEmpty(_input.Text) ? Visibility.Visible : Visibility.Collapsed;
        _showAll = false;
        UpdateSuggestions();
    }

    private void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                e.Handled = true;
                if (_historyVisible)
                {
                    _historyVisible = false;
                    UpdateSuggestions();
                    FocusInput();
                }
                else
                    Close();
                break;
            case Key.Return:
                e.Handled = true;
                Run(CurrentPath());
                break;
            case Key.Down:
                e.Handled = true;
                if (_filtered.Count == 0)
                {
                    if (!_historyVisible)
                        ToggleHistory(); // покажет историю, если скрыта
                    // иначе список пуст – ничего не делаем
                }
                else
                {
                    Select(_listSel < 0 ? 0 : (_listSel + 1) % _filtered.Count);
                }
                break;
            case Key.Up:
                e.Handled = true;
                if (_filtered.Count == 0)
                {
                    if (!_historyVisible)
                        ToggleHistory();
                }
                else
                {
                    Select(_listSel < 0 ? _filtered.Count - 1 : (_listSel - 1 + _filtered.Count) % _filtered.Count);
                }
                break;
            case Key.Tab:
                if (_filtered.Count > 0)
                {
                    e.Handled = true;
                    var pick = _filtered[Math.Max(0, _listSel)];
                    _input.Text = pick;
                    _input.CaretIndex = pick.Length;
                }
                break;
        }
    }

    private string CurrentPath() =>
        _listSel >= 0 && _listSel < _filtered.Count ? _filtered[_listSel] : _input.Text;

    private void UpdateSuggestions()
    {
        _rowBrushes.Clear();
        _historyPanel.Children.Clear();
        _filtered.Clear();

        if (!_historyVisible)
        {
            _historyHost.Visibility = Visibility.Collapsed;
            _statusHint.Text = DefaultHint;
            _historyBtn.Opacity = 0.75;
            return;
        }

        var text = _input.Text.Trim().Trim('"');
        var history = _module.RecentDirs;
        IEnumerable<string> source = _showAll || text.Length == 0
            ? history
            : history.Where(h => h.Contains(text, StringComparison.OrdinalIgnoreCase));
        _filtered = source.Take(MaxRows).ToList();

        for (int i = 0; i < _filtered.Count; i++)
            _historyPanel.Children.Add(BuildHistoryRow(_filtered[i], i));

        _historyHost.Visibility = _filtered.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        _historyBtn.Opacity = _historyVisible ? 1 : 0.75;
        _listSel = -1;
        _statusHint.Text = _filtered.Count > 0
            ? DefaultHint
            : "Укажите путь к каталогу — или выберите его кнопкой 📁 справа";
    }

    private Border BuildHistoryRow(string dir, int index)
    {
        var text = new TextBlock
        {
            Text = dir,
            Foreground = JarvisTheme.ForegroundBrush(0.9),
            FontSize = 13,
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = dir
        };

        var remove = new TextBlock
        {
            Text = "✕",
            FontSize = 11,
            Foreground = JarvisTheme.ForegroundBrush(0.4),
            Margin = new Thickness(12, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = Cursors.Hand,
            ToolTip = "Убрать из истории"
        };
        remove.MouseEnter += (_, _) => remove.Foreground = JarvisTheme.AccentBrush;
        remove.MouseLeave += (_, _) => remove.Foreground = JarvisTheme.ForegroundBrush(0.4);
        remove.MouseLeftButtonUp += (_, e) => { e.Handled = true; RemoveHistoryAt(index); };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(text, 0);
        Grid.SetColumn(remove, 1);
        grid.Children.Add(text);
        grid.Children.Add(remove);

        var brush = new SolidColorBrush(Colors.Transparent);
        _rowBrushes.Add(brush);
        var body = new Border
        {
            Background = brush,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 7, 6, 7),
            Margin = new Thickness(0, 1, 0, 1),
            Cursor = Cursors.Hand,
            Child = grid
        };
        body.MouseEnter += (_, _) => Select(index);
        body.MouseLeftButtonUp += (_, e) => { e.Handled = true; Choose(index); };
        return body;
    }

    private void Select(int index)
    {
        if (_filtered.Count == 0) return;
        index = ((index % _filtered.Count) + _filtered.Count) % _filtered.Count;
        _listSel = index;
        for (int i = 0; i < _rowBrushes.Count; i++)
            _rowBrushes[i].BeginAnimation(SolidColorBrush.ColorProperty,
                new ColorAnimation(i == index ? RowSelected : Colors.Transparent, TimeSpan.FromMilliseconds(70)));
    }

    private void Choose(int index)
    {
        if (index < 0 || index >= _filtered.Count) return;
        var path = _filtered[index];
        _input.Text = path;
        _input.CaretIndex = path.Length;
        _historyVisible = false;
        UpdateSuggestions();
        FocusInput();
    }

    private void RemoveHistoryAt(int index)
    {
        if (index < 0 || index >= _filtered.Count) return;
        _module.RemoveRecent(_filtered[index]);
        _historyVisible = false;
        UpdateSuggestions();
        FocusInput();
    }

    private void ToggleHistory()
    {
        _historyVisible = !_historyVisible;
        UpdateSuggestions();
        if (_historyVisible && _filtered.Count > 0) Select(0);
        FocusInput();
    }

    private void FocusInput()
    {
        _input.Focus();
        Keyboard.Focus(_input);
    }

    private void PickFolder()
    {
        _keepOnDeactivate = true;
        try
        {
            var dlg = new OpenFolderDialog { Title = "Каталог для слияния" };
            var current = _input.Text.Trim().Trim('"');
            if (Directory.Exists(current)) dlg.InitialDirectory = current;
            if (dlg.ShowDialog(this) == true)
            {
                _input.Text = dlg.FolderName;
                _input.CaretIndex = _input.Text.Length;
            }
        }
        catch (Exception ex) { Log.Error($"merge: выбор папки: {ex}"); }
        finally { _keepOnDeactivate = false; }
        Activate();
        FocusInput();
    }

    private async void Run(string? rawPath)
    {
        if (_running) return;
        var path = rawPath?.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            _module.Services.Overlay.ShowToast($"Каталог не найден: {TextUtil.Preview(path)}", "Слияние исходников");
            return;
        }

        _running = true;
        SetBusy(true);
        try
        {
            var result = await Task.Run(() => _module.MergeFiles(path!));
            if (result == null)
            {
                _module.Services.Overlay.ShowToast("Файлы по шаблонам не найдены", "Слияние исходников");
                FocusInput();
                return;
            }

            _module.PushRecent(path!);
            _module.Services.Overlay.ShowToast(
                $"Склеено {result.FileCount} файлов ({result.TotalBytes / 1024.0:F0} КБ) → {Path.GetFileName(result.OutputPath)}",
                "Слияние исходников", 2600);
            OpenExplorerAt(result.OutputPath);
            Close();
        }
        finally
        {
            _running = false;
            SetBusy(false);
            UpdateSuggestions();
        }
    }

    private void SetBusy(bool busy)
    {
        _runBtn.IsEnabled = !busy;
        _folderBtn.IsEnabled = !busy;
        _historyBtn.IsEnabled = !busy;
        _input.IsReadOnly = busy;
        _runBtn.Opacity = busy ? 0.55 : 1;
        if (busy) _statusHint.Text = "Слияние…";
    }

    private static void OpenExplorerAt(string filePath)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{filePath}\"") { UseShellExecute = true });
        }
        catch
        {
            try
            {
                var dir = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(dir)) Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
            }
            catch { }
        }
    }

    public void ForceClose()
    {
        _running = false;
        Close();
    }

    private static Border AccentTopLine()
    {
        var a = JarvisTheme.Accent;
        var g = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 0),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(0, a.R, a.G, a.B), 0),
                new GradientStop(a, 0.5),
                new GradientStop(Color.FromArgb(0, a.R, a.G, a.B), 1)
            }
        };
        g.Freeze();
        var line = new Border { Height = 1.5, Background = g };
        DockPanel.SetDock(line, Dock.Top);
        return line;
    }
}