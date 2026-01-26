namespace FfmpegDrop.Helpers;

internal static class FileUtils
{
    public static readonly HashSet<string> AllowedExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".mov", ".avi",
        ".mp3", ".wav", ".flac", ".m4a", ".aac"
    };

    public static bool IsAllowedFile(string path)
    {
        var ext = Path.GetExtension(path);
        return !string.IsNullOrWhiteSpace(ext) && AllowedExts.Contains(ext);
    }

    public static async Task TryDeleteFileAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        for (int attempt = 0; attempt < 6; attempt++)
        {
            try
            {
                if (!File.Exists(path)) return;
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
                return;
            }
            // Non-blocking wait
            catch (IOException) { await Task.Delay(60).ConfigureAwait(false); }
            catch (UnauthorizedAccessException) { await Task.Delay(60).ConfigureAwait(false); }
            catch { return; }
        }

        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    public static string NormalizePath(string p) =>
        System.IO.Path.GetFullPath(p).TrimEnd('\\', '/');
}
