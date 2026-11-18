using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Jargb;

Console.OutputEncoding = Encoding.UTF8;
try
{
    if (args is [] or ["help"] or ["--help"] or ["-h"])
    {
        PrintHelp();
        return 0;
    }
    LocalAccess.Run("CLI", () => ExecuteAsync(args).GetAwaiter().GetResult());
    return 0;
}
catch (FormatException error)
{
    Console.Error.WriteLine($"{error.Message} Список команд: jargb-cli --help.");
    return 2;
}
catch (Exception error) when (error is IOException or Win32Exception or UnauthorizedAccessException or InvalidDataException or JsonException)
{
    Console.Error.WriteLine(error.Message);
    return 1;
}
catch (AggregateException error)
{
    foreach (Exception failure in error.Flatten().InnerExceptions)
        Console.Error.WriteLine(failure.Message);
    return 1;
}

static async Task ExecuteAsync(string[] args)
{
    switch (args)
    {
        case ["status"]:
            await PrintStatusAsync();
            break;
        case ["on"]:
            await TurnOnAsync();
            break;
        case ["off"]:
            await TurnOffAsync();
            break;
        case ["preset", var slot]:
            await ApplyPresetAsync(ParseSlot(slot));
            break;
        case ["color", var hex]:
            await ApplyColorAsync(ParseHex(hex));
            break;
        case ["brightness", var value]:
            await ApplyBrightnessAsync(ParseBrightness(value));
            break;
        case ["game-sync", "on"]:
            MsiGameSyncClient.VerifyGameModeConfigured();
            PrintGameSync(await MsiGameSyncClient.SetEnabledAsync(true));
            break;
        case ["game-sync", "off"]:
            PrintGameSync(await MsiGameSyncClient.SetEnabledAsync(false));
            break;
        default:
            throw new FormatException("Неизвестная команда или количество аргументов.");
    }
}

static void PrintHelp() => Console.WriteLine("""
    jargb-cli status                Состояние обоих разъёмов, уровня, пресетов и Game Sync
    jargb-cli on                    Включить подсветку с последним цветом
    jargb-cli off                   Выключить подсветку и отключить Game Sync
    jargb-cli preset 1              Применить сохранённый пресет 1–4
    jargb-cli color 53BFFF           Включить выбранный RGB-цвет (RRGGBB)
    jargb-cli brightness 40         Установить уровень 10–100% с шагом 10%
    jargb-cli game-sync on          Включить штатный MSI Game Sync
    jargb-cli game-sync off         Выключить штатный MSI Game Sync
    """);

static async Task PrintStatusAsync()
{
    using MsiController controller = new();
    var (first, second) = controller.Read();
    PrintHeader("JARGB_V2_1", first);
    PrintHeader("JARGB_V2_2", second);

    PresetSettings settings = LoadSettings();
    Console.WriteLine($"Пресеты: {string.Join(", ", settings.Colors.Select((color, index) => $"{index + 1}={PresetStore.Hex(color)}"))}");
    if (settings.Brightness is int saved)
        Console.WriteLine($"Сохранённая яркость: {saved}%");
    PrintGameSync(await MsiGameSyncClient.ReadStateAsync());
}

static async Task TurnOnAsync()
{
    await RequireManualControlAsync();
    using MsiController controller = new();
    controller.TurnOn(LoadSettings().Brightness);
    PrintHeaders(controller);
}

static async Task TurnOffAsync()
{
    using MsiController controller = new();
    MsiGameSyncState sync = await MsiGameSyncClient.ReadStateAsync();
    if (sync.Enabled)
        await MsiGameSyncClient.SetEnabledAsync(false);

    try { controller.TurnOff(); }
    catch (Exception error) when (sync.Enabled && error is IOException or Win32Exception)
    {
        try { await MsiGameSyncClient.SetEnabledAsync(true); }
        catch (IOException rollback)
        {
            throw new AggregateException(error, new IOException($"Не удалось восстановить Game Sync: {rollback.Message}", rollback));
        }
        throw;
    }

    PrintHeaders(controller);
    if (sync.Enabled)
        Console.WriteLine("Game Sync отключён, чтобы игры не включали подсветку.");
}

