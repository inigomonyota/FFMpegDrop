using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;

namespace FfmpegDrop.Models;

public enum JobStatus
{
    Pending,
    Running,
    Analyzed,
    Ok,
    Failed,
    Skipped
}

public sealed class FileJob : INotifyPropertyChanged
{
    // ---- Normalization Data (Notifying Properties) ----
    private LoudnessStats? _loudnessData;
    public LoudnessStats? LoudnessData
    {
        get => _loudnessData;
        set
        {
            if (_loudnessData == value) return;
            _loudnessData = value;
            OnPropertyChanged(nameof(LoudnessData));
            OnPropertyChanged(nameof(HasNormalization));
            // Force UI refresh - the stats formatter watches this
            NotifyPropertyChanged(string.Empty);
        }
    }

    private double? _targetLufs;
    public double? TargetLufs
    {
        get => _targetLufs;
        set
        {
            // treat null vs non-null as "always different"
            if (_targetLufs.HasValue && value.HasValue)
            {
                // adjust tolerance as desired (here: 0.001)
                if (Math.Abs(_targetLufs.Value - value.Value) < 0.001)
                    return;
            }
            else if (_targetLufs == value) // both null
            {
                return;
            }

            _targetLufs = value;
            OnPropertyChanged(nameof(TargetLufs));
            // Force UI refresh
            NotifyPropertyChanged(string.Empty);
        }
    }


    public bool HasNormalization => LoudnessData != null;

    public string Path { get; }

    private double _smoothedSpeed = 0;
    private const double SpeedSmoothingFactor = 0.3;  // 0-1; lower = smoother

    // --- Per-job Log Storage ---
    public StringBuilder LogBuilder { get; } = new();
    public object LogLock { get; } = new();

    // ---------------- Status / message ----------------
    private JobStatus _status = JobStatus.Pending;
    public JobStatus Status
    {
        get => _status;
        set
        {
            if (_status == value) return;
            _status = value;
            OnPropertyChanged(nameof(Status));
            // Update stats string because "Ok" status triggers the Input -> Output comparison view
        }
    }

    private string _lastMessage = "";

    public string FileName => System.IO.Path.GetFileName(Path);
    public string DirectoryName => System.IO.Path.GetDirectoryName(Path) ?? "";

    public string LastMessage
    {
        get => _lastMessage;
        set
        {
            if (_lastMessage == value) return;
            _lastMessage = value;
            OnPropertyChanged(nameof(LastMessage));
        }
    }

    // ---------------- Metadata / Stats (NEW) ----------------
    public MediaProbeInfo? InputInfo { get; private set; }
    public MediaProbeInfo? OutputInfo { get; private set; }

    // Helper to keep the string short and clean
    private static string FormatBitrate(long bps)
    {
        if (bps >= 1_000_000)
            return $"{bps / 1_000_000.0:0.0} Mb/s";

        return $"{bps / 1000.0:0} kb/s";
    }
    public void SetInputInfo(MediaProbeInfo info)
    {
        InputInfo = info;
        // We notify "Self" so the attached property sees a change on the object
        OnPropertyChanged(nameof(InputInfo));
        NotifyPropertyChanged(string.Empty); // Force full refresh for UI binding
    }

    public void SetOutputInfo(MediaProbeInfo info)
    {
        OutputInfo = info;
        OnPropertyChanged(nameof(OutputInfo));
        NotifyPropertyChanged(string.Empty);
    }


    // ---------------- Duration (ffprobe) ----------------
    // IMPORTANT: must be a notifying property (NOT auto-property),
    // otherwise bindings won't update when you set it later async.
    private double? _totalSeconds;
    public double? TotalSeconds
    {
        get => _totalSeconds;
        set
        {
            // If both have values, compare with a tolerance
            if (_totalSeconds.HasValue && value.HasValue)
            {
                // Tolerance: 0.01s (10ms). Adjust if you want it "stickier".
                if (Math.Abs(_totalSeconds.Value - value.Value) < 0.01)
                    return;
            }
            else if (_totalSeconds == value) // both null
            {
                return;
            }

            _totalSeconds = value;
            OnPropertyChanged(nameof(TotalSeconds));
            OnPropertyChanged(nameof(ProgressPercent));
            OnPropertyChanged(nameof(ProgressText));
            OnPropertyChanged(nameof(ETAString));
        }
    }


