using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using JarvisUtils.Core;

namespace JarvisUtils.UI;

/// <summary>Карточка «вышло обновление» в правом нижнем углу, в стиле тостов.</summary>
internal sealed class UpdatePromptWindow : Window
{
    private readonly ScaleTransform _scale;
    private bool _answered;

    public event Action? RestartRequested;
    public event Action? Postponed;

    public UpdatePromptWindow(string version)
    {
        OverlayVisuals.ConfigureWindow(this);
        Width = 380 + OverlayVisuals.ExtraWidth;
        SizeToContent = SizeToContent.Height;
        Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/JarvisUtils;component/UI/Theme/JarvisStyles.xaml")
        });

        var title = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        title.Children.Add(FluentIcons.Glyph(FluentIcons.Refresh, 13, JarvisTheme.AccentBrush));
        title.Children.Add(new TextBlock
        {
            Text = "Доступно обновление",
            Foreground = JarvisTheme.AccentBrush,
            FontSize = 11.5,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(8, 0, 0, 0)
        });

        var restart = new Button { Content = FluentIcons.WithLabel(FluentIcons.Refresh, "Перезапустить сейчас"), Padding = new Thickness(12, 6, 12, 6) };
        restart.Background = JarvisTheme.Solid(JarvisTheme.Accent, 0.18);
        restart.BorderBrush = JarvisTheme.Solid(JarvisTheme.Accent, 0.45);
        restart.Click += (_, _) => Answer(RestartRequested);
        var later = new Button { Content = "Позже", Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(8, 0, 0, 0) };
        later.Click += (_, _) => Answer(Postponed);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(restart);
        buttons.Children.Add(later);

        var panel = new StackPanel { Margin = new Thickness(16, 12, 16, 14) };
        panel.Children.Add(title);
        panel.Children.Add(new TextBlock
        {
            Text = $"JarvisUtils {version} загружен. Перезапуск займёт пару секунд — приложение откроется само.",
            Foreground = JarvisTheme.ForegroundBrush(0.9),
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(buttons);

        var (shell, scale) = OverlayVisuals.CreateShell(10, panel);
        _scale = scale;
        Content = shell;

        var area = SystemParameters.WorkArea;
        double height = OverlayVisuals.MeasureShellHeight(shell, Width);
        Left = area.Right - Width - 14;
        Top = area.Bottom - height - 14;
        Opacity = 0;
        Loaded += (_, _) => OverlayVisuals.PlayEntrance(this, _scale);
        Closed += (_, _) => { if (!_answered) Postponed?.Invoke(); }; // закрыли иначе — считаем «позже»
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (OverlayVisuals.UseAcrylic) WindowHelper.ApplyGlassBackdrop(this);
        WindowHelper.MakeNonActivating(this); // не отбираем фокус у того, во что пользователь печатает
    }

    private void Answer(Action? action)
    {
        _answered = true;
        Close();
        action?.Invoke();
    }
}
