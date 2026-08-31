using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using JarvisUtils.Core;

namespace JarvisUtils.UI.Theme;

/// <summary>
/// Тумблер в стиле Jarvis. Отличие от XAML-триггеров с EnterActions: начальное состояние
/// применяется мгновенно — включённый модуль не «проигрывает» анимацию с выключенного при
/// каждом открытии настроек. Анимируется только реальное переключение после загрузки.
/// </summary>
public sealed class JarvisToggleSwitch : ToggleButton
{
    private const double TrackW = 40, TrackH = 22, KnobD = 14, KnobTravel = 18;

    private static readonly Color OffTrack = Color.FromRgb(0x23, 0x27, 0x33);
    private static readonly Color OffBorder = Color.FromRgb(0x31, 0x36, 0x4A);
    private static readonly Color OffBorderHover = Color.FromRgb(0x3D, 0x43, 0x56);
    private static readonly Color OnTrack = Color.FromArgb(0x2E, 0x00, 0xD2, 0xFF);
    private static readonly Color OnBorder = Color.FromArgb(0x8C, 0x00, 0xD2, 0xFF);
    private static readonly Color OffKnob = Color.FromRgb(0x8B, 0x93, 0xA5);

    private readonly TranslateTransform _knob = new();
    private Border _track = null!;
    private Ellipse _knobDot = null!;
    private bool _loaded, _hover;

    public JarvisToggleSwitch()
    {
        Cursor = Cursors.Hand;
        Focusable = false;

        // голый ContentPresenter вместо дефолтного хрома ToggleButton
        Template = new ControlTemplate(typeof(ToggleButton))
        { VisualTree = new FrameworkElementFactory(typeof(ContentPresenter)) };

        BuildVisual();

        Checked += (_, _) => ApplyState(true);
        Unchecked += (_, _) => ApplyState(false);
        Loaded += (_, _) => { _loaded = true; ApplyState(IsChecked == true, animate: false); };
        MouseEnter += (_, _) => { _hover = true; PaintBorder(); };
        MouseLeave += (_, _) => { _hover = false; PaintBorder(); };

        ApplyState(false, animate: false); // стартовое состояние — мгновенно
    }

    private void BuildVisual()
    {
        _track = new Border
        {
            Width = TrackW, Height = TrackH,
            CornerRadius = new CornerRadius(TrackH / 2),
            Background = new SolidColorBrush(OffTrack),   // «живые» кисти — под ColorAnimation
            BorderBrush = new SolidColorBrush(OffBorder),
            BorderThickness = new Thickness(1)
        };
        _knobDot = new Ellipse
        {
            Width = KnobD, Height = KnobD,
            Fill = new SolidColorBrush(OffKnob),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 0, 0),
            RenderTransform = _knob
        };
        var grid = new Grid();
        grid.Children.Add(_track);
        grid.Children.Add(_knobDot);
        Content = new Border { Padding = new Thickness(6, 4, 6, 4), Child = grid }; // площадка клика
    }

    private void ApplyState(bool on, bool? animate = null)
    {
        bool anim = animate ?? _loaded; // до первой загрузки — только мгновенные состояния
        double target = on ? KnobTravel : 0;

        if (anim)
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var dur = TimeSpan.FromMilliseconds(140);
            _knob.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(target, dur) { EasingFunction = ease });
            ((SolidColorBrush)_track.Background).BeginAnimation(SolidColorBrush.ColorProperty,
                new ColorAnimation(on ? OnTrack : OffTrack, dur));
            ((SolidColorBrush)_knobDot.Fill).BeginAnimation(SolidColorBrush.ColorProperty,
                new ColorAnimation(on ? JarvisTheme.Accent : OffKnob, dur));
        }
        else
        {
            _knob.BeginAnimation(TranslateTransform.XProperty, null);
            _knob.X = target;
            SetColorInstant((SolidColorBrush)_track.Background, on ? OnTrack : OffTrack);
            SetColorInstant((SolidColorBrush)_knobDot.Fill, on ? JarvisTheme.Accent : OffKnob);
        }
        PaintBorder();
    }

    private void PaintBorder()
    {
        bool on = IsChecked == true;
        var target = on ? OnBorder : (_hover ? OffBorderHover : OffBorder);
        var brush = (SolidColorBrush)_track.BorderBrush;
        if (_loaded)
            brush.BeginAnimation(SolidColorBrush.ColorProperty,
                new ColorAnimation(target, TimeSpan.FromMilliseconds(90)));
        else
            SetColorInstant(brush, target);
    }

    private static void SetColorInstant(SolidColorBrush brush, Color c)
    {
        brush.BeginAnimation(SolidColorBrush.ColorProperty, null); // снять анимацию, если была
        brush.Color = c;
    }
}