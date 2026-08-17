using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace StopwatchOverlay;

/// <summary>Stores user preferences outside the application directory so they survive single-file publishing and upgrades.</summary>
public sealed class UserSettings
{
    public int Mode { get; set; }
    public int ScreenIndex { get; set; }
    public string Theme { get; set; } = "Dark";
    public string Language { get; set; } = "en";
    public string Position { get; set; } = "Top Center";
    public string TextColor { get; set; } = "White";
    public string BorderColor { get; set; } = "Black";
    public string Font { get; set; } = "Consolas";
    public int TimeFormat { get; set; }
    public double TextSize { get; set; } = 48;
    public double BorderWidth { get; set; } = 2;
    public double BackgroundOpacity { get; set; } = 50;
    public bool AutoStart { get; set; }
    public bool ShowRecIndicator { get; set; }
    public bool ClickThrough { get; set; }
    public bool BlinkColon { get; set; }
    public string CountdownHours { get; set; } = "0";
    public string CountdownMinutes { get; set; } = "5";
    public string CountdownSeconds { get; set; } = "00";
    public string QuickPreset1 { get; set; } = "1";
    public string QuickPreset2 { get; set; } = "5";
    public string QuickPreset3 { get; set; } = "10";
    public string QuickPreset4 { get; set; } = "30";
    public string QuickPreset5 { get; set; } = "60";
    public bool LightRingEnabled { get; set; }
    public double LightRingBrightness { get; set; } = 100;
    public double LightRingWidth { get; set; } = 20;
    public bool LightRingHideFromCapture { get; set; }
    public Dictionary<string, OverlayPosition> OverlayPositions { get; set; } = new();
}

public sealed class OverlayPosition
{
    public double Left { get; set; }
    public double Top { get; set; }
}

public static class UserSettingsStore
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "StopwatchOverlay",
        "settings.json");

    public static UserSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
                return JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(SettingsPath)) ?? new UserSettings();
        }
        catch (JsonException) { }
        catch (IOException) { }

        return new UserSettings();
    }

    public static void Save(UserSettings settings)
    {
        try
        {
            var directory = Path.GetDirectoryName(SettingsPath)!;
            Directory.CreateDirectory(directory);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
