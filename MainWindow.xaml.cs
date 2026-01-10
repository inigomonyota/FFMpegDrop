using Microsoft.Win32;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Media;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using WF = System.Windows.Forms;

namespace FfmpegDrop;

public partial class MainWindow
{

    private readonly SemaphoreSlim _probeSem = new(2, 2); // keep low (1–3 is plenty)
    private readonly ObservableCollection<FileJob> _queue = new();
    private readonly HashSet<string> _queueSet = new(StringComparer.OrdinalIgnoreCase);

    // ---- Dynamic Jobs (live adjustable) ----
    private const int MaxConcurrentJobs = 4;         // hard cap; UI can still show 1..8 if you want
    private SemaphoreSlim? _jobSem;                    // gate used by running tasks
    private int _activeJobs = 0;
    private int _jobDesired = 2;                       // desired concurrency for this run
    private int _jobDrained = 0;                       // permits currently "held" to enforce a lower limit

    private readonly object _jobAdjustLock = new();
    private Task? _jobAdjustTask;
    private CancellationTokenSource? _jobAdjustCts;

    private readonly SemaphoreSlim _startGate = new(1, 1);
    private long _nextStartTicksUtc = 0; // DateTime.UtcNow.Ticks

    // ---- Auto-follow active jobs in the list ----
    private readonly DispatcherTimer _followActiveTimer;
    private DateTime _suppressFollowUntilUtc = DateTime.MinValue;
    private string? _lastFollowPath = null;

    private readonly AutoResetEvent _throttleChanged = new AutoResetEvent(false);

    private readonly DispatcherTimer _enforceThrottleTimer;
    private volatile bool _enforcePending = false;

    private bool _wasCancelled = false;

    internal enum JobStatus
    {
        Pending,
        Running,
        Ok,
        Failed,
        Skipped
    }

