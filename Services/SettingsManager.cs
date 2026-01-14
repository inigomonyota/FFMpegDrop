using System.Globalization;
using System.Text.Json;
using FfmpegDrop.Models;
using Microsoft.Win32;

namespace FfmpegDrop.Services;

internal static class SettingsManager
{
    public static Settings? LoadSettings(string settingsPath)
    {
        try
        {
            if (!File.Exists(settingsPath))
                return null;

            var json = File.ReadAllText(settingsPath);
            return JsonSerializer.Deserialize<Settings>(json);
        }
        catch
        {
            // ignore corrupt settings
            return null;
        }
    }

    public static void SaveSettings(string settingsPath, Settings settings)
    {
        try
        {
            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(settingsPath, json);
        }
        catch
        {
            // ignore
        }
    }

    public static List<Preset> LoadPresets(string presetsPath)
    {
        try
        {
            if (!File.Exists(presetsPath))
                return new List<Preset>();

            var json = File.ReadAllText(presetsPath);
            var list = JsonSerializer.Deserialize<List<Preset>>(json) ?? new List<Preset>();
            return list.Where(p => !string.IsNullOrWhiteSpace(p.Name)).ToList();
        }
        catch
        {
            // ignore corrupt presets
            return new List<Preset>();
        }
    }

    public static void SavePresets(string presetsPath, List<Preset> presets)
    {
        try
        {
            var json = JsonSerializer.Serialize(presets, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(presetsPath, json);
        }
        catch
        {
            // ignore
        }
    }

    public static bool IsWindowsInDarkMode()
    {
        try
        {
            // Registry key for Personalization
            const string key = @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

            // Value: AppsUseLightTheme (0 = Dark, 1 = Light)
            // We default to 1 (Light) if the key is missing
            var val = Registry.GetValue(key, "AppsUseLightTheme", 1);

            if (val is int i && i == 0)
                return true; // 0 means Dark Mode
        }
        catch
        {
            // If registry read fails, assume Light
        }
        return false;
    }
}
