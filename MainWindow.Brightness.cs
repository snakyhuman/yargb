using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace Jargb;

public partial class MainWindow
{
    private readonly DispatcherTimer brightnessTimer = new() { Interval = TimeSpan.FromMilliseconds(180) };
    private int? savedBrightness;
    private bool brightnessReady;
    private int BrightnessPercent => (int)BrightnessSlider.Value;

    private async Task InitializeBrightnessAsync(HeaderState first, HeaderState second)
    {
        if (savedBrightness is not int saved)
        {
            int detected = first.Mode == 0 && second.Mode != 0 ? second.BrightnessPercent : first.BrightnessPercent;
            BrightnessSlider.Value = sync.ActiveGame is null && MsiController.SupportsBrightness(detected)
                ? detected
                : MsiController.MaximumBrightness;
            return;
        }

        BrightnessSlider.Value = saved;
        if (powered == true && sync.ActiveGame is null &&
            ((first.Mode != 0 && first.BrightnessPercent != saved) ||
             (second.Mode != 0 && second.BrightnessPercent != saved)))
            await WriteBrightnessAsync(saved);
    }

    private void BrightnessChanged(object sender, RoutedPropertyChangedEventArgs<double> args)
    {
        if (!brightnessReady || closing)
            return;

        double snapped = Math.Round(args.NewValue / MsiController.BrightnessStep, MidpointRounding.AwayFromZero) *
            MsiController.BrightnessStep;
        if (args.NewValue != snapped)
        {
            BrightnessSlider.Value = snapped;
            return;
        }

        brightnessTimer.Stop();
        brightnessTimer.Start();
    }

    private void CommitBrightness(object? sender, EventArgs args)
    {
        if (!operation.IsCompleted || editingPreset ||
            (BrightnessSlider.IsMouseCaptureWithin && Mouse.LeftButton == MouseButtonState.Pressed))
            return;

        brightnessTimer.Stop();
        operation = CommitBrightnessAsync();
    }

    private async Task<bool> CommitBrightnessAsync()
    {
        int percent = BrightnessPercent;
        if (savedBrightness == percent)
            return true;

        SetBusy();
        try
        {
            PresetStore.SetBrightness(PresetStore.PathOnDisk, percent);
            savedBrightness = percent;
            if (powered == true)
            {
                if (sync.Enabled)
                    ShowGameSync(await MsiGameSyncClient.ReadStateAsync());

                if (sync.ActiveGame is not null)
                {
                    if (restorePending == GameEndRestore.None)
                        restorePending = GameEndRestore.Brightness;
                    ShowState("Яркость сохранена · применится после игры", Active);
                    return true;
                }

                await WriteBrightnessAsync(percent);
            }

            ShowState($"Яркость {percent}%", powered == true ? Active : Muted);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception)
        {
            ShowState($"Яркость {percent}% · {error.Message}", Error);
            return false;
        }
    }

    private async Task RestoreBrightnessAsync()
    {
        SetBusy();
        try
        {
            int percent = BrightnessPercent;
            await WriteBrightnessAsync(percent);
            ShowState($"Яркость {percent}%", Active);
        }
        catch (Exception error) when (error is IOException or Win32Exception)
        {
            ShowState(error.Message, Error);
        }
    }

    private static Task WriteBrightnessAsync(int percent) => Task.Run(() =>
    {
        using MsiController controller = new();
        controller.ApplyBrightness(percent);
    });
}
