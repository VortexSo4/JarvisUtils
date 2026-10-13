using JarvisUtils.Core;

namespace JarvisUtils.Modules.Widgets;

/// <summary>Готовый «свой» виджет: XAML + скрипт. Заодно — живые примеры для редактора.</summary>
public sealed record WidgetTemplate(
    string Name, string Glyph, string Description,
    double Width, double Height, WidgetLayer Layer,
    string Xaml, string Script = "");

public static class WidgetTemplates
{
    // Общие куски разметки: тёмная полупрозрачная «карточка»
    private const string Card = "Background=\"#E6161820\" CornerRadius=\"14\" BorderBrush=\"#3300D2FF\" BorderThickness=\"1\"";
    private const string Icons = "FontFamily=\"Segoe Fluent Icons\"";

    public static readonly WidgetTemplate Clock = new(
        "Часы", FluentIcons.Clock, "Время и дата крупно",
        250, 120, WidgetLayer.Normal,
        $$"""
        <Border {{Card}} Padding="18,12">
          <StackPanel VerticalAlignment="Center">
            <TextBlock Text="{Binding Now, StringFormat=HH:mm}" FontSize="48" FontWeight="Light" Foreground="White"/>
            <TextBlock Text="{Binding Now, StringFormat='dddd, d MMMM'}" FontSize="14" Foreground="#9900D2FF"/>
          </StackPanel>
        </Border>
        """);

    public static readonly WidgetTemplate Monitor = new(
        "Система", FluentIcons.Cpu, "CPU, память, сеть, диск",
        260, 170, WidgetLayer.Normal,
        $$"""
        <Border {{Card}} Padding="16,12">
          <Border.Resources>
            <Style TargetType="TextBlock"><Setter Property="Foreground" Value="#DDFFFFFF"/><Setter Property="FontSize" Value="12.5"/></Style>
          </Border.Resources>
          <StackPanel>
            <DockPanel>
              <TextBlock Text="{Binding Cpu, StringFormat={}{0:0}%}" DockPanel.Dock="Right"/>
              <TextBlock Text="Процессор"/>
            </DockPanel>
            <Grid Height="5" Margin="0,5,0,10">
              <Border Background="#22FFFFFF" CornerRadius="3"/>
              <Border Background="#FF00D2FF" CornerRadius="3" RenderTransformOrigin="0,0.5">
                <Border.RenderTransform><ScaleTransform ScaleX="{Binding CpuFraction}"/></Border.RenderTransform>
              </Border>
            </Grid>
            <DockPanel>
              <TextBlock DockPanel.Dock="Right">
                <Run Text="{Binding RamUsedGb, StringFormat={}{0:0.0}, Mode=OneWay}"/> / <Run Text="{Binding RamTotalGb, StringFormat={}{0:0} ГБ, Mode=OneWay}"/>
              </TextBlock>
              <TextBlock Text="Память"/>
            </DockPanel>
            <Grid Height="5" Margin="0,5,0,10">
              <Border Background="#22FFFFFF" CornerRadius="3"/>
              <Border Background="#FFB18CFF" CornerRadius="3" RenderTransformOrigin="0,0.5">
                <Border.RenderTransform><ScaleTransform ScaleX="{Binding RamFraction}"/></Border.RenderTransform>
              </Border>
            </Grid>
            <DockPanel>
              <TextBlock DockPanel.Dock="Right" Text="{Binding DiskFreeGb, StringFormat=свободно {0:0} ГБ}"/>
              <TextBlock>
                <Run Text="&#xE896;" FontFamily="Segoe Fluent Icons" Foreground="#FF00D2FF"/> <Run Text="{Binding NetDown, StringFormat={}{0:0} КБ/с, Mode=OneWay}"/>
                <Run Text="  "/><Run Text="&#xE898;" FontFamily="Segoe Fluent Icons" Foreground="#FFB18CFF"/> <Run Text="{Binding NetUp, StringFormat={}{0:0} КБ/с, Mode=OneWay}"/>
              </TextBlock>
            </DockPanel>
          </StackPanel>
        </Border>
        """);

    public static readonly WidgetTemplate Pomodoro = new(
        "Помодоро", FluentIcons.Stopwatch, "25 минут работы, 5 отдыха",
        230, 150, WidgetLayer.Topmost,
        $$"""
        <Border {{Card}} Padding="14,10">
          <StackPanel>
            <TextBlock Text="{Binding Data[phase]}" Foreground="#9900D2FF" FontSize="12"/>
            <TextBlock Text="{Binding Data[left]}" Foreground="White" FontSize="44" FontWeight="Light"/>
            <StackPanel Orientation="Horizontal" Margin="0,4,0,0">
              <Button x:Name="toggle" Content="{Binding Data[toggleText]}" Padding="12,4" Margin="0,0,6,0"/>
              <Button x:Name="reset" Padding="8,4"><TextBlock Text="&#xE72C;" {{Icons}}/></Button>
            </StackPanel>
          </StackPanel>
        </Border>
        """,
        """
        // Переменные скрипта живут, пока живёт виджет
        var work = TimeSpan.FromMinutes(25);
        var rest = TimeSpan.FromMinutes(5);
        var resting = false;
        var running = false;
        var left = work;
        DateTime last = Now;

        void Show()
        {
            Data["left"] = left.ToString(@"mm\:ss");
            Data["phase"] = resting ? "Отдых" : "Работа";
            Data["toggleText"] = running ? "Пауза" : "Старт";
        }

        Every(0.25, () =>
        {
            var now = Now;
            if (running)
            {
                left -= now - last;
                if (left <= TimeSpan.Zero)
                {
                    resting = !resting;
                    left = resting ? rest : work;
                    Toast(resting ? "Время отдохнуть" : "Пора за работу");
                }
            }
            last = now;
            Show();
        });

        On("toggle", () => { running = !running; Show(); });
        On("reset", () => { running = false; resting = false; left = work; Show(); });
        """);