    private void UpdateJobsLiveHint()
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(UpdateJobsLiveHint, DispatcherPriority.Background);
            return;
        }

        if (!_isRunning)
        {
            JobsLiveHint.Text = "";
            JobsLiveHint.ToolTip = null;
            return;
        }

        int desired = Volatile.Read(ref _jobDesired);
        int activeRunning = Volatile.Read(ref _activeRunning);

        string prefix = _isPaused ? "paused" : "live";
        JobsLiveHint.Text = $"{prefix} • {activeRunning}/{desired}";

        JobsLiveHint.ToolTip =
            "You can change Jobs while running.\n" +
            "Increasing applies immediately.\n" +
            "Decreasing pauses the least-progress jobs immediately,\n" +
            "and they resume as slots free up.";
    }

    private static double GetProgress01(FileJob job)
    {
        // Prefer percentage if duration known; otherwise treat as 0 (unknown).
        if (job.TotalSeconds.HasValue && job.TotalSeconds.Value > 0)
        {
            double pct = job.ProcessedSeconds / job.TotalSeconds.Value;
            return Math.Clamp(pct, 0.0, 1.0);
        }
        return 0.0;
    }

    private void RequestEnforceDesiredConcurrency(string? reason = null)
    {
        if (!_isRunning) return;
        _enforcePending = true;
        _throttleChanged.Set();
    }

    private void EnforceDesiredConcurrency(string? reason = null)
    {
        if (!_isRunning) return;

        // If user hit global Pause, keep everything suspended until Resume. 
        if (_isPaused)
        {
            Volatile.Write(ref _activeRunning, 0);
            _ = Dispatcher.BeginInvoke(UpdateJobsLiveHint, DispatcherPriority.Background);
            return;
        }

        int desired = Math.Clamp(Volatile.Read(ref _jobDesired), 1, MaxConcurrentJobs);

        List<Process> all;
        Dictionary<Process, FileJob> mapSnapshot;
        HashSet<Process> throttledSnapshot;
        Dictionary<Process, long> orderSnapshot;

        lock (_procLock)
        {
            // Cleanup any exited processes opportunistically
            _runningProcs.RemoveWhere(p => { try { return p.HasExited; } catch { return true; } });

            all = _runningProcs.ToList();
            mapSnapshot = new Dictionary<Process, FileJob>(_procToJob);
            throttledSnapshot = new HashSet<Process>(_throttledProcs);
            orderSnapshot = new Dictionary<Process, long>(_procOrder);
        }

        // Active running = running procs that are NOT throttled
        int activeRunning = 0;
        foreach (var p in all)
        {
            if (p == null) continue;
            bool exited;
            try { exited = p.HasExited; } catch { exited = true; }
            if (exited) continue;

            if (!throttledSnapshot.Contains(p))
                activeRunning++;
        }

        // If too many active, suspend some immediately (least progress first).
        if (activeRunning > desired)
        {
            int needThrottle = activeRunning - desired;

            var candidates = all
                .Where(p =>
                {
                    try { return p != null && !p.HasExited; } catch { return false; }
                })
                .Where(p => !throttledSnapshot.Contains(p))
                .Select(p =>
                {
                    mapSnapshot.TryGetValue(p, out var job);
                    orderSnapshot.TryGetValue(p, out var order);
                    double prog = job != null ? GetProgress01(job) : 0.0;
                    return new { Proc = p, Job = job, Prog = prog, Order = order };
                })
                // Least done first; tie-break newest started first
                .OrderBy(x => x.Prog)
                .ThenByDescending(x => x.Order)
                .Take(needThrottle)
                .ToList();

            if (candidates.Count > 0)
            {
                foreach (var x in candidates)
                {
                    try
                    {
                        SuspendProcess(x.Proc);
                    }
                    catch { /* ignore */ }

                    lock (_procLock)
                    {
                        _throttledProcs.Add(x.Proc);
                    }

                    if (x.Job != null)
                    {
                        Log($"[AUTO-PAUSE] Throttled to match concurrency limit ({desired}).", x.Job);
                        _ = Dispatcher.BeginInvoke(() =>
                        {
                            if (x.Job.Status == JobStatus.Running)
                                x.Job.LastMessage = $"Throttled (Jobs={desired})";
                        }, DispatcherPriority.Background);
                    }
                }

                Log($"[JOBS] Throttled {candidates.Count} job(s){(string.IsNullOrWhiteSpace(reason) ? "" : $" ({reason})")}");
            }
        }
        // If we have room, resume throttled ones (most progress first).
        else if (activeRunning < desired)
        {
            int needResume = desired - activeRunning;

            List<Process> throttled;
            lock (_procLock) throttled = _throttledProcs.ToList();

            var toResume = throttled
                .Where(p =>
                {
                    try { return p != null && !p.HasExited; } catch { return false; }
                })
                .Select(p =>
                {
                    mapSnapshot.TryGetValue(p, out var job);
                    orderSnapshot.TryGetValue(p, out var order);
                    double prog = job != null ? GetProgress01(job) : 0.0;
                    return new { Proc = p, Job = job, Prog = prog, Order = order };
                })
                // Most done first; tie-break oldest started first
                .OrderByDescending(x => x.Prog)
                .ThenBy(x => x.Order)
                .Take(needResume)
                .ToList();

            if (toResume.Count > 0)
            {
                foreach (var x in toResume)
                {
                    try
                    {
                        ResumeProcess(x.Proc);
                    }
                    catch { /* ignore */ }

                    lock (_procLock)
                    {
                        _throttledProcs.Remove(x.Proc);
                    }

                    if (x.Job != null)
                    {
                        Log("[AUTO-RESUME] Slot opened, resuming.", x.Job);
                        _ = Dispatcher.BeginInvoke(() =>
                        {
                            if (x.Job.Status == JobStatus.Running)
                                x.Job.LastMessage = "Running…";
                        }, DispatcherPriority.Background);
                    }
                }

                Log($"[JOBS] Resumed {toResume.Count} job(s){(string.IsNullOrWhiteSpace(reason) ? "" : $" ({reason})")}");
            }
        }

        // Refresh live count for UI
        int newActiveRunning;
        lock (_procLock)
        {
            newActiveRunning = 0;
            foreach (var p in _runningProcs)
            {
                bool exited;
                try { exited = p.HasExited; } catch { exited = true; }
                if (exited) continue;

                if (!_throttledProcs.Contains(p))
                    newActiveRunning++;
            }
        }

        Volatile.Write(ref _activeRunning, newActiveRunning);
        _ = Dispatcher.BeginInvoke(UpdateJobsLiveHint, DispatcherPriority.Background);

        _throttleChanged.Set();
    }
    private void ArgsGripper_DragDelta(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
    {
        // Adjust height based on mouse drag, limiting range between 60 and 500 pixels
        var newHeight = ArgsBox.Height + e.VerticalChange;
        ArgsBox.Height = Math.Max(60, Math.Min(newHeight, 500));
    }

    private void LogGripper_DragDelta(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
    {
        // We are dragging the 'Top' of the content, but logically extending the height downwards
        // Since this is a bottom drawer, dragging DOWN (positive) should shrink it? 
        // Actually, usually headers stay put. 
        // If the thumb is at the top of the content, dragging UP (negative) increases the height.

        // Calculate new height (Subtract Y because dragging UP should increase height)
        var newHeight = LogRowDef.ActualHeight - e.VerticalChange;

        // Set limits
        if (newHeight < 100) newHeight = 100; // Minimum open size
        if (newHeight > 600) newHeight = 600; // Maximum size

        LogRowDef.Height = new GridLength(newHeight);
    }

    internal sealed class FileJob : INotifyPropertyChanged
    {
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
                if (_totalSeconds == value) return;
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

    private readonly string _appDir = AppContext.BaseDirectory;

    private string PresetsPath => Path.Combine(_appDir, "presets.json");
    private string SettingsPath => Path.Combine(_appDir, "settings.json");

    private readonly List<Preset> _presets = new();

    private bool _isRunning;
    private bool _initializing;

    // Cancel support (multiple processes)
    private readonly object _procLock = new();
    private readonly HashSet<Process> _runningProcs = new();

    // ---- Process ↔ Job mapping (for auto-throttle) ----
    private readonly Dictionary<Process, FileJob> _procToJob = new();
    private readonly HashSet<Process> _throttledProcs = new(); // suspended due to Jobs limit
    private readonly Dictionary<Process, long> _procOrder = new(); // start order for tie-breaks
    private long _procOrderCounter = 0;

    // For the live hint: "active running (not throttled)"
    private int _activeRunning = 0;

    private CancellationTokenSource? _cts;

    // --- UI log batching (prevents UI freeze with multiple jobs) ---
    private readonly ConcurrentQueue<(FileJob? Job, string Message)> _logQueue = new();
    private readonly ConcurrentQueue<string> _failedFiles = new();
    private readonly DispatcherTimer _logFlushTimer;
    private const int MaxLogChars = 1_500_000;  // cap textbox size (~1.5MB)
    private const int FlushMaxLines = 400;      // max lines appended per tick
    private bool _autoScrollLog = true;
    private bool _isDarkTheme = false;

    public MainWindow()
    {

        _initializing = true;

        InitializeComponent();

        _followActiveTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };
        _followActiveTimer.Tick += (_, _) => FollowActiveJobsTick();

        // Any user interaction with the list should temporarily disable auto-follow
        FilesList.PreviewMouseWheel += (_, __) => SuppressFollow(TimeSpan.FromSeconds(2));
        FilesList.PreviewMouseDown += (_, __) => SuppressFollow(TimeSpan.FromSeconds(2));
        FilesList.PreviewKeyDown += (_, __) => SuppressFollow(TimeSpan.FromSeconds(2));
        FilesList.ManipulationStarted += (_, __) => SuppressFollow(TimeSpan.FromSeconds(2));

        SourceInitialized += (s, e) => UseImmersiveDarkMode(this, _isDarkTheme);

        LogRowDef.Height = GridLength.Auto;

        FilesList.ItemsSource = _queue;
        JobsCombo.ItemsSource = Enumerable.Range(1, 4).ToList();

        _logFlushTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(100)
        };
        _logFlushTimer.Tick += (_, _) => FlushLogToUi();
        _logFlushTimer.Start();

        _enforceThrottleTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(75)  // Run at most every 75ms
        };
        _enforceThrottleTimer.Tick += (_, _) =>
        {
            if (_enforcePending && _isRunning)
            {
                _enforcePending = false;
                EnforceDesiredConcurrency("throttled");
            }
        };
        _enforceThrottleTimer.Start();

        LoadPresets();
        if (_presets.Count == 0)
        {
            _presets.Add(new Preset
            {
                Name = "H.264 CRF18 (copy audio)",
                ArgsTemplate = "-hide_banner -y -i \"{in}\" -c:v libx264 -crf 18 -preset veryfast -c:a copy \"{out}\""
            });
            SavePresets();
        }

        PresetCombo.ItemsSource = _presets;
        PresetCombo.DisplayMemberPath = nameof(Preset.Name);

        // Defaults
        ShowWindowCheck.IsChecked = false;
        OverwriteCheck.IsChecked = false;
        OutputFolderCheck.IsChecked = false;
        OutputFolderBox.Text = "";
        JobsCombo.SelectedItem = 2;

        // IMPORTANT: load settings BEFORE forcing a preset selection
        LoadSettings();

        // If settings didn't pick a preset, pick one now
        if (PresetCombo.SelectedItem == null && _presets.Count > 0)
            PresetCombo.SelectedIndex = 0;

        Progress.Minimum = 0;
        Progress.Maximum = 1;
        Progress.Value = 0;

        UpdateOutputFolderUiEnabledState();

        UpdateFfmpegStatus();
        Status("Drop files into the queue.");

        _initializing = false;

        Closing += MainWindow_Closing;

        ShowWindowCheck.Checked += (_, _) => SaveSettings();
        ShowWindowCheck.Unchecked += (_, _) => SaveSettings();

        OverwriteCheck.Checked += (_, _) =>
        {
            UpdateOutputFolderUiEnabledState();
            SaveSettings();
        };
        OverwriteCheck.Unchecked += (_, _) =>
        {
            UpdateOutputFolderUiEnabledState();
            SaveSettings();
        };

        OutputFolderCheck.Checked += (_, _) =>
        {
            UpdateOutputFolderUiEnabledState();
            SaveSettings();
        };
        OutputFolderCheck.Unchecked += (_, _) =>
        {
            UpdateOutputFolderUiEnabledState();
            SaveSettings();
        };

        OutputFolderBox.TextChanged += (_, _) => { if (!_isRunning) SaveSettings(); };
        JobsCombo.SelectionChanged += JobsCombo_SelectionChanged;

    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // 1. Always save settings first
        SaveSettings();

        // 2. Stop the main loop logic
        _cts?.Cancel();
        _jobAdjustCts?.Cancel();

        // 3. Identify running processes safely
        List<Process> procsToKill;
        lock (_procLock)
        {
            procsToKill = _runningProcs.ToList();
            _runningProcs.Clear(); // clear immediately to prevent double-handling
        }

        // 4. Force kill them
        if (procsToKill.Count > 0)
        {
            foreach (var p in procsToKill)
            {
                try
                {
                    if (!p.HasExited)
                    {
                        // Kill the process AND any sub-shells it might have spawned
#if NET8_0_OR_GREATER
                        p.Kill(entireProcessTree: true);
#else
                    p.Kill();
#endif
                    }
                }
                catch
                {
                    // Process might have exited between the check and the kill.
                    // We ignore errors here because we are shutting down anyway.
                }
                finally
                {
                    p.Dispose();
                }
            }
        }
    }

    private void SuppressFollow(TimeSpan forHowLong)
    {
        _suppressFollowUntilUtc = DateTime.UtcNow.Add(forHowLong);
    }

    private void StartFollowingActiveJobs()
    {
        _lastFollowPath = null; // force next tick to scroll
        _followActiveTimer.Start();
    }

    private void StopFollowingActiveJobs()
    {
        _followActiveTimer.Stop();
        _lastFollowPath = null;
    }

    private void FollowActiveJobsTick()
    {
        if (!_isRunning) return;
        if (DateTime.UtcNow < _suppressFollowUntilUtc) return;

        // Find the first running job in list order (this makes the "active block" visible)
        FileJob? target = null;

        // Snapshot safely (ObservableCollection can be modified on UI thread; tick is on UI thread)
        for (int i = 0; i < _queue.Count; i++)
        {
            if (_queue[i].Status == JobStatus.Running)
            {
                target = _queue[i];
                break;
            }
        }

        if (target == null) return;

        // Avoid jitter: only scroll when the first-running job changes
        if (string.Equals(_lastFollowPath, target.Path, StringComparison.OrdinalIgnoreCase))
            return;

        _lastFollowPath = target.Path;

        // Scroll it into view
        FilesList.UpdateLayout();
        FilesList.ScrollIntoView(target);
    }

    private void ToggleTheme_Click(object sender, RoutedEventArgs e)
    {
        _isDarkTheme = !_isDarkTheme;
        SetTheme(_isDarkTheme);

        // NEW: Save immediately
        SaveSettings();
    }

    private void SetTheme(bool isDark)
    {
        // 1. Determine which file to load
        string uri = isDark ? "Themes/Dark.xaml" : "Themes/Light.xaml";

        // 2. Load the dictionary
        // Note: Make sure Build Action for XAML files is "Page" (default)
        var dict = new ResourceDictionary { Source = new Uri(uri, UriKind.Relative) };

        // 3. Clear old theme and add new one
        // This instantly updates all DynamicResources in the app
        Application.Current.Resources.MergedDictionaries.Clear();
        Application.Current.Resources.MergedDictionaries.Add(dict);

        // 4. Force the Windows Title Bar (Minimize/Close area) to update
        UseImmersiveDarkMode(this, isDark);
    }

    // =========================================================
    // WINDOWS 10/11 DARK TITLE BAR MAGIC (P/Invoke)
    // =========================================================
    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1 = 19;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    private static bool UseImmersiveDarkMode(Window window, bool enabled)
    {
        // Don't run in Design Mode
        if (System.ComponentModel.DesignerProperties.GetIsInDesignMode(window)) return false;

        try
        {
            // Get the handle (HWND) of the window
            IntPtr handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            int useImmersiveDarkMode = enabled ? 1 : 0;

            // Attempt to set the DWM attribute (Windows 11 & Windows 10 20H1+)
            if (DwmSetWindowAttribute(handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref useImmersiveDarkMode, sizeof(int)) == 0)
                return true;

            // Fallback for older Windows 10 versions
            if (DwmSetWindowAttribute(handle, DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1, ref useImmersiveDarkMode, sizeof(int)) == 0)
                return true;
        }
        catch
        {
            // Fail silently on older OS (Win 7/8)
        }

        return false;
    }

    private void JobsCombo_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;
        if (JobsCombo.SelectedItem is not int newJobs) return;

        newJobs = Math.Clamp(newJobs, 1, MaxConcurrentJobs);

        if (_isRunning)
            RequestJobConcurrency(newJobs);

        // Always refresh hint (it will blank itself if !_isRunning)
        _ = Dispatcher.BeginInvoke(UpdateJobsLiveHint, DispatcherPriority.Background);

        // Persist preference
        SaveSettings();
    }

    private void RequestJobConcurrency(int newDesired)
    {
        newDesired = Math.Clamp(newDesired, 1, MaxConcurrentJobs);

        var sem = _jobSem;
        var cts = _jobAdjustCts;

        if (sem == null || cts == null) return;
        if (cts.IsCancellationRequested) return;

        bool changed;
        lock (_jobAdjustLock)
        {
            changed = (_jobDesired != newDesired);
            _jobDesired = newDesired;

            if (_jobAdjustTask != null && !_jobAdjustTask.IsCompleted)
            {
                // Adjuster will see new desired; no need to start another. 
            }
            else
            {
                _jobAdjustTask = Task.Run(() => AdjustJobConcurrencyLoopAsync(sem, cts.Token), cts.Token);
            }
        }

        // Immediate effect:  throttle/resume processes right now (NOT throttled).
        if (changed)
            EnforceDesiredConcurrency("jobs changed");  // ← Keep this immediate
    }
    private async Task AdjustJobConcurrencyLoopAsync(SemaphoreSlim sem, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                int desired;
                lock (_jobAdjustLock) desired = _jobDesired;
                desired = Math.Clamp(desired, 1, MaxConcurrentJobs);

                int drained = Volatile.Read(ref _jobDrained);
                int targetDrained = Math.Max(0, MaxConcurrentJobs - desired);

                if (drained < targetDrained)
                {
                    int need = targetDrained - drained;

                    for (int i = 0; i < need; i++)
                    {
                        await sem.WaitAsync(token).ConfigureAwait(false);
                        Interlocked.Increment(ref _jobDrained);
                    }

                    Log($"[JOBS] Set to {desired} (decrease applies as jobs finish)");
                    _ = Dispatcher.BeginInvoke(UpdateJobsLiveHint, DispatcherPriority.Background);
                    continue;
                }

                if (drained > targetDrained)
                {
                    int give = drained - targetDrained;

                    Interlocked.Add(ref _jobDrained, -give);
                    sem.Release(give);

                    Log($"[JOBS] Set to {desired}");
                    _ = Dispatcher.BeginInvoke(UpdateJobsLiveHint, DispatcherPriority.Background);
                    continue;
                }

                // Linger: catch last-moment changes
                int stableDesired = desired;
                await Task.Delay(75, token).ConfigureAwait(false);

                int desiredNow;
                lock (_jobAdjustLock) desiredNow = _jobDesired;

                if (desiredNow == stableDesired)
                    break;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log("[JOBS] Adjust error: " + ex.Message);
        }
    }


    private string GuessDefaultOutputBrowseFolder()
    {
        // 1) If user already set an output folder and it exists, start there.
        var current = OutputFolderBox.Text?.Trim();
        if (!string.IsNullOrWhiteSpace(current) && Directory.Exists(current))
            return current;

        // 2) Otherwise, use the directory of the most recently queued file (that still exists).
        for (int i = _queue.Count - 1; i >= 0; i--)
        {
            var p = _queue[i].Path;
            if (string.IsNullOrWhiteSpace(p)) continue;

            var dir = System.IO.Path.GetDirectoryName(p);
            if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir))
                return dir;
        }

        // 3) Fallback: app directory
        return AppContext.BaseDirectory;
    }


    private static readonly HashSet<string> AllowedExts = new(StringComparer.OrdinalIgnoreCase)
{
    ".mp4", ".mkv", ".mov", ".avi",
    ".mp3", ".wav", ".flac", ".m4a", ".aac"
};

    private static bool IsAllowedFile(string path)
    {
        var ext = Path.GetExtension(path);
        return !string.IsNullOrWhiteSpace(ext) && AllowedExts.Contains(ext);
    }

    // ---------------- Portable settings ----------------
    private sealed class Settings
    {
        public string? SelectedPresetName { get; set; }
        public bool ShowWindow { get; set; }
        public bool Overwrite { get; set; }
        public bool OutputFolderEnabled { get; set; }
        public string? OutputFolder { get; set; }
        public int Jobs { get; set; } = 2;
        public bool DarkMode { get; set; }
    }

    private void LoadSettings()
    {
        try
        {
            // Case A: Settings file exists (Restore user preferences)
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var s = JsonSerializer.Deserialize<Settings>(json);

                if (s != null)
                {
                    // Restore selected preset
                    if (!string.IsNullOrWhiteSpace(s.SelectedPresetName))
                    {
                        var match = _presets.FirstOrDefault(p =>
                            string.Equals(p.Name, s.SelectedPresetName, StringComparison.OrdinalIgnoreCase));
                        if (match != null)
                            PresetCombo.SelectedItem = match;
                    }

                    // Restore Checkboxes
                    ShowWindowCheck.IsChecked = s.ShowWindow;
                    OverwriteCheck.IsChecked = s.Overwrite;
                    OutputFolderCheck.IsChecked = s.OutputFolderEnabled;

                    // Restore Output Folder path
                    OutputFolderBox.Text = s.OutputFolder ?? "";

                    // Trigger logic to enable/disable the output box based on the checkbox
                    OutputFolderCheck_Changed(this, new RoutedEventArgs());

                    // Restore Jobs (clamped to valid UI range)
                    int jobs = Math.Clamp(s.Jobs, 1, 4);
                    JobsCombo.SelectedItem = jobs;

                    // Restore Theme from Settings
                    _isDarkTheme = s.DarkMode;
                }
            }
            // Case B: No settings file (First Run)
            else
            {
                // Detect Windows Theme automatically
                _isDarkTheme = IsWindowsInDarkMode();
            }

            // Apply the theme (Happens for both Case A and Case B)
            SetTheme(_isDarkTheme);
        }
        catch
        {
            // ignore corrupt settings
        }
    }

    static private bool IsWindowsInDarkMode()
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

    private void SaveSettings()
    {
        if (_initializing) return;

        try
        {
            var s = new Settings
            {
                SelectedPresetName = (PresetCombo.SelectedItem as Preset)?.Name,
                ShowWindow = ShowWindowCheck.IsChecked == true,
                Overwrite = OverwriteCheck.IsChecked == true,
                OutputFolderEnabled = OutputFolderCheck.IsChecked == true,
                OutputFolder = OutputFolderBox.Text,
                Jobs = JobsCombo.SelectedItem is int j ? j : 2,

                // NEW:
                DarkMode = _isDarkTheme
            };

            var json = JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsPath, json);
        }
        catch
        {
            // ignore
        }
    }
    // ---------------- FFmpeg detection (must be next to app exe) ----------------
    private string? ResolveFfmpegExeOrNull()
    {
        string exeDir = AppContext.BaseDirectory;
        string local = Path.Combine(exeDir, "ffmpeg.exe");
        return File.Exists(local) ? local : null;
    }

    private void UpdateFfmpegStatus()
    {
        var ff = ResolveFfmpegExeOrNull();
        if (ff == null)
        {
            FfmpegStatusText.Text = "Missing (place ffmpeg.exe next to this app's .exe)";
            FfmpegStatusText.Foreground = System.Windows.Media.Brushes.OrangeRed;
            RunBtn.IsEnabled = false;
        }
        else
        {
            // TRY GET VERSION
            string? ver = GetFfmpegVersionString(ff);

            if (!string.IsNullOrWhiteSpace(ver))
                FfmpegStatusText.Text = $"OK ({ver})";
            else
                FfmpegStatusText.Text = "OK (found ffmpeg.exe next to the app)";

            FfmpegStatusText.Foreground = System.Windows.Media.Brushes.SeaGreen;
            RunBtn.IsEnabled = !_isRunning;
        }

        bool hasFfprobe = File.Exists(Path.Combine(_appDir, "ffprobe.exe"));
        if (!hasFfprobe && File.Exists(ResolveFfmpegExeOrNull()!))
        {
            FfmpegStatusText.Text += " (ffprobe.exe missing → no % progress)";
            // If we found ffmpeg but not ffprobe, switch color to warn
            FfmpegStatusText.Foreground = System.Windows.Media.Brushes.Orange;
        }
    }

    private string? GetFfmpegVersionString(string exePath)
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

            // Start after "version "
            string ver = line.Substring(vIndex + 8).Trim();

            // Cut off at "Copyright" if present
            int cIndex = ver.IndexOf("Copyright", StringComparison.OrdinalIgnoreCase);
            if (cIndex != -1)
            {
                ver = ver.Substring(0, cIndex).Trim();
            }

            return ver;
        }
        catch
        {
            return null;
        }
    }

    private void OpenAppFolder_Click(object sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo("explorer.exe", AppContext.BaseDirectory) { UseShellExecute = true });
    }

    // ---------------- UI helpers ----------------
    private void Status(string msg) => StatusText.Text = msg;

    private void Log(string msg, FileJob? job = null)
    {
        _logQueue.Enqueue((job, msg));
    }

    private void FlushLogToUi()
    {
        if (_logQueue.IsEmpty) return;

        // 1. Get the currently selected job (if any)
        // We snapshot this at the start of the tick
        var selectedJob = FilesList.SelectedItem as FileJob;

        // We'll build a small buffer just for the screen to minimize AppendText calls
        StringBuilder? screenBuffer = null;

        int processed = 0;
        while (processed < FlushMaxLines && _logQueue.TryDequeue(out var item))
        {
            var (job, msg) = item;

            // A. If it's a specific job message, save to that Job's history
            if (job != null)
            {
                lock (job.LogLock)
                {
                    job.LogBuilder.AppendLine(msg);

                    // Optional: Cap memory usage per job (e.g. 500KB)
                    if (job.LogBuilder.Length > 500_000)
                    {
                        job.LogBuilder.Remove(0, job.LogBuilder.Length - 450_000);
                    }
                }
            }

            // B. If this log belongs to the CURRENTLY selected job, 
            //    OR if it's a generic system message (job == null), add to screen buffer.
            if (job == null || job == selectedJob)
            {
                screenBuffer ??= new StringBuilder();
                screenBuffer.AppendLine(msg);
            }

            processed++;
        }

        // 2. Update the UI Textbox
        if (screenBuffer != null && screenBuffer.Length > 0)
        {
            LogBox.AppendText(screenBuffer.ToString());

            // Trim the UI Textbox (Visual only)
            if (LogBox.Text.Length > MaxLogChars)
            {
                LogBox.Text = LogBox.Text.Substring(LogBox.Text.Length - MaxLogChars);
                LogBox.CaretIndex = LogBox.Text.Length;
            }

            if (_autoScrollLog)
                LogBox.ScrollToEnd();
        }
    }

    private void FilesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var selectedJob = FilesList.SelectedItem as FileJob;

        LogBox.Clear();

        if (selectedJob != null)
        {
            // Load history from this job
            lock (selectedJob.LogLock)
            {
                LogBox.Text = selectedJob.LogBuilder.ToString();
            }

            // Optional: Show a hint if empty
            if (LogBox.Text.Length == 0)
            {
                LogBox.Text = $"(Log for {selectedJob.FileName} is empty)";
            }
        }
        else
        {
            LogBox.Text = "Select a file to view its log.";
        }

        // Auto-scroll to bottom of the newly loaded log
        if (_autoScrollLog) LogBox.ScrollToEnd();
    }

    private void OutputFolderCheck_Changed(object sender, RoutedEventArgs e)
    {
        UpdateOutputFolderUiEnabledState();
        if (!_isRunning && !_initializing) SaveSettings();
    }

    private void UpdateOutputFolderUiEnabledState()
    {
        bool overwrite = OverwriteCheck.IsChecked == true;

        // When overwriting originals, output folder is irrelevant.
        if (overwrite)
        {
            // Option A (recommended): force it off so the state is always coherent
            OutputFolderCheck.IsChecked = false;
            OutputFolderCheck.IsEnabled = false;

            OutputFolderBox.IsEnabled = false;
            BrowseOutBtn.IsEnabled = false;
            OutputFolderBox.Opacity = 0.55;
            BrowseOutBtn.Opacity = 0.55;
        }
        else
        {
            OutputFolderCheck.IsEnabled = true;

            bool enabled = OutputFolderCheck.IsChecked == true;
            OutputFolderBox.IsEnabled = enabled;
            BrowseOutBtn.IsEnabled = enabled;

            OutputFolderBox.Opacity = 1.0;
            BrowseOutBtn.Opacity = 1.0;
        }
    }



    // ---------------- Drag/drop ----------------
    private void FilesList_PreviewDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void FilesList_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;

        var files = new List<string>();

        foreach (var p in paths)
        {
            if (File.Exists(p))
            {
                if (IsAllowedFile(p))
                    files.Add(p);
            }
            else if (Directory.Exists(p))
            {
                foreach (var f in Directory.EnumerateFiles(p, "*.*", SearchOption.AllDirectories))
                {
                    if (IsAllowedFile(f))
                        files.Add(f);
                }
            }
        }

        AddFiles(files);
    }

    private void ArgsExpander_Expanded(object sender, RoutedEventArgs e)
    {
        // Snap height when drawer opens
        FitArgsBoxToContent();
    }

    // ---------------- Buttons ----------------
    private void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Multiselect = true,
            Title = "Select files",
            Filter = "Media Files|*.mp4;*.mkv;*.mov;*.avi;*.mp3;*.wav;*.flac;*.m4a;*.aac|All Files|*.*"
        };
        if (dlg.ShowDialog(this) != true) return;
        AddFiles(dlg.FileNames);
    }

    private void AddFiles(IEnumerable<string> files)
    {
        // 1. Filter valid files
        // 2. Distinct prevents adding the same file twice in one drop
        // 3. OrderBy sorts them alphabetically by Path before adding to the UI
        var sortedFiles = files
            .Where(File.Exists)
            .Where(IsAllowedFile)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(f => f); // <--- This fixes the jumbled order

        foreach (var f in sortedFiles)
        {
            // Check against the global queue set to prevent duplicates from previous adds
            if (!_queueSet.Add(f))
                continue;

            var job = new FileJob(f)
            {
                TotalSeconds = null
            };

            _queue.Add(job);

            // async duration probe (won't block UI)
            StartDurationProbe(job);
        }

        Status($"{_queue.Count} file(s) in queue.");
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (_isRunning) return;
        _queue.Clear();
        _queueSet.Clear();
        Status("Queue cleared.");
    }


    private void RemoveSelected_Click(object sender, RoutedEventArgs e)
    {
        if (_isRunning) return;

        var selected = FilesList.SelectedItems.Cast<FileJob>().ToList();
        foreach (var job in selected)
        {
            _queue.Remove(job);
            _queueSet.Remove(job.Path);
        }

        Status($"{_queue.Count} file(s) in queue.");
    }


    private void BrowseOutput_Click(object sender, RoutedEventArgs e)
    {
        using var dlg = new WF.FolderBrowserDialog
        {
            Description = "Choose output folder",
            SelectedPath = GuessDefaultOutputBrowseFolder(),
            ShowNewFolderButton = true
        };

        if (dlg.ShowDialog() == WF.DialogResult.OK)
            OutputFolderBox.Text = dlg.SelectedPath;
    }


    // ---------------- Presets ----------------

    private void PresetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PresetCombo.SelectedItem is Preset p)
        {
            ArgsBox.Text = p.ArgsTemplate;
            if (!_initializing) SaveSettings();

            // Snap height to new text
            FitArgsBoxToContent();
        }
    }

    private void NewPreset_Click(object sender, RoutedEventArgs e)
    {
        if (_isRunning) return;

        // 1. Ask for name
        string? name = PromptText(this, "Enter name for new preset:", "New Preset", "My Custom Preset");
        if (string.IsNullOrWhiteSpace(name)) return;
        name = name.Trim();

        // 2. Check duplicates
        if (_presets.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show(this, "A preset with that name already exists.", "FFmpeg Drop", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // 3. Create BLANK preset
        var newPreset = new Preset
        {
            Name = name,
            ArgsTemplate = "" // User explicitly asked for blank
        };

        _presets.Add(newPreset);
        SavePresets();

        // 4. Update UI
        PresetCombo.Items.Refresh();
        PresetCombo.SelectedItem = newPreset;

        // Focus the box so you can start typing immediately
        ArgsBox.Focus();

        Status($"Created preset '{name}'.");
    }

    // CHANGED: This now saves the CURRENT preset without asking for a name
    // Make sure your XAML button points to "SaveCurrentPreset_Click"
    private void SaveCurrentPreset_Click(object sender, RoutedEventArgs e)
    {
        if (_isRunning) return;
        if (PresetCombo.SelectedItem is not Preset p) return;

        // Overwrite the current preset's args with whatever is in the box
        p.ArgsTemplate = ArgsBox.Text;

        SavePresets();
        Status($"Saved args to preset '{p.Name}'.");
    }

    // ADDED: You need this for the "Rename" button
    private void RenamePreset_Click(object sender, RoutedEventArgs e)
    {
        if (_isRunning) return;
        if (PresetCombo.SelectedItem is not Preset p) return;

        string? newName = PromptText(this, "Enter new name:", "Rename Preset", p.Name);
        if (string.IsNullOrWhiteSpace(newName)) return;
        newName = newName.Trim();

        if (string.Equals(p.Name, newName, StringComparison.Ordinal)) return; // No change

        // Check duplicates (excluding self)
        if (_presets.Any(x => x != p && string.Equals(x.Name, newName, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show(this, "A preset with that name already exists.", "FFmpeg Drop", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        p.Name = newName;
        SavePresets();

        // 1. Refresh the list so the dropdown shows the new name
        PresetCombo.Items.Refresh();

        // 2. FORCE the main text box to update by toggling selection
        // (WPF won't redraw the text otherwise because the object reference 'p' didn't change)
        PresetCombo.SelectedItem = null;
        PresetCombo.SelectedItem = p;

        Status($"Renamed to '{newName}'.");
    }

    private void DeletePreset_Click(object sender, RoutedEventArgs e)
    {
        if (_isRunning) return;
        if (PresetCombo.SelectedItem is not Preset p) return;

        if (_presets.Count <= 1)
        {
            MessageBox.Show(this, "You must keep at least one preset.", "FFmpeg Drop", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (MessageBox.Show(this, $"Are you sure you want to delete '{p.Name}'?", "Delete Preset",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        int idx = PresetCombo.SelectedIndex;
        _presets.Remove(p);
        SavePresets();

        PresetCombo.Items.Refresh();

        // Select the previous item to prevent null selection
        if (idx >= _presets.Count) idx = _presets.Count - 1;
        PresetCombo.SelectedIndex = idx;

        Status("Preset deleted.");
    }

    private void LoadPresets()
    {
        try
        {
            if (!File.Exists(PresetsPath)) return;
            var json = File.ReadAllText(PresetsPath);
            var list = JsonSerializer.Deserialize<List<Preset>>(json) ?? new();
            _presets.Clear();
            _presets.AddRange(list.Where(p => !string.IsNullOrWhiteSpace(p.Name)));
        }
        catch
        {
            // ignore corrupt presets
        }
    }

    private void SavePresets()
    {
        var json = JsonSerializer.Serialize(_presets, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(PresetsPath, json);
    }

    // ---------------- Cancel (kills all running ffmpeg procs) ----------------
    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (!_isRunning) return;

        _wasCancelled = true;

        Status("Cancelling…");
        _cts?.Cancel();

        List<Process> procs;
        lock (_procLock) procs = _runningProcs.ToList();

        foreach (var p in procs)
        {
            try
            {
                if (!p.HasExited)
                {
                    Log("[CANCEL] Killing ffmpeg…");
#if NET8_0_OR_GREATER
                    p.Kill(entireProcessTree: true);
#else
                    p.Kill();
#endif
                }
            }
            catch (Exception ex)
            {
                Log("[CANCEL] Kill failed: " + ex.Message);
            }
        }

        CancelBtn.IsEnabled = false; // prevent spam clicks
    }

    private void FitArgsBoxToContent()
    {
        // Only resize if visible, otherwise width calculations are wrong
        if (ArgsExpander.IsExpanded)
        {
            // Use Dispatcher to ensure layout (width) is updated before measuring
            Dispatcher.BeginInvoke(() =>
            {
                // 1. Unlock height so it expands to fit text
                ArgsBox.Height = double.NaN;
                ArgsBox.UpdateLayout();

                // 2. Measure how tall it wants to be
                double needed = ArgsBox.ActualHeight;

                // 3. Clamp: Min 60px, Max 350px (so it doesn't push buttons off screen)
                double constrained = Math.Clamp(needed, 60, 350);

                // 4. Lock it back to a fixed height (so manual resizing still works later)
                ArgsBox.Height = constrained;
            }, DispatcherPriority.Render);
        }
    }

    private async Task StaggerStartAsync(int staggerMs, CancellationToken token)
    {
        if (staggerMs <= 0) return;

        await _startGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            long now = DateTime.UtcNow.Ticks;
            long next = Volatile.Read(ref _nextStartTicksUtc);

            if (next > now)
            {
                var delay = TimeSpan.FromTicks(next - now);
                await Task.Delay(delay, token).ConfigureAwait(false);
            }

            // After we allow this start, push the next slot forward
            long newNext = DateTime.UtcNow.AddMilliseconds(staggerMs).Ticks;
            Volatile.Write(ref _nextStartTicksUtc, newNext);
        }
        finally
        {
            _startGate.Release();
        }
    }

    private void UpdateTaskbarProgress()
    {
        if (TaskbarInfo == null)
            return;

        // If not running or no jobs, clear taskbar progress.
        if (!_isRunning || Progress.Maximum <= 0)
        {
            TaskbarInfo.ProgressState = TaskbarItemProgressState.None;
            TaskbarInfo.ProgressValue = 0;
            return;
        }

        double ratio = 0.0;
        if (Progress.Maximum > 0)
            ratio = Math.Clamp(Progress.Value / Progress.Maximum, 0.0, 1.0);

        TaskbarInfo.ProgressState = _isPaused
            ? TaskbarItemProgressState.Paused
            : TaskbarItemProgressState.Normal;

        TaskbarInfo.ProgressValue = ratio;
    }

    private void SetOverallProgressCompleted(bool done)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(() => SetOverallProgressCompleted(done), DispatcherPriority.Background);
            return;
        }

        if (done)
        {
            Progress.Foreground = Brushes.SeaGreen; // or LimeGreen, etc.
        }
        else
        {
            // IMPORTANT: restore whatever the theme/style would normally use
            Progress.ClearValue(System.Windows.Controls.Primitives.RangeBase.ForegroundProperty);
            // (Or ProgressBar.ForegroundProperty also works if you prefer)
        }
    }

    private void SetOverallProgressCancelled()
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(SetOverallProgressCancelled, DispatcherPriority.Background);
            return;
        }

        // Pick any red you like
        Progress.Foreground = Brushes.IndianRed;
    }

    // ---------------- Run (parallel jobs) ----------------
    private async void Run_Click(object sender, RoutedEventArgs e)
    {
        if (_isRunning) return;

        _wasCancelled = false;
        UpdateFfmpegStatus();
        string? ffmpegExe = ResolveFfmpegExeOrNull();
        if (ffmpegExe == null)
        {
            MessageBox.Show(this,
                "ffmpeg.exe was not found next to this program.\n\n" +
                "Fix: put ffmpeg.exe in the same folder as the built EXE and try again.\n\n" +
                "Click 'Open App Folder' to open the location.",
                "FFmpeg Drop", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var jobsList = _queue.ToList();
        if (jobsList.Count == 0)
        {
            Status("Queue is empty.");
            return;
        }

        var argsTemplate = ArgsBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(argsTemplate))
        {
            MessageBox.Show(this, "Please enter an FFmpeg args template.", "FFmpeg Drop");
            return;
        }

        if (!ContainsToken(argsTemplate, "{in}") || !ContainsToken(argsTemplate, "{out}"))
        {
            MessageBox.Show(this, "Template must include {in} and {out}.", "FFmpeg Drop");
            return;
        }

        bool overwriteInPlace = OverwriteCheck.IsChecked == true;
        bool showWindow = ShowWindowCheck.IsChecked == true;
        bool outFolderEnabled = OutputFolderCheck.IsChecked == true;

        int jobs = (JobsCombo.SelectedItem is int j) ? j : 2;
        jobs = Math.Clamp(jobs, 1, MaxConcurrentJobs);

        string outFolder = OutputFolderBox.Text.Trim();

        // Output folder only matters when NOT overwriting in place.
        if (!overwriteInPlace && outFolderEnabled)
        {
            if (string.IsNullOrWhiteSpace(outFolder))
            {
                MessageBox.Show(this, "Choose an output folder or uncheck 'Output folder'.", "FFmpeg Drop");
                return;
            }

            try
            {
                Directory.CreateDirectory(outFolder);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Could not create output folder:\n\n" + ex.Message, "FFmpeg Drop");
                return;
            }
        }

        // Output folder writeability check
        if (!overwriteInPlace && outFolderEnabled && !string.IsNullOrWhiteSpace(outFolder))
        {
            try
            {
                string testFile = System.IO.Path.Combine(outFolder, ".fftest");
                File.WriteAllText(testFile, "");
                File.Delete(testFile);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this,
                    $"Output folder is not accessible or not writable:\n\n{outFolder}\n\n{ex.Message}",
                    "FFmpeg Drop", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }

        // Persist current settings (args, jobs, etc.)
        SaveSettings();

        // 🔍 VALIDATE FFMPEG ARGS BEFORE STARTING BATCH
        Status("Validating FFmpeg arguments...");
        RunBtn.IsEnabled = false;
        CancelBtn.IsEnabled = false;
        PauseBtn.IsEnabled = false;

        bool valid = await ValidateFfmpegArgsAsync(ffmpegExe, argsTemplate);
        if (!valid)
        {
            Status("FFmpeg argument validation failed. See log for details.");
            MessageBox.Show(
                this,
                "FFmpeg could not run with the current arguments.\n\n" +
                "No files were processed.\n\n" +
                "Check the log window for details, fix the FFmpeg template, then try again.",
                "FFmpeg Argument Validation Failed",
                MessageBoxButton.OK,
                MessageBoxImage.Error);


            RunBtn.IsEnabled = true;
            return;
        }

        // ---- UI: entering run ----
        _isRunning = true;
        RunBtn.IsEnabled = false;
        CancelBtn.IsEnabled = true;
        PauseBtn.IsEnabled = true;

        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        StartFollowingActiveJobs();

        Progress.Minimum = 0;
        Progress.Maximum = jobsList.Count;
        Progress.Value = 0;

        SetOverallProgressCompleted(false);

        UpdateTaskbarProgress();

        LogBox.Clear();
        Log("=== Run ===");
        Log($"[Jobs] {jobs}");
        if (showWindow) Log("[Note] FFmpeg window is visible; live log capture is disabled.");

        while (_failedFiles.TryDequeue(out _)) { }

        int ok = 0, fail = 0, done = 0;

        // ---- Dynamic jobs gate (drain-based) ----
        _jobDesired = Math.Clamp(jobs, 1, MaxConcurrentJobs);
        _jobDrained = 0;

        // Start fully open (Max permits), then "drain" down to desired.
        _jobSem = new SemaphoreSlim(MaxConcurrentJobs, MaxConcurrentJobs);

        int initialDrain = MaxConcurrentJobs - _jobDesired;
        for (int i = 0; i < initialDrain; i++)
        {
            _jobSem.Wait(); // won't block; starts at MaxConcurrentJobs
            Interlocked.Increment(ref _jobDrained);
        }

        // Cancels any in-flight drain loops when run ends
        _jobAdjustCts = CancellationTokenSource.CreateLinkedTokenSource(token);

        // Live hint tracking
        Interlocked.Exchange(ref _activeJobs, 0);

        // Now the hint is coherent (after sem + drain is established)
        _ = Dispatcher.BeginInvoke(UpdateJobsLiveHint, DispatcherPriority.Background);

        // Track temp outputs created this run, so we can sweep-delete anything left behind.
        var runTempFiles = new ConcurrentBag<string>();

        // Reserve final output names across parallel jobs to avoid collisions.
        var reservedFinalOutputs = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);

        void SetJob(FileJob job, JobStatus status, string message)
        {
            _ = Dispatcher.BeginInvoke(() =>
            {
                job.Status = status;
                job.LastMessage = message;
            }, DispatcherPriority.Background);
        }

        foreach (var job in jobsList)
        {
            job.Status = JobStatus.Pending;
            job.LastMessage = "Pending";
            job.ProcessedSeconds = 0;
            job.Speed = 0;
        }

        // Rename it to 'Async' to follow convention
        static async Task TryDeleteFileAsync(string path)
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

        static string NormalizePath(string p) =>
            System.IO.Path.GetFullPath(p).TrimEnd('\\', '/');

        string ReserveUniqueFinalPath(string preferredFullPath)
        {
            string dir = System.IO.Path.GetDirectoryName(preferredFullPath)!;
            string name = System.IO.Path.GetFileNameWithoutExtension(preferredFullPath);
            string ext = System.IO.Path.GetExtension(preferredFullPath);

            for (int i = 1; i < 100000; i++)
            {
                string candidate = (i == 1)
                    ? preferredFullPath
                    : System.IO.Path.Combine(dir, $"{name}_{i}{ext}");

                if (!File.Exists(candidate) && reservedFinalOutputs.TryAdd(candidate, 0))
                    return candidate;
            }

            string guid = System.IO.Path.Combine(dir, $"{name}_{Guid.NewGuid():N}{ext}");
            reservedFinalOutputs.TryAdd(guid, 0);
            return guid;
        }


        try
        {
            // ---------------- FIFO START BATON ----------------
            // job i waits for startTurn[i]; when it successfully STARTS ffmpeg, it releases startTurn[i+1].
            var startTurn = new TaskCompletionSource<bool>[jobsList.Count + 1];
            for (int i = 0; i < startTurn.Length; i++)
                startTurn[i] = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            startTurn[0].TrySetResult(true); // first job may start

            // ---------------- Queue (keeps original order) ----------------
            var ch = Channel.CreateUnbounded<(FileJob Job, int Index)>(new UnboundedChannelOptions
            {
                SingleWriter = true,
                SingleReader = false,
                AllowSynchronousContinuations = false
            });

            for (int i = 0; i < jobsList.Count; i++)
                ch.Writer.TryWrite((jobsList[i], i));

            ch.Writer.Complete();

            bool IsStartAllowedNow()
            {
                if (_isPaused) return false;
                lock (_procLock) return _throttledProcs.Count == 0;
            }

            async Task HandleOneJobAsync((FileJob Job, int Index) item)
            {
                var job = item.Job;
                int index = item.Index;

                bool acquired = false;
                bool releasedNext = false;

                string input = job.Path;
                string fileLabel = System.IO.Path.GetFileName(input);

                string? tempOutForCleanup = null;

                void ReleaseNextStart()
                {
                    if (releasedNext) return;
                    releasedNext = true;
                    startTurn[index + 1].TrySetResult(true);
                }

                // ---------------------------------------------------------
                // 1. CLEAR LOG ON START
                // ---------------------------------------------------------
                lock (job.LogLock)
                {
                    job.LogBuilder.Clear();
                }
                // If this job is currently shown in the UI, clear the visible box too
                _ = Dispatcher.BeginInvoke(() =>
                {
                    if (FilesList.SelectedItem == job) LogBox.Clear();
                });
                // ---------------------------------------------------------

                try
                {
                    // Cheap early-outs that should NOT consume a job slot.
                    if (token.IsCancellationRequested)
                    {
                        SetJob(job, JobStatus.Skipped, "Cancelled");
                        ReleaseNextStart();
                        return;
                    }

                    if (!System.IO.File.Exists(input))
                    {
                        Log($"[SKIP] Missing: {input}", job);
                        _failedFiles.Enqueue($"[MISSING] {input}");
                        Interlocked.Increment(ref fail);
                        SetJob(job, JobStatus.Skipped, "Missing file");
                        ReleaseNextStart();
                        return;
                    }

                    if (!IsAllowedFile(input))
                    {
                        Log($"[SKIP] Unsupported type: {input}", job); // Added job param
                        _failedFiles.Enqueue($"[UNSUPPORTED] {input}");
                        Interlocked.Increment(ref fail);
                        SetJob(job, JobStatus.Skipped, "Unsupported type");
                        ReleaseNextStart();
                        return;
                    }

                    // ✅ STRICT FIFO: do not allow starting ffmpeg until it's our turn.
                    await startTurn[index].Task.ConfigureAwait(false);

                    // Acquire a slot, but DO NOT start a new ffmpeg while paused
                    // or while any throttled processes exist.
                    while (true)
                    {
                        await WaitUntilAllowedToStartNewJobAsync(token).ConfigureAwait(false);

                        await _jobSem!.WaitAsync(token).ConfigureAwait(false);
                        acquired = true;

                        // Re-check after acquire (pause/throttle can flip between the await and the permit).
                        if (!IsStartAllowedNow())
                        {
                            acquired = false;
                            _jobSem.Release();
                            continue;
                        }

                        break;
                    }

                    // We count "active jobs" as "permits in use" (even if paused).
                    Interlocked.Increment(ref _activeJobs);

                    // IMPORTANT: schedule throttle/resume enforcement (runs on timer)
                    RequestEnforceDesiredConcurrency("job acquired");

                    _ = Dispatcher.BeginInvoke(UpdateJobsLiveHint, DispatcherPriority.Background);

                    if (token.IsCancellationRequested)
                    {
                        SetJob(job, JobStatus.Skipped, "Cancelled");
                        ReleaseNextStart(); // not started
                        return;
                    }

                    SetJob(job, JobStatus.Running, "Running…");

                    string inDir = System.IO.Path.GetDirectoryName(input)!;
                    string inName = System.IO.Path.GetFileNameWithoutExtension(input);
                    string inExt = System.IO.Path.GetExtension(input);

                    string desiredOutDir = inDir;
                    if (!overwriteInPlace && outFolderEnabled && !string.IsNullOrWhiteSpace(outFolder))
                        desiredOutDir = outFolder;

                    bool sameDir = string.Equals(NormalizePath(desiredOutDir), NormalizePath(inDir), StringComparison.OrdinalIgnoreCase);

                    string finalOut;
                    if (overwriteInPlace)
                    {
                        finalOut = input;
                    }
                    else
                    {
                        string baseName = sameDir ? $"{inName}_out{inExt}" : System.IO.Path.GetFileName(input);
                        string preferredFinal = System.IO.Path.Combine(desiredOutDir, baseName);
                        finalOut = ReserveUniqueFinalPath(preferredFinal);
                    }

                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(finalOut)!);

                    string finalDir = System.IO.Path.GetDirectoryName(finalOut)!;
                    string finalName = System.IO.Path.GetFileNameWithoutExtension(finalOut);
                    string finalExt = System.IO.Path.GetExtension(finalOut);

                    string tempOut = overwriteInPlace
                        ? System.IO.Path.Combine(inDir, $"{inName}.__fftmp__{Guid.NewGuid():N}{inExt}")
                        : System.IO.Path.Combine(finalDir, $"{finalName}.__fftmp__{Guid.NewGuid():N}{finalExt}");

                    tempOutForCleanup = tempOut;
                    runTempFiles.Add(tempOut);

                    string userArgs = argsTemplate
                        .Replace("{in}", input, StringComparison.OrdinalIgnoreCase)
                        .Replace("{out}", tempOut, StringComparison.OrdinalIgnoreCase)
                        .Replace("{dir}", inDir, StringComparison.OrdinalIgnoreCase)
                        .Replace("{name}", inName, StringComparison.OrdinalIgnoreCase)
                        .Replace("{ext}", inExt, StringComparison.OrdinalIgnoreCase);

                    string finalArgs = EnhanceArgs(userArgs, showWindow);

                    Log($"> ffmpeg {finalArgs}", job);

                    // ✅ Release FIFO baton only when ffmpeg actually starts
                    var result = await RunFfmpegAsync(
                        ffmpegExe,
                        finalArgs,
                        showWindow,
                        token,
                        job,
                        onStarted: ReleaseNextStart,
                        onProgressLine: null,
                        onLogLine: line => Log(line, job)
                    ).ConfigureAwait(false);

                    if (token.IsCancellationRequested || result.ExitCode == -1)
                    {
                        await TryDeleteFileAsync(tempOut).ConfigureAwait(false);
                        Log("\n[CANCELLED] User stopped the process.", job); // Added cancel log
                        SetJob(job, JobStatus.Skipped, "Cancelled");
                        return;
                    }

                    if (result.ExitCode == 0)
                    {
                        // 1. Determine where the final file actually is
                        string actualFinalPath = "";

                        if (overwriteInPlace)
                        {
                            try
                            {
                                string backup = System.IO.Path.Combine(inDir, $"{inName}.__backup__{inExt}");
                                if (System.IO.File.Exists(backup)) System.IO.File.Delete(backup);

                                System.IO.File.Replace(tempOut, input, backup, ignoreMetadataErrors: true);

                                if (System.IO.File.Exists(backup)) System.IO.File.Delete(backup);

                                actualFinalPath = input; // The input path is now the new file
                            }
                            catch (Exception ex)
                            {
                                Log($"[{fileLabel}] [FAIL] Replace failed: {ex.Message}", job);
                                _failedFiles.Enqueue($"[REPLACE] {input} :: {ex.Message}");
                                Interlocked.Increment(ref fail);
                                await TryDeleteFileAsync(tempOut).ConfigureAwait(false);
                                SetJob(job, JobStatus.Failed, "Replace failed: " + ex.Message);
                                return;
                            }
                        }
                        else
                        {
                            try
                            {
                                System.IO.File.Move(tempOut, finalOut);
                                actualFinalPath = finalOut; // The destination is the new file
                            }
                            catch (Exception ex)
                            {
                                Log($"[{fileLabel}] [FAIL] Move failed: {ex.Message}", job);
                                _failedFiles.Enqueue($"[MOVE] {input} -> {finalOut} :: {ex.Message}");
                                Interlocked.Increment(ref fail);
                                await TryDeleteFileAsync(tempOut).ConfigureAwait(false);
                                SetJob(job, JobStatus.Failed, "Move failed: " + ex.Message);
                                return;
                            }
                        }


                        // ---------------------------------------------------------
                        // ✅ PROBE OUTPUT (Throttled & Robust Version)
                        // ---------------------------------------------------------
                        // ---------------------------------------------------------
                        // ✅ PROBE OUTPUT (Semaphore Removed for Speed)
                        // ---------------------------------------------------------
                        MediaProbeInfo? outInfo = null;

                        // 1. Retry Loop: Try for up to 2.5 seconds
                        // We do NOT use _probeSem here. We want this to run immediately.
                        for (int i = 0; i < 10; i++)
                        {
                            try
                            {
                                await Task.Delay(100 + (i * 50));

                                outInfo = await GetMediaInfoAsync(actualFinalPath);
                                if (outInfo != null) break;
                            }
                            catch { /* ignore and retry */ }
                        }

                        // 2. Fallback: If FFprobe failed, force a basic FileInfo check.
                        if (outInfo == null)
                        {
                            try
                            {
                                var fi = new FileInfo(actualFinalPath);
                                fi.Refresh();

                                if (fi.Exists)
                                {
                                    double d = job.InputInfo?.Duration ?? 0;
                                    double f = job.InputInfo?.Fps ?? 0;

                                    outInfo = new MediaProbeInfo(d, "", "", f, 0, fi.Length);
                                    Log($"[WARN] Probe failed (Process Error), used fallback size.", job);
                                }
                            }
                            catch { /* Truly failed */ }
                        }

                        // 3. Update the UI
                        if (outInfo != null)
                        {
                            await Dispatcher.InvokeAsync(() => job.SetOutputInfo(outInfo));
                        }
                        else
                        {
                            Log($"[ERROR] Could not retrieve output stats for: {fileLabel}", job);
                        }
                        // ---------------------------------------------------------

                        Log($"[{fileLabel}] [OK]", job);
                        Interlocked.Increment(ref ok);
                        tempOutForCleanup = null;

                        SetJob(job, JobStatus.Ok, "OK");
                    }
                    else
                    {
                        Log($"[{fileLabel}] [FAIL] ExitCode={result.ExitCode}", job); // Added job param
                        _failedFiles.Enqueue($"[FFMPEG] {input} :: ExitCode={result.ExitCode}");
                        Interlocked.Increment(ref fail);

                        await TryDeleteFileAsync(tempOut).ConfigureAwait(false);
                        SetJob(job, JobStatus.Failed, $"ffmpeg ExitCode={result.ExitCode}");
                    }
                }
                catch (OperationCanceledException)
                {
                    SetJob(job, JobStatus.Skipped, "Cancelled");
                    if (tempOutForCleanup != null) await TryDeleteFileAsync(tempOutForCleanup).ConfigureAwait(false);

                    // If we never started, don't deadlock the chain
                    ReleaseNextStart();
                }
                catch (Exception ex)
                {
                    Log($"[{fileLabel}] [EXCEPTION] {ex}", job); // Added job param
                    _failedFiles.Enqueue($"[EXCEPTION] {input} :: {ex.GetType().Name}: {ex.Message}");
                    Interlocked.Increment(ref fail);

                    if (tempOutForCleanup != null) await TryDeleteFileAsync(tempOutForCleanup).ConfigureAwait(false);
                    SetJob(job, JobStatus.Failed, $"{ex.GetType().Name}: {ex.Message}");

                    // If we never started, don't deadlock the chain
                    ReleaseNextStart();
                }
                finally
                {
                    if (acquired)
                    {
                        Interlocked.Decrement(ref _activeJobs);
                        _jobSem!.Release();
                        _ = Dispatcher.BeginInvoke(UpdateJobsLiveHint, DispatcherPriority.Background);
                    }

                    int d = Interlocked.Increment(ref done);
                    int okNow = Volatile.Read(ref ok);
                    int failNow = Volatile.Read(ref fail);

                    _ = Dispatcher.BeginInvoke(() =>
                    {
                        Progress.Value = d;
                        Status($"Running… OK={okNow} FAIL={failNow} ({d}/{jobsList.Count})");
                        UpdateTaskbarProgress();
                    }, DispatcherPriority.Background);
                }
            }

            async Task WorkerAsync()
            {
                while (await ch.Reader.WaitToReadAsync(token).ConfigureAwait(false))
                {
                    while (ch.Reader.TryRead(out var item))
                    {
                        await HandleOneJobAsync(item).ConfigureAwait(false);
                    }
                }
            }

            _ = Dispatcher.BeginInvoke(() =>
            {
                Status($"Running… OK=0 FAIL=0 (0/{jobsList.Count})");
            }, DispatcherPriority.Background);

            int workerCount = Math.Min(MaxConcurrentJobs, jobsList.Count);
            var workers = Enumerable.Range(0, workerCount).Select(_ => WorkerAsync()).ToArray();

            try
            {
                await Task.WhenAll(workers).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Log("[CANCELLED] Batch stopped.");
            }
            catch (Exception ex)
            {
                Log("[FATAL] One or more workers faulted: " + ex);
            }

            if (fail > 0)
            {
                Log("=== Failed files ===");

                const int maxToShow = 500;
                var all = _failedFiles.ToArray();

                foreach (var f in all.Take(maxToShow))
                    Log("FAIL: " + f);

                if (all.Length > maxToShow)
                    Log($"(More failures not shown: {all.Length - maxToShow})");

                Log("=== End failed files ===");
            }
        }
        finally
        {
            // 1) Stop any in-flight adjuster and wait for it to exit BEFORE disposing sem
            try { _jobAdjustCts?.Cancel(); } catch { }

            Task? adjustTask = null;
            lock (_jobAdjustLock) adjustTask = _jobAdjustTask;

            if (adjustTask != null)
            {
                try { await adjustTask.ConfigureAwait(false); }
                catch { /* ignore */ }
            }

            _jobAdjustCts?.Dispose();
            _jobAdjustCts = null;

            // Clear adjust task reference
            lock (_jobAdjustLock) _jobAdjustTask = null;

            // 2) Dispose the job semaphore (no more gating)
            _jobSem?.Dispose();
            _jobSem = null;

            // 3) Mark run as finished
            _isRunning = false;

            // Reset live job tracking
            Interlocked.Exchange(ref _activeJobs, 0);

            // 4) Cancel token source cleanup
            _cts?.Dispose();
            _cts = null;

            // 5) Final sweep: delete any temp files still hanging around from this run
            foreach (var tmp in runTempFiles)
                await TryDeleteFileAsync(tmp).ConfigureAwait(false);

            // 6) Dispose running processes list
            lock (_procLock)
            {
                foreach (var p in _runningProcs.ToList())
                {
                    try { p.Dispose(); } catch { }
                }
                _runningProcs.Clear();
            }

            int okNow = Volatile.Read(ref ok);
            int failNow = Volatile.Read(ref fail);

            // 7) UI reset
            _ = Dispatcher.BeginInvoke(() =>
            {
                CancelBtn.IsEnabled = false;
                PauseBtn.IsEnabled = false;
                PauseBtn.Content = "Pause";
                _isPaused = false;

                RunBtn.IsEnabled = true;
                UpdateFfmpegStatus();

                if (_wasCancelled)
                {
                    // User explicitly cancelled: don't turn the bar green, just say "Cancelled".
                    Status("Cancelled.");
                    SetOverallProgressCancelled();
                }
                else
                {
                    // Completed run (even with some FAILs) → mark as done and turn bar green.
                    Status($"Done. OK={okNow}, FAIL={failNow}");
                    SetOverallProgressCompleted(true);

                    // Play a Windows "ding" only on true completion, not on cancel.
                    try
                    {
                        SystemSounds.Asterisk.Play();
                    }
                    catch
                    {
                        // ignore sound errors
                    }
                }

                // Clear taskbar progress either way.
                if (TaskbarInfo != null)
                {
                    TaskbarInfo.ProgressState = TaskbarItemProgressState.None;
                    TaskbarInfo.ProgressValue = 0;
                }

                // Clear/refresh the hint now that _isRunning is false
                UpdateJobsLiveHint();
            }, DispatcherPriority.Background);

            StopFollowingActiveJobs();

            Log($"=== Done. OK={okNow}, FAIL={failNow} ===");
        }
    }

    private async Task<bool> ValidateFfmpegArgsAsync(string ffmpegExe, string argsTemplate)
    {
        // Offload to a background thread so we don't block the UI for up to 15 seconds
        return await Task.Run(() =>
        {
            string tempDir = Path.GetTempPath();
            string testInputFile = Path.Combine(tempDir, $"ffmpeg_test_input_{Guid.NewGuid():N}.mp4");
            string testOutputFile = Path.Combine(tempDir, $"ffmpeg_test_output_{Guid.NewGuid():N}.mp4");

            try
            {
                // Step 1: Create a tiny test input file using lavfi
                Log("[VALIDATION] Creating test input file...");

                // '-y' so we don't get stuck on overwrite prompts
                string genArgs = "-hide_banner -y -f lavfi -i color=c=black:s=320x240:d=0.1 "
                               + "\"" + testInputFile + "\"";

                var genPsi = new ProcessStartInfo
                {
                    FileName = ffmpegExe,
                    Arguments = genArgs,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = false  // we don't need stdout here
                };

                using (var genProc = Process.Start(genPsi))
                {
                    if (genProc == null)
                    {
                        Log("[VALIDATION] Failed to start ffmpeg for test file creation");
                        return false;
                    }

                    bool genCompleted = genProc.WaitForExit(5000);
                    string genStderr = genProc.StandardError.ReadToEnd();

                    if (!genCompleted)
                    {
                        try { genProc.Kill(); } catch { }
                        Log("[VALIDATION] Test file creation timed out");
                        return false;
                    }

                    if (genProc.ExitCode != 0)
                    {
                        Log("[VALIDATION] Failed to create test input file (exit code " + genProc.ExitCode + ")");
                        if (!string.IsNullOrWhiteSpace(genStderr))
                            Log("[VALIDATION] ffmpeg stderr (create): " + genStderr);
                        return false;
                    }
                }

                if (!File.Exists(testInputFile))
                {
                    Log("[VALIDATION] Test input file was not created");
                    return false;
                }

                // Step 2: Run user's template against the test file
                Log("[VALIDATION] Testing user arguments...");

                string inDir = tempDir;
                string inName = "test";
                string inExt = Path.GetExtension(testInputFile); // ".mp4"

                string userArgs = argsTemplate
                    .Replace("{in}", testInputFile, StringComparison.OrdinalIgnoreCase)
                    .Replace("{out}", testOutputFile, StringComparison.OrdinalIgnoreCase)
                    .Replace("{dir}", inDir, StringComparison.OrdinalIgnoreCase)
                    .Replace("{name}", inName, StringComparison.OrdinalIgnoreCase)
                    .Replace("{ext}", inExt, StringComparison.OrdinalIgnoreCase);

                // Don't auto-add progress flags for validation
                string finalArgs = EnhanceArgs(userArgs, showWindow: true);

                Log($"[VALIDATION] Running: ffmpeg {finalArgs}");

                var psi = new ProcessStartInfo
                {
                    FileName = ffmpegExe,
                    Arguments = finalArgs,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = false   // avoid deadlock risk
                };

                using (var p = Process.Start(psi))
                {
                    if (p == null)
                    {
                        Log("[VALIDATION] Failed to start ffmpeg for template validation");
                        return false;
                    }

                    bool completed = p.WaitForExit(15000);
                    string stderrOutput = p.StandardError.ReadToEnd();

                    if (!completed)
                    {
                        try { p.Kill(); } catch { }
                        Log("[VALIDATION] Template test timed out");
                        return false;
                    }

                    if (p.ExitCode != 0)
                    {
                        Log("[VALIDATION] ✗ FFmpeg exited with error code: " + p.ExitCode);
                        if (!string.IsNullOrWhiteSpace(stderrOutput))
                        {
                            Log("[VALIDATION] Error output:");
                            Log(stderrOutput);
                        }
                        return false;
                    }

                    Log("[VALIDATION] ✓ Arguments validated successfully");
                    return true;
                }
            }
            catch (Exception ex)
            {
                Log($"[VALIDATION] Exception: {ex.Message}");
                return false;
            }
            finally
            {
                // Clean up test files
                try { if (File.Exists(testInputFile)) File.Delete(testInputFile); } catch { }
                try { if (File.Exists(testOutputFile)) File.Delete(testOutputFile); } catch { }
            }
        });
    }


    private static bool ContainsToken(string s, string token)
        => s.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0;

    private string EnhanceArgs(string originalArgs, bool showWindow)
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

    private async Task<ProcResult> RunFfmpegAsync(
    string ffmpegExe,
    string args,
    bool showWindow,
    CancellationToken token,
    FileJob job,
    Action? onStarted = null,
    Action<string>? onProgressLine = null,   // keep for debug/log if you want
    Action<string>? onLogLine = null)
    {
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
            await StaggerStartAsync(200, token).ConfigureAwait(false);

            p.Start();

            // FIFO baton release happens exactly when Process.Start succeeds
            onStarted?.Invoke();

            try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }

            // Register this proc for cancel + throttle decisions
            lock (_procLock)
            {
                _runningProcs.Add(p);
                _procToJob[p] = job;

                long order = Interlocked.Increment(ref _procOrderCounter);
                _procOrder[p] = order;
            }

            RequestEnforceDesiredConcurrency("proc start");

            Task? progressReadTask = null;

            if (!showWindow)
            {
                // stdout progress (coalesced per ffmpeg "progress=continue/end" burst)
                progressReadTask = Task.Run(async () =>
                {
                    double? pendingSeconds = null;
                    double? pendingSpeed = null;
                    long lastUiUpdateTick = 0; // Initialize the timestamp tracker

                    void Flush(bool isEnd)
                    {
                        // Rate-limiting logic: Only update UI if it's the end,
                        // OR if enough time has passed since the last UI update.
                        // Adjust 250ms (250,000 ticks) as needed.
                        long now = Environment.TickCount64;
                        if (!isEnd && (now - lastUiUpdateTick < 250))
                        {
                            return; // Not enough time has passed, skip UI update
                        }
                        lastUiUpdateTick = now; // Update the timestamp for the next check

                        // Capture & reset before dispatch
                        var sec = pendingSeconds;
                        var spd = pendingSpeed;
                        pendingSeconds = null;
                        pendingSpeed = null;

                        if (!sec.HasValue && !spd.HasValue && !isEnd)
                            return;

                        _ = Dispatcher.BeginInvoke(() =>
                        {
                            if (sec.HasValue) job.ProcessedSeconds = sec.Value;

                            if (spd.HasValue)
                            {
                                job.Speed = spd.Value;
                                job.UpdateSmoothedSpeed(spd.Value);
                            }

                            if (isEnd && job.TotalSeconds.HasValue)
                                job.ProcessedSeconds = job.TotalSeconds.Value; // snap to 100%
                        }, DispatcherPriority.Background);
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
                                    System.Globalization.NumberStyles.Any,
                                    System.Globalization.CultureInfo.InvariantCulture,
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

                // stderr log
                p.ErrorDataReceived += (_, e) =>
                {
                    if (e.Data != null) onLogLine?.Invoke(e.Data);
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

            return new ProcResult(p.ExitCode);
        }
        catch (OperationCanceledException)
        {
            return new ProcResult(-1);
        }
        finally
        {
            lock (_procLock)
            {
                _runningProcs.Remove(p);
                _procToJob.Remove(p);
                _procOrder.Remove(p);
                _throttledProcs.Remove(p);
            }

            RequestEnforceDesiredConcurrency("proc exit");

            try { p.Dispose(); } catch { }
        }
    }





    private record ProcResult(int ExitCode);

    public class Preset
    {
        public string Name { get; set; } = "";
        public string ArgsTemplate { get; set; } = "";
    }

    // ---------------- Tiny WPF prompt (replaces Microsoft.VisualBasic InputBox) ----------------
    private static string? PromptText(Window owner, string label, string title, string defaultValue)
    {
        var win = new Window
        {
            Title = title,
            Owner = owner,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false
        };

        var tb = new TextBox { MinWidth = 360, Text = defaultValue, Margin = new Thickness(0, 6, 0, 10) };

        var ok = new Button { Content = "OK", Width = 90, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "Cancel", Width = 90, IsCancel = true };

        string? result = null;

        ok.Click += (_, _) =>
        {
            result = tb.Text;
            win.DialogResult = true;
            win.Close();
        };

        var panel = new StackPanel { Margin = new Thickness(14) };
        panel.Children.Add(new TextBlock { Text = label });
        panel.Children.Add(tb);

        var btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        btns.Children.Add(ok);
        btns.Children.Add(cancel);
        panel.Children.Add(btns);

        win.Content = panel;

        tb.SelectAll();
        tb.Focus();

        return win.ShowDialog() == true ? result : null;
    }
    private async Task<MediaProbeInfo?> GetMediaInfoAsync(string path)
    {
        string ffprobe = Path.Combine(_appDir, "ffprobe.exe");
        if (!File.Exists(ffprobe)) return null;

        var psi = new ProcessStartInfo(ffprobe)
        {
            Arguments = $"-v quiet -print_format json -show_format -show_streams -select_streams v:0 \"{path}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true, // We redirect this...
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8
        };

        try
        {
            using var p = Process.Start(psi);
            if (p == null) return null;

            // ...So we MUST read it to prevent deadlocks!
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
                    double.TryParse(durProp.GetString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double d))
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

            if (root.TryGetProperty("streams", out var streams) && streams.GetArrayLength() > 0)
            {
                var vid = streams[0];
                if (vid.TryGetProperty("codec_name", out var c)) codec = c.GetString() ?? "";

                int w = 0, h = 0;
                if (vid.TryGetProperty("width", out var wProp)) w = wProp.GetInt32();
                if (vid.TryGetProperty("height", out var hProp)) h = hProp.GetInt32();
                if (w > 0 && h > 0) res = $"{w}x{h}";

                if (vid.TryGetProperty("avg_frame_rate", out var fpsProp))
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

            return new MediaProbeInfo(duration, codec, res, fps, bitrate, size);
        }
        catch
        {
            return null;
        }
    }
    private void StartDurationProbe(FileJob job)
    {
        // If ffprobe missing, just return
        if (!File.Exists(Path.Combine(_appDir, "ffprobe.exe")))
            return;

        _ = Task.Run(async () =>
        {
            await _probeSem.WaitAsync().ConfigureAwait(false);
            try
            {
                // We removed the simple _durationCache because we now need full metadata.
                // PROBE INPUT
                var info = await GetMediaInfoAsync(job.Path);
                if (info != null)
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        // Job might have been removed while probing
                        if (!_queueSet.Contains(job.Path)) return;

                        // Set the Duration for the progress bar
                        job.TotalSeconds = info.Duration;

                        // Set the Metadata for the UI text
                        job.SetInputInfo(info);
                    }, DispatcherPriority.Background);
                }
            }
            finally
            {
                _probeSem.Release();
            }
        });
    }
    private void LogExpander_Expanded(object sender, RoutedEventArgs e)
    {

        // Only pick a default the first time / when collapsed-auto
        if (LogRowDef.Height.IsAuto || LogRowDef.Height.Value < 50)
            LogRowDef.Height = new GridLength(200);
    }


    private void LogExpander_Collapsed(object sender, RoutedEventArgs e)
    {

        // Switch back to Auto so it shrinks to exactly the size of the Header
        LogRowDef.Height = GridLength.Auto;
    }

    // ---------------- PAUSE / RESUME MAGIC ----------------
    private volatile bool _isPaused = false;

    private void Pause_Click(object sender, RoutedEventArgs e)
    {
        if (!_isRunning) return;

        _isPaused = !_isPaused;

        // 1. Snapshot processes AND their associated Jobs safely
        List<(Process Proc, FileJob Job)> targets;
        HashSet<Process> throttledSnapshot;

        lock (_procLock)
        {
            targets = _runningProcs
                .Select(p =>
                {
                    _procToJob.TryGetValue(p, out var j);
                    return (Proc: p, Job: j);
                })
                .Where(x => x.Job != null) // Filter out any zombies
                .Select(x => (x.Proc, x.Job!))
                .ToList();

            throttledSnapshot = new HashSet<Process>(_throttledProcs);
        }

        if (_isPaused)
        {
            PauseBtn.Content = "Resume";
            Status("Pausing...");

            foreach (var (p, job) in targets)
            {
                try
                {
                    SuspendProcess(p);
                    // ✅ LOG TO THE SPECIFIC JOB
                    Log("[PAUSE] User suspended process.", job);
                }
                catch { }
            }

            Volatile.Write(ref _activeRunning, 0);
            _ = Dispatcher.BeginInvoke(UpdateJobsLiveHint, DispatcherPriority.Background);

            Status("Paused. Click Resume to continue.");
            Log("[SYSTEM] Batch paused by user."); // Optional global marker
        }
        else
        {
            PauseBtn.Content = "Pause";
            Status("Resuming...");

            foreach (var (p, job) in targets)
            {
                // If it was throttled by the auto-limiter, don't resume it yet.
                // The EnforceDesiredConcurrency call below will handle it.
                if (throttledSnapshot.Contains(p))
                {
                    // Optional: You could log that it's waiting
                    // Log("[RESUME] Waiting for slot (throttled)...", job);
                    continue;
                }

                try
                {
                    ResumeProcess(p);
                    // ✅ LOG TO THE SPECIFIC JOB
                    Log("[RESUME] User resumed process.", job);
                }
                catch { }
            }

            // Now apply the Jobs governor (it may resume some throttled ones too)
            EnforceDesiredConcurrency("user resume");

            Status("Running…");
            Log("[SYSTEM] Batch resumed by user.");
        }
        UpdateTaskbarProgress();
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern IntPtr OpenThread(int dwDesiredAccess, bool bInheritHandle, int dwThreadId);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern int SuspendThread(IntPtr hThread);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern int ResumeThread(IntPtr hThread);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern int CloseHandle(IntPtr hObject);
    private async Task WaitUntilAllowedToStartNewJobAsync(CancellationToken token)
    {
        WaitHandle[] handles = { _throttleChanged, token.WaitHandle };

        while (true)
        {
            token.ThrowIfCancellationRequested();

            while (_isPaused)
                await Task.Delay(200, token).ConfigureAwait(false);

            int throttledCount;
            lock (_procLock) throttledCount = _throttledProcs.Count;

            if (throttledCount == 0)
                return;

            int signaled = WaitHandle.WaitAny(handles, 150);

            if (signaled == 1)
                token.ThrowIfCancellationRequested();
        }
    }

    private static void SuspendProcess(Process process)
    {
        if (process == null) return;
        try { if (process.HasExited) return; } catch { return; }

        try
        {
            foreach (ProcessThread pT in process.Threads)
            {
                IntPtr hThread = OpenThread(0x0002, false, pT.Id);
                if (hThread == IntPtr.Zero) continue;
                try { SuspendThread(hThread); }
                finally { CloseHandle(hThread); }
            }
        }
        catch { /* ignore */ }
    }

    private static void ResumeProcess(Process process)
    {
        if (process == null) return;
        try { if (process.HasExited) return; } catch { return; }

        try
        {
            foreach (ProcessThread pT in process.Threads)
            {
                IntPtr hThread = OpenThread(0x0002, false, pT.Id);
                if (hThread == IntPtr.Zero) continue;

                try
                {
                    while (ResumeThread(hThread) > 1) { }
                }
                finally { CloseHandle(hThread); }
            }
        }
        catch { /* ignore */ }
    }
    private void About_Click(object sender, RoutedEventArgs e)
    {
        var win = new AboutWindow
        {
            Owner = this
        };
        win.ShowDialog();
    }
}
public record MediaProbeInfo(double Duration, string Codec, string Resolution, double Fps, long Bitrate, long SizeBytes);

public class JobStatsFormatter : DependencyObject
{
    // Store handlers per TextBlock so we can remove them later
    private static readonly ConditionalWeakTable<TextBlock, EventHandler<PropertyChangedEventArgs>> _handlers =
        new();

    public static readonly DependencyProperty FileJobProperty =
        DependencyProperty.RegisterAttached("FileJob", typeof(object), typeof(JobStatsFormatter),
            new PropertyMetadata(null, OnFileJobChanged));

    public static object GetFileJob(DependencyObject obj) => obj.GetValue(FileJobProperty);
    public static void SetFileJob(DependencyObject obj, object value) => obj.SetValue(FileJobProperty, value);

    private static void OnFileJobChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock tb) return;

        var oldJob = e.OldValue as INotifyPropertyChanged;
        var newJob = e.NewValue as INotifyPropertyChanged;

        // 1. PROPERLY REMOVE the old handler (using the stored reference)
        if (oldJob != null && _handlers.TryGetValue(tb, out var oldHandler))
        {
            PropertyChangedEventManager.RemoveHandler(oldJob, oldHandler, "");
            _handlers.Remove(tb);
        }

        // 2. Create and store NEW handler
        if (newJob != null)
        {
            // ✅ Create the handler ONCE and store it
            EventHandler<PropertyChangedEventArgs> handler = (s, a) => OnJobPropertyChanged(s, a, tb);
            _handlers.Add(tb, handler);

            // Subscribe using the SAME handler reference
            PropertyChangedEventManager.AddHandler(newJob, handler, "");

            // Initial draw
            UpdateText(tb, newJob);
        }
        else
        {
            tb.Inlines.Clear();
        }
    }

    // Event Handler:  Runs whenever a property inside FileJob changes
    private static void OnJobPropertyChanged(object? sender, PropertyChangedEventArgs e, TextBlock tb)
    {
        // ✅ UPDATED: Listen to more properties that affect display
        if (e.PropertyName == "Status" ||
            e.PropertyName == "InputInfo" ||
            e.PropertyName == "OutputInfo" ||
            e.PropertyName == "TotalSeconds" ||      // Duration probe
            e.PropertyName == "ProcessedSeconds" ||  // Progress
            e.PropertyName == "LastMessage")         // Any status change
        {
            // Must run on UI thread
            if (tb.Dispatcher.CheckAccess())
                UpdateText(tb, sender);
            else
                tb.Dispatcher.BeginInvoke(() => UpdateText(tb, sender), DispatcherPriority.Background);
        }
    }

    private static void UpdateText(TextBlock tb, object? jobObj)
    {
        tb.Inlines.Clear();
        if (jobObj is not FfmpegDrop.MainWindow.FileJob fileJob) return;

        if (fileJob.InputInfo == null) return;

        // Only show "Diff View" if Status is OK **AND** we actually have Output Data
        bool showDiff = (fileJob.Status == FfmpegDrop.MainWindow.JobStatus.Ok && fileJob.OutputInfo != null);

        if (!showDiff)
        {
            // --- PENDING / RUNNING / FALLBACK VIEW ---
            AddRun(tb, fileJob.InputInfo.Codec);
            AddSeparator(tb);
            AddRun(tb, fileJob.InputInfo.Resolution);
            AddSeparator(tb);
            if (fileJob.InputInfo.Fps > 0) AddRun(tb, $"{fileJob.InputInfo.Fps: 0.##}fps");
            AddSeparator(tb);
            // Show Input Bitrate here
            if (fileJob.InputInfo.Bitrate > 0) AddRun(tb, FormatBitrate(fileJob.InputInfo.Bitrate));
            AddSeparator(tb);
            AddRun(tb, $"{fileJob.InputInfo.SizeBytes / 1024.0 / 1024.0:0.0} MB");
        }
        else
        {
            // --- DONE VIEW (Comparison) ---
            var input = fileJob.InputInfo!;
            var output = fileJob.OutputInfo!;

            // Codec
            AddDiff(tb, input.Codec, output.Codec);
            AddSeparator(tb);

            // Resolution
            AddDiff(tb, input.Resolution, output.Resolution);
            AddSeparator(tb);

            // FPS
            if (Math.Abs(input.Fps - output.Fps) > 0.1)
                AddDiff(tb, $"{input.Fps:0.##}fps", $"{output.Fps:0.##}fps");
            else if (output.Fps > 0)
                AddRun(tb, $"{output.Fps:0.##}fps", isBold: false);

            AddSeparator(tb);

            // Bitrate (Show arrow if changed by > 5kbps)
            long inBr = input.Bitrate;
            long outBr = output.Bitrate;
            if (inBr > 0 && outBr > 0 && Math.Abs(inBr - outBr) > 5000)
                AddDiff(tb, FormatBitrate(inBr), FormatBitrate(outBr));
            else if (outBr > 0)
                AddRun(tb, FormatBitrate(outBr), isBold: false);

            AddSeparator(tb);

            // Size
            double inMb = input.SizeBytes / 1024.0 / 1024.0;
            double outMb = output.SizeBytes / 1024.0 / 1024.0;

            if (inMb > 0)
            {
                double pct = ((outMb - inMb) / inMb) * 100.0;
                string oldTxt = $"{inMb:0.0} MB";

                string pctText = pct > 0
                    ? $"+{pct:0.0}%"
                    : $"{pct:0.0}%";

                string newTxt = $"{outMb:0.0} MB ({pctText})";

                AddRun(tb, oldTxt, isBold: false, isDim: true);
                AddArrow(tb);
                AddRun(tb, newTxt, isBold: true, isDim: false);
            }
            else
            {
                AddRun(tb, $"{outMb:0.0} MB", isBold: true);
            }
        }
    }

    // --- Helpers (Same as before) ---

    private static void AddDiff(TextBlock tb, string oldVal, string newVal)
    {
        if (string.IsNullOrEmpty(newVal)) return;

        if (!string.Equals(oldVal, newVal, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(oldVal))
        {
            AddRun(tb, oldVal, isBold: false, isDim: true);
            AddArrow(tb);
            AddRun(tb, newVal, isBold: true, isDim: false);
        }
        else
        {
            AddRun(tb, newVal, isBold: false, isDim: true);
        }
    }

    private static void AddRun(TextBlock tb, string text, bool isBold = false, bool isDim = true)
    {
        if (string.IsNullOrEmpty(text)) return;
        var run = new System.Windows.Documents.Run(text);

        if (isBold) run.FontWeight = FontWeights.Bold;
        if (isDim) run.Foreground = System.Windows.Media.Brushes.Gray;
        // Note: Non-dim runs inherit the TextBlock color (White/Black)

        tb.Inlines.Add(run);
    }

    private static void AddArrow(TextBlock tb)
    {
        tb.Inlines.Add(new System.Windows.Documents.Run(" → ") { Foreground = System.Windows.Media.Brushes.Gray });
    }

    private static void AddSeparator(TextBlock tb)
    {
        if (tb.Inlines.Count > 0)
            tb.Inlines.Add(new System.Windows.Documents.Run(" • ") { Foreground = System.Windows.Media.Brushes.DarkGray });
    }

    private static string FormatBitrate(long bps)
    {
        if (bps >= 1_000_000) return $"{bps / 1_000_000.0:0.0} Mb/s";
        return $"{bps / 1000.0:0} kb/s";
    }
}
