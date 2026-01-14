using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FfmpegDrop.Models;

namespace FfmpegDrop.Services;

internal class FfmpegService
{
    private readonly string _appDir;
    private readonly Action<FileJob?, string> _logAction;

    public FfmpegService(string appDir, Action<FileJob?, string> logAction)
    {
        _appDir = appDir;
        _logAction = logAction;
    }

    public static string? ResolveFfmpegExeOrNull(string appDir)
    {
        string local = Path.Combine(appDir, "ffmpeg.exe");
        return File.Exists(local) ? local : null;
    }

    public static string? GetFfmpegVersionString(string exePath)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = "-version",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };

            using var p = Process.Start(psi);
            if (p == null) return null;

            // We only need the first line
            // e.g. "ffmpeg version 8.0.1-essentials... Copyright (c) 2000..."
            string? line = p.StandardOutput.ReadLine();

            // Wait briefly to let it finish cleanly, but don't block long
            p.WaitForExit(500);

            if (string.IsNullOrWhiteSpace(line)) return null;

            // Simple parsing logic
            int vIndex = line.IndexOf("version ", StringComparison.OrdinalIgnoreCase);
            if (vIndex == -1) return null;

            int start = vIndex + "version ".Length;
            int end = line.IndexOfAny(new[] { ' ', '-', '\t' }, start);
            if (end == -1) end = line.Length;

            string version = line.Substring(start, end - start);
            return version;
        }
        catch
        {
            return null;
        }
    }

    public static string EnhanceArgs(string originalArgs, bool showWindow)
    {
        var parts = new List<string>();
        var sb = new StringBuilder();

        // 1. Overwrite handling (-y and -nostdin)
        if (!Regex.IsMatch(originalArgs, @"(^|\s)-n(\s|$)", RegexOptions.IgnoreCase))
        {
            // User didn't explicitly say "never overwrite"
            if (!Regex.IsMatch(originalArgs, @"(^|\s)-y(\s|$)", RegexOptions.IgnoreCase))
                parts.Add("-y");

            if (!Regex.IsMatch(originalArgs, @"(^|\s)-nostdin(\s|$)", RegexOptions.IgnoreCase))
                parts.Add("-nostdin");
        }

        // 2. Progress reporting — only when FFmpeg window is HIDDEN
        if (!showWindow)
        {
            if (!Regex.IsMatch(originalArgs, @"\-progress\s+", RegexOptions.IgnoreCase))
                parts.Add("-progress pipe:1");

            if (!Regex.IsMatch(originalArgs, @"\-nostats", RegexOptions.IgnoreCase))
                parts.Add("-nostats");

            if (!Regex.IsMatch(originalArgs, @"\-hide_banner", RegexOptions.IgnoreCase))
                parts.Add("-hide_banner");
            
            if (!Regex.IsMatch(originalArgs, @"(^|\s)-stats_period(\s|$)", RegexOptions.IgnoreCase))
                parts.Add("-stats_period 0.25"); // 4 updates/sec feels smooth without hammering UI
        }

        // Build final string: injected flags first, then user args
        if (parts.Count > 0)
            sb.Append(string.Join(" ", parts)).Append(' ');

        sb.Append(originalArgs);

        return sb.ToString().Trim();
    }

    public static bool LooksLikeCliError(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return false;
        string s = line.ToLowerInvariant();

        // Things that usually mean the preset / arguments are bad, not the file.
        if (s.Contains("unrecognized option") ||
            s.Contains("unknown option") ||
            s.Contains("option not found") ||
            s.Contains("invalid argument") ||
            s.Contains("error parsing options") ||
            s.Contains("error while parsing option") ||
            (s.Contains("invalid value") && s.Contains("for option")) ||
            s.Contains("no such filter") ||
            s.Contains("unknown filter") ||
            s.Contains("error opening filters") ||
            (s.Contains("filtergraph description") && s.Contains("error")) ||
            s.Contains("unable to find a suitable output format") ||
            s.Contains("unable to parse option value"))
        {
            return true;
        }

        return false;
    }

    public async Task<ProcResult> RunFfmpegAsync(
        string ffmpegExe,
        string args,
        bool showWindow,
        CancellationToken token,
        FileJob job,
        Func<Process, Task> onStarted,
        Action<string>? onProgressLine = null,
        Action<string>? onLogLine = null)
    {
        bool cliErrorDetected = false;

        ProcessStartInfo psi;

        if (showWindow)
        {
            psi = new ProcessStartInfo
            {
                FileName = ffmpegExe,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = false,
                RedirectStandardOutput = false,
                RedirectStandardError = false,
                RedirectStandardInput = false
            };
        }
        else
        {
            psi = new ProcessStartInfo
            {
                FileName = ffmpegExe,
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = false
            };
        }

        using var p = new Process { StartInfo = psi, EnableRaisingEvents = true };

        try
        {
            p.Start();

            // FIFO baton release happens exactly when Process.Start succeeds
            await onStarted(p).ConfigureAwait(false);

            try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }

            Task? progressReadTask = null;

            if (!showWindow)
            {
                // stdout progress (coalesced per ffmpeg "progress=continue/end" burst)
                progressReadTask = Task.Run(async () =>
                {
                    double? pendingSeconds = null;
                    double? pendingSpeed = null;
                    long lastUiUpdateTick = 0;

                    void Flush(bool isEnd)
                    {
                        // Rate-limiting logic
                        long now = Environment.TickCount64;
                        if (!isEnd && (now - lastUiUpdateTick < 250))
                        {
                            return;
                        }
                        lastUiUpdateTick = now;

                        // Capture & reset before dispatch
                        var sec = pendingSeconds;
                        var spd = pendingSpeed;
                        pendingSeconds = null;
                        pendingSpeed = null;

                        if (!sec.HasValue && !spd.HasValue && !isEnd)
                            return;

                        if (sec.HasValue) job.ProcessedSeconds = sec.Value;

                        if (spd.HasValue)
                        {
                            job.Speed = spd.Value;
                            job.UpdateSmoothedSpeed(spd.Value);
                        }

                        if (isEnd && job.TotalSeconds.HasValue)
                            job.ProcessedSeconds = job.TotalSeconds.Value; // snap to 100%
                    }

                    while (!p.StandardOutput.EndOfStream && !token.IsCancellationRequested)
                    {
                        string? line = await p.StandardOutput.ReadLineAsync().ConfigureAwait(false);
                        if (string.IsNullOrWhiteSpace(line))
                            continue;

                        var parts = line.Split('=', 2);
                        if (parts.Length != 2)
                            continue;

                        string key = parts[0].Trim();
                        string value = parts[1].Trim();

                        switch (key)
                        {
                            case "out_time_us":
                                if (long.TryParse(value, out long us))
                                    pendingSeconds = us / 1_000_000.0;
                                break;

                            case "out_time_ms":
                                // Your build reports this in MICROseconds (same as out_time_us).
                                if (long.TryParse(value, out long msLike))
                                    pendingSeconds = msLike / 1_000_000.0;
                                break;

                            case "out_time":
                                if (TimeSpan.TryParse(value, out var ts))
                                    pendingSeconds = ts.TotalSeconds;
                                break;

                            case "speed":
                                var speedStr = value.EndsWith("x", StringComparison.OrdinalIgnoreCase) ? value[..^1] : value;
                                if (double.TryParse(speedStr,
                                    NumberStyles.Any,
                                    CultureInfo.InvariantCulture,
                                    out double s))
                                {
                                    pendingSpeed = s;
                                }
                                break;

                            case "progress":
                                // ✅ ffmpeg burst boundary
                                bool isEnd = value.Equals("end", StringComparison.OrdinalIgnoreCase);
                                Flush(isEnd);
                                break;
                        }
                    }

                    // In case ffmpeg exits without a final "progress=end"
                    Flush(isEnd: false);
                }, token);

                // stderr log + CLI error detection
                p.ErrorDataReceived += (_, e) =>
                {
                    if (e.Data == null) return;

                    string line = e.Data;
                    onLogLine?.Invoke(line);

                    if (LooksLikeCliError(line))
                    {
                        cliErrorDetected = true;
                    }
                };
                p.BeginErrorReadLine();
            }

            using var reg = token.Register(() =>
            {
                try
                {
                    if (!p.HasExited)
                    {
#if NET8_0_OR_GREATER
                        p.Kill(entireProcessTree: true);
#else
                        p.Kill();
#endif
                    }
                }
                catch { }
            });

            await p.WaitForExitAsync(token).ConfigureAwait(false);

            if (progressReadTask != null)
            {
                try { await progressReadTask.ConfigureAwait(false); } catch { }
            }

            return new ProcResult(p.ExitCode, cliErrorDetected);
        }
        catch (OperationCanceledException)
        {
            return new ProcResult(-1, false);
        }
    }

    public async Task<LoudnessStats?> AnalyzeFileLufsAsync(
        string ffmpegExe,
        string inputPath,
        FileJob job,
        double normalizeTargetLufs,
        CancellationToken token,
        Func<Process, Task> onStarted)
    {
        // Use loudnorm with print_format=json for analysis
        string args = $"-hide_banner -nostdin -i \"{inputPath}\" " +
                      $"-af loudnorm=I={normalizeTargetLufs.ToString(CultureInfo.InvariantCulture)}:" +
                      $"TP=-2.0:LRA=7.0:print_format=json " +
                      $"-f null -";

        var stderrBuilder = new StringBuilder();

        var result = await RunFfmpegAsync(
            ffmpegExe,
            args,
            showWindow: false,
            token: token,
            job: job,
            onStarted: onStarted,
            onProgressLine: null,
            onLogLine: line =>
            {
                stderrBuilder.AppendLine(line);
            });

        if (result.ExitCode != 0)
            return null;

        // Parse the loudnorm JSON from stderr
        return ParseLoudnormStats(stderrBuilder.ToString(), job);
    }

    private LoudnessStats? ParseLoudnormStats(string stderr, FileJob job)
    {
        try
        {
            // Find the JSON block (starts with "{" and ends with "}")
            int jsonStart = stderr.LastIndexOf('{');
            int jsonEnd = stderr.LastIndexOf('}');

            if (jsonStart == -1 || jsonEnd == -1 || jsonEnd <= jsonStart)
            {
                _logAction(job, "[NORMALIZE] Could not find JSON stats in FFmpeg output");
                return null;
            }

            string json = stderr.Substring(jsonStart, jsonEnd - jsonStart + 1);

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            double inputI = ParseJsonDouble(root, "input_i");
            double inputTP = ParseJsonDouble(root, "input_tp");
            double inputLRA = ParseJsonDouble(root, "input_lra");
            double inputThresh = ParseJsonDouble(root, "input_thresh");
            double targetOffset = ParseJsonDouble(root, "target_offset");

            _logAction(job, $"[NORMALIZE] Measured:  I={inputI:F2} LUFS, TP={inputTP:F2} dB, LRA={inputLRA:F2} LU");

            return new LoudnessStats(inputI, inputTP, inputLRA, inputThresh, targetOffset);
        }
        catch (Exception ex)
        {
            _logAction(job, $"[NORMALIZE] Failed to parse loudnorm JSON: {ex.Message}");
            return null;
        }
    }

    private static double ParseJsonDouble(JsonElement root, string propertyName)
    {
        if (root.TryGetProperty(propertyName, out var prop))
        {
            string? val = prop.GetString();
            if (double.TryParse(val, NumberStyles.Any,
                CultureInfo.InvariantCulture, out double result))
            {
                return result;
            }
        }
        return 0;
    }

    public async Task<MediaProbeInfo?> GetMediaInfoAsync(string path)
    {
        string ffprobe = Path.Combine(_appDir, "ffprobe.exe");
        if (!File.Exists(ffprobe)) return null;

        var psi = new ProcessStartInfo(ffprobe)
        {
            Arguments = $"-v quiet -print_format json -show_format -show_streams -select_streams v:0 \"{path}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8
        };

        try
        {
            using var p = Process.Start(psi);
            if (p == null) return null;

            // Read both streams to prevent deadlocks
            var stdoutTask = p.StandardOutput.ReadToEndAsync();
            var stderrTask = p.StandardError.ReadToEndAsync();

            // Wait for exit with timeout
            var waitTask = p.WaitForExitAsync();
            if (await Task.WhenAny(waitTask, Task.Delay(5000)) != waitTask)
            {
                try { p.Kill(); } catch { }
                return null;
            }

            string json = await stdoutTask;
            await stderrTask; // Ensure error buffer is drained

            if (string.IsNullOrWhiteSpace(json)) return null;

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // --- Format Section ---
            double duration = 0;
            long size = 0;
            long bitrate = 0;

            if (root.TryGetProperty("format", out var format))
            {
                if (format.TryGetProperty("duration", out var durProp) &&
                    double.TryParse(durProp.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out double d))
                    duration = d;

                if (format.TryGetProperty("size", out var sizeProp) &&
                    long.TryParse(sizeProp.GetString(), out long s))
                    size = s;

                if (format.TryGetProperty("bit_rate", out var brProp) &&
                    long.TryParse(brProp.GetString(), out long br))
                    bitrate = br;
            }

            // --- Stream Section ---
            string codec = "";
            string res = "";
            double fps = 0;
            string audioCodec = "";
            long audioBitrate = 0;

            if (root.TryGetProperty("streams", out var streams) && streams.GetArrayLength() > 0)
            {
                // Parse all streams to find video and audio
                foreach (var stream in streams.EnumerateArray())
                {
                    if (!stream.TryGetProperty("codec_type", out var typeProp))
                        continue;

                    string codecType = typeProp.GetString() ?? "";

                    if (codecType == "video" && string.IsNullOrEmpty(codec))
                    {
                        // Video stream
                        if (stream.TryGetProperty("codec_name", out var c)) codec = c.GetString() ?? "";

                        int w = 0, h = 0;
                        if (stream.TryGetProperty("width", out var wProp)) w = wProp.GetInt32();
                        if (stream.TryGetProperty("height", out var hProp)) h = hProp.GetInt32();
                        if (w > 0 && h > 0) res = $"{w}x{h}";

                        if (stream.TryGetProperty("avg_frame_rate", out var fpsProp))
                        {
                            string val = fpsProp.GetString() ?? "";
                            var parts = val.Split('/');
                            if (parts.Length == 2 &&
                                double.TryParse(parts[0], out double num) &&
                                double.TryParse(parts[1], out double den) && den > 0)
                                fps = num / den;
                            else if (double.TryParse(val, out double flat))
                                fps = flat;
                        }
                    }
                    else if (codecType == "audio" && string.IsNullOrEmpty(audioCodec))
                    {
                        // Audio stream
                        if (stream.TryGetProperty("codec_name", out var ac))
                            audioCodec = ac.GetString() ?? "";

                        if (stream.TryGetProperty("bit_rate", out var abrProp) &&
                            long.TryParse(abrProp.GetString(), out long abr))
                        {
                            audioBitrate = abr;
                        }
                    }
                }
            }

            return new MediaProbeInfo(duration, codec, res, fps, bitrate, size, audioCodec, audioBitrate);
        }
        catch
        {
            return null;
        }
    }
}
