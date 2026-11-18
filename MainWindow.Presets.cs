using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace Jargb;

public partial class MainWindow
{
    private readonly Button[] presetButtons = new Button[PresetStore.Count];
    private readonly Border[] swatches = new Border[PresetStore.Count];
    private readonly TextBlock[] presetLabels = new TextBlock[PresetStore.Count];
    private Lighting[] presets = [];
    private string? presetLoadError;
    private int? selectedPreset;
    private bool editingPreset;

    private void InitializePresets()
    {
        try
        {
            PresetSettings settings = PresetStore.Load(PresetStore.PathOnDisk);
            presets = settings.Colors;
            savedBrightness = settings.Brightness;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
        {
            presetLoadError = $"Не удалось прочитать {PresetStore.PathOnDisk}. {error.Message}";
            presets = PresetStore.CreateDefaults();
        }

        selected = presets[0];
        selectedPreset = 0;
        for (int index = 0; index < presets.Length; index++)
            Palette.Children.Add(CreatePreset(index));
    }

    private Grid CreatePreset(int index)
    {
        Grid tile = new() { Margin = new Thickness(3, 0, 3, 0) };
        Border swatch = new() { Height = 19, CornerRadius = new CornerRadius(5), Margin = new Thickness(0, 0, 0, 7) };
        TextBlock label = new() { FontFamily = new FontFamily("Consolas"), FontSize = 11 };
        StackPanel content = new() { Margin = new Thickness(10, 0, 10, 0) };
        content.Children.Add(swatch);
        content.Children.Add(label);

        Button apply = new()
        {
            Style = (Style)FindResource("Tile"),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Content = content
        };
        AutomationProperties.SetAutomationId(apply, $"preset-{index + 1}");
        apply.Click += async (_, _) => await (operation = SelectPresetAsync(index));

        Button edit = new()
        {
            Style = (Style)FindResource("PresetEdit"),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 5, 7),
            IsEnabled = presetLoadError is null,
            ToolTip = presetLoadError ?? $"Изменить цвет {index + 1}",
            Content = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse("M1,10 L2,7 L8,1 L11,4 L5,10 Z M7,2 L10,5"),
                Width = 12,
                Height = 12,
                Stroke = Brush(186, 173, 209),
                StrokeThickness = 1.25,
                StrokeLineJoin = PenLineJoin.Round
            }
        };
        AutomationProperties.SetName(edit, $"Изменить цвет {index + 1}");
        AutomationProperties.SetAutomationId(edit, $"edit-preset-{index + 1}");
        edit.Click += async (_, _) => await (operation = EditPresetAsync(index));

        presetButtons[index] = apply;
        presetLabels[index] = label;
        swatches[index] = swatch;
        RefreshPreset(index);
        tile.Children.Add(apply);
        tile.Children.Add(edit);
        return tile;
    }

    private async Task SelectPresetAsync(int index)
    {
        selectedPreset = index;
        selected = presets[index];
        await ApplySelectedPresetAsync();
    }

    private async Task EditPresetAsync(int index)
    {
        editingPreset = true;
        try
        {
            Lighting? choice = WindowsColorPicker.Choose(new WindowInteropHelper(this).Handle, presets[index], presets);
            if (choice is not Lighting color || color == presets[index])
                return;

            Lighting[] updated = (Lighting[])presets.Clone();
            updated[index] = color;
            PresetStore.SetColor(PresetStore.PathOnDisk, index, color);
            presets = updated;
            RefreshPreset(index);

            if (selectedPreset == index)
            {
                selected = color;
                if (powered == true)
                {
                    await ApplySelectedPresetAsync();
                    return;
                }
            }

            ShowState($"Цвет {index + 1} сохранён", powered == true ? Active : Muted);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception)
        {
            ShowState($"Цвет {index + 1} · {error.Message}", Error);
        }
        finally
        {
            editingPreset = false;
        }
    }

    private void RefreshPreset(int index)
    {
        Lighting color = presets[index];
        string hex = PresetStore.Hex(color);
        swatches[index].Background = Brush(color.Red, color.Green, color.Blue);
        presetLabels[index].Text = hex;
        presetButtons[index].ToolTip = $"Применить {hex}";
        AutomationProperties.SetName(presetButtons[index], $"Применить цвет {index + 1}, {hex}");
    }
}