    // ---------------- Progress (ffmpeg -progress) ----------------
    private double _processedSeconds;
    public double ProcessedSeconds
    {
        get => _processedSeconds;
        set
        {
            // Always accept the final snap-to-end (or any large move)
            bool isSnapToEnd = TotalSeconds.HasValue && Math.Abs(value - TotalSeconds.Value) < 0.01;

            if (!isSnapToEnd && Math.Abs(_processedSeconds - value) < 0.5)
                return;

            _processedSeconds = value;
            OnPropertyChanged(nameof(ProcessedSeconds));
            OnPropertyChanged(nameof(ProgressPercent));
            OnPropertyChanged(nameof(ProgressText));
            OnPropertyChanged(nameof(ETAString));
        }
    }

    public double ProgressPercent
    {
        get
        {
            if (!TotalSeconds.HasValue || TotalSeconds.Value <= 0) return 0;
            var pct = (ProcessedSeconds / TotalSeconds.Value) * 100.0;
            return Math.Clamp(pct, 0.0, 100.0);
        }
    }

    // If you end up removing the % text from the UI, you can also delete this.
    public string ProgressText => $"{ProgressPercent:0.0}%";

    public string ETAString
    {
        get
        {
            if (!TotalSeconds.HasValue || TotalSeconds.Value <= 0) return "";
            if (ProcessedSeconds <= 0) return "";

            if (ProcessedSeconds >= TotalSeconds.Value - 0.01)
                return "Done";

            // Use smoothed speed for ETA (less jittery)
            double remaining = TotalSeconds.Value - ProcessedSeconds;

            if (_smoothedSpeed > 0.01)
            {
                // Estimate time at current smoothed speed
                double estimatedSeconds = remaining / _smoothedSpeed;
                var ts = TimeSpan.FromSeconds(Math.Max(0, estimatedSeconds));
                return ts.TotalHours >= 1 ? ts.ToString(@"h\:mm\:ss") : ts.ToString(@"mm\:ss");
            }

            // Fallback:  assume linear progress (speed not yet stabilized)
            var fallback = TimeSpan.FromSeconds(Math.Max(0, remaining));
            return fallback.TotalHours >= 1 ? fallback.ToString(@"h\:mm\:ss") : fallback.ToString(@"mm\:ss");
        }
    }

    // ---------------- Speed ----------------
    private double _speed;
    public double Speed
    {
        get => _speed;
        set
        {
            if (Math.Abs(_speed - value) < 0.01) return;
            _speed = value;
            OnPropertyChanged(nameof(Speed));
            OnPropertyChanged(nameof(SpeedText));
            OnPropertyChanged(nameof(ETAString));  // ← Refresh ETA when speed changes
        }
    }

    public string SpeedText => Speed > 0 ? $"{Speed:0.00}x" : "";

    /// <summary>
    /// Updates the smoothed speed using exponential moving average. 
    /// Call this when a new raw speed value arrives from ffmpeg.
    /// </summary>
    public void UpdateSmoothedSpeed(double rawSpeed)
    {
        if (rawSpeed > 0.01)
        {
            _smoothedSpeed = (_smoothedSpeed * (1 - SpeedSmoothingFactor))
                           + (rawSpeed * SpeedSmoothingFactor);
        }
    }

    public FileJob(string path)
    {
        Path = path;
        LastMessage = "Pending";
        Status = JobStatus.Pending;

        // optional defaults
        TotalSeconds = null;      // will be filled async by ffprobe
        ProcessedSeconds = 0;     // will be updated by ffmpeg progress
        Speed = 0;
    }

    // ---------------- INotifyPropertyChanged ----------------
    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>
    /// Public method to notify property changes from outside the class.
    /// </summary>
    public void NotifyPropertyChanged(string propertyName) =>
        OnPropertyChanged(propertyName);
}