    public static readonly WidgetTemplate Stopwatch = new(
        "Секундомер", FluentIcons.Stopwatch, "Старт, пауза, круги",
        230, 140, WidgetLayer.Topmost,
        $$"""
        <Border {{Card}} Padding="14,10">
          <StackPanel>
            <TextBlock Text="{Binding Data[time]}" Foreground="White" FontSize="38" FontWeight="Light" FontFamily="Cascadia Mono, Consolas"/>
            <StackPanel Orientation="Horizontal" Margin="0,6,0,0">
              <Button x:Name="toggle" Content="{Binding Data[toggleText]}" Padding="12,4" Margin="0,0,6,0"/>
              <Button x:Name="reset" Padding="8,4"><TextBlock Text="&#xE72C;" {{Icons}}/></Button>
            </StackPanel>
          </StackPanel>
        </Border>
        """,
        """
        var sw = new System.Diagnostics.Stopwatch();
        void Show()
        {
            Data["time"] = sw.Elapsed.ToString(@"mm\:ss\.f");
            Data["toggleText"] = sw.IsRunning ? "Пауза" : "Старт";
        }
        Every(0.1, Show);
        On("toggle", () => { if (sw.IsRunning) sw.Stop(); else sw.Start(); Show(); });
        On("reset", () => { sw.Reset(); Show(); });
        """);

    public static readonly WidgetTemplate Countdown = new(
        "Обратный отсчёт", FluentIcons.Calendar, "Сколько осталось до даты",
        250, 120, WidgetLayer.Normal,
        $$"""
        <Border {{Card}} Padding="16,12">
          <StackPanel VerticalAlignment="Center">
            <TextBlock Text="{Binding Data[title]}" Foreground="#9900D2FF" FontSize="13"/>
            <TextBlock Text="{Binding Data[left]}" Foreground="White" FontSize="34" FontWeight="Light"/>
          </StackPanel>
        </Border>
        """,
        """
        // Поменяйте дату и подпись под себя
        var target = new DateTime(DateTime.Now.Year + 1, 1, 1);
        Data["title"] = "До Нового года";

        Every(1, () =>
        {
            var left = target - Now;
            Data["left"] = left <= TimeSpan.Zero ? "Наступило!" : $"{left.Days} дн {left.Hours:00}:{left.Minutes:00}:{left.Seconds:00}";
        });
        """);

    public static readonly WidgetTemplate Launcher = new(
        "Лаунчер", FluentIcons.Tiles, "Кнопки запуска программ и сайтов",
        250, 76, WidgetLayer.Topmost,
        $$$"""
        <Border {{{Card}}} Padding="8">
          <Border.Resources>
            <Style TargetType="Button" BasedOn="{StaticResource {x:Type Button}}">
              <Setter Property="Width" Value="48"/><Setter Property="Height" Value="44"/>
              <Setter Property="Margin" Value="3,0"/><Setter Property="Padding" Value="0"/>
              <Setter Property="Command" Value="{Binding Run}"/>
            </Style>
            <Style TargetType="TextBlock"><Setter Property="FontFamily" Value="Segoe Fluent Icons"/><Setter Property="FontSize" Value="18"/><Setter Property="Foreground" Value="White"/></Style>
          </Border.Resources>
          <StackPanel Orientation="Horizontal" HorizontalAlignment="Center">
            <Button CommandParameter="explorer.exe" ToolTip="Проводник"><TextBlock Text="&#xE8B7;"/></Button>
            <Button CommandParameter="calc.exe" ToolTip="Калькулятор"><TextBlock Text="&#xE8EF;"/></Button>
            <Button CommandParameter="https://github.com" ToolTip="GitHub"><TextBlock Text="&#xE774;"/></Button>
            <Button CommandParameter="ms-settings:" ToolTip="Параметры"><TextBlock Text="&#xE713;"/></Button>
          </StackPanel>
        </Border>
        """);

    public static readonly WidgetTemplate Blank = new(
        "Свой виджет", FluentIcons.Code, "XAML + C# с нуля",
        260, 140, WidgetLayer.Topmost,
        $$"""
        <Border {{Card}} Padding="16,12">
          <StackPanel>
            <TextBlock Text="Привет!" Foreground="White" FontSize="22"/>
            <TextBlock Text="{Binding Data[clicks], StringFormat=Нажатий: {0}, TargetNullValue=Нажмите кнопку}" Foreground="#99FFFFFF" Margin="0,4,0,8"/>
            <Button x:Name="click" Content="Нажми" HorizontalAlignment="Left"/>
          </StackPanel>
        </Border>
        """,
        """
        var clicks = (int)LoadNumber("clicks");
        if (clicks > 0) Data["clicks"] = clicks;
        On("click", () =>
        {
            clicks++;
            Data["clicks"] = clicks;
            Save("clicks", clicks); // переживёт перезапуск
        });
        """);

    public static readonly WidgetTemplate[] All = [Clock, Monitor, Pomodoro, Stopwatch, Countdown, Launcher, Blank];
}