static async Task ApplyPresetAsync(int slot)
{
    PresetSettings settings = LoadSettings();
    await ApplyManualColorAsync(settings.Colors[slot - 1], settings.Brightness);
}

static async Task ApplyColorAsync(Lighting color) =>
    await ApplyManualColorAsync(color, LoadSettings().Brightness);

static async Task ApplyManualColorAsync(Lighting color, int? brightness)
{
    await RequireManualControlAsync();
    using MsiController controller = new();
    var (first, second) = controller.Read();
    int detected = first.Mode == 0 && second.Mode != 0 ? second.BrightnessPercent : first.BrightnessPercent;
    int preferred = brightness ?? (MsiController.SupportsBrightness(detected) ? detected : MsiController.MaximumBrightness);
    controller.Apply(color, preferred);
    PrintHeaders(controller);
}

static async Task ApplyBrightnessAsync(int percent)
{
    await RequireManualControlAsync();
    using MsiController controller = new();
    var (first, second) = controller.Read();
    if (first.Mode != 0 || second.Mode != 0)
        controller.ApplyBrightness(percent);

    try { PresetStore.SetBrightness(PresetStore.PathOnDisk, percent); }
    catch (Exception error) when (error is IOException or Win32Exception or UnauthorizedAccessException or InvalidDataException or JsonException)
    {
        throw new IOException($"Не удалось сохранить яркость {percent}% для следующего включения: {error.Message}", error);
    }

    Console.WriteLine(first.Mode == 0 && second.Mode == 0
        ? $"Яркость {percent}% сохранена для следующего включения."
        : $"Яркость обоих разъёмов: {percent}%.");
}

static async Task RequireManualControlAsync()
{
    MsiGameSyncState sync = await MsiGameSyncClient.ReadStateAsync();
    if (sync.ActiveGame is string game)
        throw new IOException($"Подсветкой управляет Game Sync ({game}). Для немедленного изменения сначала выполните jargb-cli game-sync off.");
}

static PresetSettings LoadSettings() => PresetStore.Load(PresetStore.PathOnDisk);

static void PrintHeaders(MsiController controller)
{
    var (first, second) = controller.Read();
    PrintHeader("JARGB_V2_1", first);
    PrintHeader("JARGB_V2_2", second);
}

static void PrintHeader(string name, HeaderState header)
{
    string hex = $"#{header.Red:X2}{header.Green:X2}{header.Blue:X2}";
    string power = header.Mode == 0 ? "выкл" : "вкл";
    Console.WriteLine($"{name}: {power} · {hex} · {header.BrightnessPercent}%");
}

static void PrintGameSync(MsiGameSyncState sync)
{
    string power = sync.Enabled ? "вкл" : "выкл";
    string game = sync.ActiveGame is string name ? $" · {name}" : "";
    Console.WriteLine($"Game Sync: {power}{game}");
}

static int ParseSlot(string slot) =>
    int.TryParse(slot, NumberStyles.None, CultureInfo.InvariantCulture, out int index) && index is >= 1 and <= PresetStore.Count
        ? index
        : throw new FormatException($"Номер пресета должен быть от 1 до {PresetStore.Count}.");

static int ParseBrightness(string input) =>
    int.TryParse(input.TrimEnd('%'), NumberStyles.None, CultureInfo.InvariantCulture, out int percent) &&
    MsiController.SupportsBrightness(percent)
        ? percent
        : throw new FormatException("Яркость должна быть от 10 до 100% с шагом 10%. Для выключения используйте off.");

static Lighting ParseHex(string input)
{
    string hex = input.StartsWith('#') ? input : $"#{input}";
    return PresetStore.TryParseHex(hex, out Lighting color)
        ? color
        : throw new FormatException("Цвет должен содержать шесть шестнадцатеричных цифр, например 53BFFF.");
}
