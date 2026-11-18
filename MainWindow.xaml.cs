using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace Jargb;

public partial class MainWindow : Window
{
    private static readonly Lighting LegacyGameSetting = new(LightingEffect.Breathing, 153, 92, 255);
    private static readonly SolidColorBrush BorderIdle = Brush(51, 48, 66);
    private static readonly SolidColorBrush BorderSelected = Brush(181, 151, 255);
    private static readonly SolidColorBrush Active = Brush(146, 227, 182);
    private static readonly SolidColorBrush Muted = Brush(129, 119, 146);
    private static readonly SolidColorBrush Error = Brush(251, 144, 156);

    private Lighting selected;
    private bool? powered;
    private Lighting? rendered;
    private Task operation = Task.CompletedTask;

    public MainWindow()
    {
        InitializeComponent();
        InitializePresets();
        syncTimer.Tick += SyncTick;
        brightnessTimer.Tick += CommitBrightness;
        Closing += FinishBeforeClosing;
        SetBusy();
        Loaded += StartReadController;
    }

    private void StartReadController(object sender, RoutedEventArgs args)
    {
        Loaded -= StartReadController;
        operation = ReadControllerAsync();
    }

    private async Task ReadControllerAsync()
    {
        try
        {
            (HeaderState first, HeaderState second) = await Task.Run(() =>
            {
                using MsiController controller = new();
                return controller.Read();
            });
            powered = first.Mode != 0 || second.Mode != 0;
            if (first with { BrightnessPercent = second.BrightnessPercent } == second)
            {
                selected = RestoreSelection(first);
                int match = Array.IndexOf(presets, selected);
                selectedPreset = match >= 0 ? match : null;
            }

            await ConnectGameSyncAsync();
            await InitializeBrightnessAsync(first, second);
            ShowState(presetLoadError ?? "Контроллер подключён",
                presetLoadError is not null ? Error : powered == true ? Active : Muted);
        }
        catch (Exception error) when (error is IOException or Win32Exception)
        {
            ShowState(error.Message, Error);
        }
        finally
        {
            brightnessReady = true;
        }
    }

    private Lighting RestoreSelection(HeaderState header)
    {
        Lighting steady = new(LightingEffect.Static, header.Red, header.Green, header.Blue);
        if (header.Mode == (byte)LightingEffect.Breathing ||
            (header.Mode == (byte)LightingEffect.Off && steady == LegacyGameSetting with { Effect = LightingEffect.Static }))
            return steady with { Effect = LightingEffect.Breathing };

        return header.Mode == 0 && steady == new Lighting(LightingEffect.Static, 0, 0, 0) && !presets.Contains(steady)
            ? presets[0]
            : steady;
    }

    private async Task ApplyAsync(Lighting setting)
    {
        SetBusy();
        try
        {
            await WriteLightingAsync(setting, BrightnessPercent);
            rendered = setting;
            powered = setting.Effect != LightingEffect.Off;
            ShowState(powered == true ? "Подсветка включена" : "Подсветка выключена", powered == true ? Active : Muted);
        }
        catch (Exception error) when (error is IOException or Win32Exception)
        {
            powered = null;
            rendered = null;
            ShowState(error.Message, Error);
        }
    }

    private void SetBusy()
    {
        Power.IsEnabled = Game.IsEnabled = Palette.IsEnabled = false;
        if (!brightnessReady)
            BrightnessSlider.IsEnabled = false;
        Status.Text = "Связь с контроллером…";
        Status.ToolTip = null;
        StatusDot.Fill = Muted;
    }

    private void ShowState(string text, Brush indicator)
    {
        Power.IsEnabled = Game.IsEnabled = Palette.IsEnabled = true;
        BrightnessSlider.IsEnabled = presetLoadError is null;
        PowerText.Text = powered switch { true => "Вкл", false => "Выкл", null => "Включить" };
        AutomationProperties.SetName(Power, powered == true ? "Выключить подсветку" : "Включить подсветку");
        Power.Foreground = powered == true ? Active : Foreground;
        Power.BorderBrush = powered == true ? Active : BorderIdle;
        RefreshGameSwitch();
        for (int i = 0; i < presetButtons.Length; i++)
            presetButtons[i].BorderBrush = powered == true && selectedPreset == i ? BorderSelected : BorderIdle;

        Status.Text = text;
        Status.ToolTip = text;
        StatusDot.Fill = indicator;
    }

    private async void TogglePower(object sender, RoutedEventArgs args) =>
        await (operation = ApplyPowerAsync());

    private static Task WriteLightingAsync(Lighting setting, int brightnessPercent) => Task.Run(() =>
    {
        using MsiController controller = new();
        controller.Apply(setting, brightnessPercent);
    });

    private void Minimize(object sender, RoutedEventArgs args) => WindowState = WindowState.Minimized;

    private void CloseWindow(object sender, RoutedEventArgs args) => Close();

    private static SolidColorBrush Brush(byte red, byte green, byte blue) => new(Color.FromRgb(red, green, blue));
}
