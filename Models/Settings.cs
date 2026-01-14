namespace FfmpegDrop.Models;

internal sealed class Settings
{
    public string? SelectedPresetName { get; set; }
    public bool ShowWindow { get; set; }
    public bool Overwrite { get; set; }
    public bool OutputFolderEnabled { get; set; }
    public string? OutputFolder { get; set; }
    public int Jobs { get; set; } = 2;
    public bool DarkMode { get; set; }
    public string? NormalizeTarget { get; set; }
}
