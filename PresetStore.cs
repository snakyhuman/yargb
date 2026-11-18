using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jargb;

internal readonly record struct PresetSettings(Lighting[] Colors, int? Brightness);

internal static class PresetStore
{
    internal const int Count = 4;
    private const string Resource = "presets";
    internal static readonly string PathOnDisk = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Jargb", "presets.json");

    private sealed record PresetFile([property: JsonRequired] string[] Colors, int? Brightness);

    internal static PresetSettings Load(string path) => LocalAccess.Run(Resource, () => ReadFile(path));

    private static PresetSettings ReadFile(string path)
    {
        byte[] bytes;
        try { bytes = File.ReadAllBytes(path); }
        catch (FileNotFoundException) { return new(CreateDefaults(), null); }
        catch (DirectoryNotFoundException) { return new(CreateDefaults(), null); }

        PresetFile? file = JsonSerializer.Deserialize<PresetFile>(bytes);
        if (file?.Colors is not { Length: Count } colors)
            throw new InvalidDataException($"В файле {path} должно быть ровно четыре цвета.");

        if (file.Brightness is int brightness && !MsiController.SupportsBrightness(brightness))
            throw new InvalidDataException($"В файле {path} яркость должна быть от 10 до 100 с шагом 10.");

        return new(colors.Select((color, index) => ParseColor(color, index, path)).ToArray(), file.Brightness);
    }

    internal static void SetBrightness(string path, int percent) => LocalAccess.Run(Resource, () =>
    {
        if (!MsiController.SupportsBrightness(percent))
            throw new ArgumentOutOfRangeException(nameof(percent), percent, "Expected 10–100 percent in 10-point steps.");
        PresetSettings settings = ReadFile(path);
        if (settings.Brightness != percent)
            WriteFile(path, settings with { Brightness = percent });
    });

    internal static void SetColor(string path, int index, Lighting color) => LocalAccess.Run(Resource, () =>
    {
        if (index is < 0 or >= Count)
            throw new ArgumentOutOfRangeException(nameof(index), index, "Invalid preset index.");
        PresetSettings settings = ReadFile(path);
        if (settings.Colors[index] == color)
            return;
        Lighting[] colors = (Lighting[])settings.Colors.Clone();
        colors[index] = color;
        WriteFile(path, settings with { Colors = colors });
    });

    private static void WriteFile(string path, PresetSettings settings)
    {
        string directory = Path.GetDirectoryName(path)
            ?? throw new ArgumentException("Укажите полный путь к файлу пресетов.", nameof(path));
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory, $"presets-{Guid.NewGuid():N}.tmp");
        try
        {
            PresetFile file = new(settings.Colors.Select(Hex).ToArray(), settings.Brightness);
            using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, file);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    internal static string Hex(Lighting color) => $"#{color.Red:X2}{color.Green:X2}{color.Blue:X2}";

    internal static Lighting[] CreateDefaults() =>
    [
        new(LightingEffect.Static, 163, 117, 255),
        new(LightingEffect.Static, 83, 191, 255),
        new(LightingEffect.Static, 90, 227, 174),
        new(LightingEffect.Static, 255, 172, 89)
    ];

    private static Lighting ParseColor(string? text, int index, string path)
    {
        if (!TryParseHex(text, out Lighting color))
            throw new InvalidDataException($"В файле {path} цвет №{index + 1} должен быть в формате #RRGGBB.");

        return color;
    }

    internal static bool TryParseHex(string? text, out Lighting color)
    {
        color = default;
        if (text is not { Length: 7 } || text[0] != '#' ||
            !uint.TryParse(text.AsSpan(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint rgb))
            return false;
        color = new(LightingEffect.Static, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        return true;
    }
}
