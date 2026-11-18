using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Threading;

namespace Jargb;

public partial class MainWindow
{
    private readonly DispatcherTimer syncTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private MsiGameSyncState sync;
    private int profileCount;
    private int syncRevision;
    private long? idleSince;
    private enum GameEndRestore { None, Brightness, Preset }
    private GameEndRestore restorePending;
    private bool resumeSyncOnPowerOn;
    private bool pollingSync;
    private bool closing;
    private bool readyToClose;

    private async Task ConnectGameSyncAsync()
    {
        try
        {
            MsiGameSyncState state = await MsiGameSyncClient.ReadStateAsync();
            profileCount = await MsiGameSyncClient.ReadProfileCountAsync();
            // The current LED colors can already belong to a game when the window opens.
            sync = state;
            ShowGameSync(state);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            ShowGameSyncError(error);
        }
    }

    private async void ToggleGameSync(object sender, RoutedEventArgs args) =>
        await (operation = ToggleGameSyncAsync());

    private async Task ToggleGameSyncAsync()
    {
        SetBusy();
        try
        {
            MsiGameSyncState current = await MsiGameSyncClient.ReadStateAsync();
            if (current.Enabled)
            {
                ShowGameSync(await MsiGameSyncClient.SetEnabledAsync(false));
                CancelScheduledRestore();
                if (powered == true)
                    await ApplyAsync(selected);
                else
                    ShowState("Game Sync выключен", Muted);
                return;
            }

            MsiGameSyncClient.VerifyGameModeConfigured();
            if (profileCount == 0)
                profileCount = await MsiGameSyncClient.ReadProfileCountAsync();

            if (powered != true)
            {
                await WriteLightingAsync(selected, BrightnessPercent);
                powered = true;
            }

            ShowGameSync(await MsiGameSyncClient.SetEnabledAsync(true));
            resumeSyncOnPowerOn = false;
            ShowState("Game Sync включён", Active);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception)
        {
            ShowState(error.Message, Error);
        }
    }

    private async void SyncTick(object? sender, EventArgs args)
    {
        if (closing || editingPreset || pollingSync || !operation.IsCompleted)
            return;

        pollingSync = true;
        int revision = syncRevision;
        try
        {
            MsiGameSyncState state = await MsiGameSyncClient.ReadStateAsync();
            if (closing || editingPreset || revision != syncRevision || !operation.IsCompleted)
                return;

            ShowGameSync(state);
            if (restorePending == GameEndRestore.None || idleSince is not long since ||
                Stopwatch.GetElapsedTime(since) < TimeSpan.FromSeconds(4))
                return;

            GameEndRestore restore = restorePending;
            restorePending = GameEndRestore.None;
            idleSince = null;
            if (powered == true)
                await (operation = restore == GameEndRestore.Preset ? ApplyAsync(selected) : RestoreBrightnessAsync());
        }
        catch (IOException error)
        {
            if (!closing && revision == syncRevision)
                ShowGameSyncError(error);
        }
        finally
        {
            pollingSync = false;
        }
    }

    private void ShowGameSync(MsiGameSyncState state)
    {
        syncRevision++;
        bool wasActive = sync.ActiveGame is not null;
        sync = state;
        if (state.ActiveGame is not null)
        {
            if (!wasActive)
                restorePending = GameEndRestore.Preset;
            idleSince = null;
        }
        else if (wasActive && restorePending != GameEndRestore.None)
        {
            idleSince = Stopwatch.GetTimestamp();
        }

        GameText.Text = state.ActiveGame is not null
            ? "Игра управляет подсветкой"
            : state.Enabled
            ? $"Профилей MSI: {profileCount} · ожидание игры"
            : $"Mystic Light · профилей: {profileCount}";
        Game.ToolTip = "Игровые эффекты из установленного Mystic Light";
        RefreshGameSwitch();

        if (!closing && (state.Enabled || restorePending != GameEndRestore.None))
            syncTimer.Start();
        else
            syncTimer.Stop();
    }

    private void CancelScheduledRestore()
    {
        restorePending = GameEndRestore.None;
        idleSince = null;
        syncTimer.Stop();
    }

    private void ShowGameSyncError(Exception error)
    {
        syncTimer.Stop();
        GameText.Text = "Связь с Mystic Light недоступна";
        Game.ToolTip = error.Message;
    }

    private void RefreshGameSwitch()
    {
        Game.BorderBrush = sync.Enabled ? BorderSelected : BorderIdle;
        GameSwitch.Text = sync.Enabled ? "Вкл" : "Выкл";
        GameSwitch.Foreground = sync.Enabled ? Active : Foreground;
        AutomationProperties.SetName(Game, sync.Enabled ? "Выключить Game Sync" : "Включить Game Sync");
    }

    private async Task ApplySelectedPresetAsync()
    {
        SetBusy();
        try
        {
            if (sync.Enabled)
            {
                ShowGameSync(await MsiGameSyncClient.ReadStateAsync());
                if (sync.ActiveGame is not null)
                {
                    restorePending = GameEndRestore.Preset;
                    ShowState("Цвет выбран · применится после игры", Active);
                    return;
                }
            }

            await ApplyAsync(selected);
        }
        catch (IOException error)
        {
            ShowState(error.Message, Error);
        }
    }

    private async Task ApplyPowerAsync()
    {
        SetBusy();
        try
        {
            if (powered == true)
            {
                if (sync.Enabled)
                {
                    ShowGameSync(await MsiGameSyncClient.SetEnabledAsync(false));
                    CancelScheduledRestore();
                    resumeSyncOnPowerOn = true;
                }
                await ApplyAsync(selected with { Effect = LightingEffect.Off });
                return;
            }

            await ApplyAsync(selected);
            if (powered == true && resumeSyncOnPowerOn)
            {
                SetBusy();
                MsiGameSyncClient.VerifyGameModeConfigured();
                ShowGameSync(await MsiGameSyncClient.SetEnabledAsync(true));
                resumeSyncOnPowerOn = false;
                ShowState("Подсветка и Game Sync включены", Active);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            ShowState(error.Message, Error);
        }
    }

    private async void FinishBeforeClosing(object? sender, CancelEventArgs args)
    {
        syncTimer.Stop();
        if (readyToClose || (!closing && operation.IsCompleted && !brightnessTimer.IsEnabled))
        {
            closing = true;
            return;
        }

        args.Cancel = true;
        if (closing)
            return;

        closing = true;
        bool saveBrightness = brightnessTimer.IsEnabled;
        brightnessTimer.Stop();
        await Dispatcher.Yield();
        await operation;
        if (saveBrightness && !await CommitBrightnessAsync())
        {
            closing = false;
            if (sync.Enabled)
                syncTimer.Start();
            return;
        }
        readyToClose = true;
        Close();
    }
}
