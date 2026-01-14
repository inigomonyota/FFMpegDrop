using Microsoft.Win32;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
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
using FfmpegDrop.Models;
using FfmpegDrop.Services;
using FfmpegDrop.Helpers;

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

    private readonly string _appDir = AppContext.BaseDirectory;

    private string PresetsPath => Path.Combine(_appDir, "presets.json");
    private string SettingsPath => Path.Combine(_appDir, "settings.json");

    private readonly List<Preset> _presets = new();

    private bool _normalizeEnabled;
    private double _normalizeTargetLufs;    // e.g. -16, -18, etc.
    private bool _analysisValid;            // true when per-file gains are current
    private bool IsNormalizationEnabled() => _normalizeEnabled;

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

    private bool _cliErrorPopupShown = false;

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

        UpdateRunButtonContent();

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


    private void NormalizeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;

        if (NormalizeCombo.SelectedItem is ComboBoxItem item &&
            double.TryParse(item.Tag as string, NumberStyles.Float,
                            CultureInfo.InvariantCulture, out double target))
        {
            _normalizeEnabled = true;
            _normalizeTargetLufs = target;
        }
        else
        {
            _normalizeEnabled = false;
        }

        InvalidateAnalysis();
        UpdateRunButtonContent();

        // ✅ ADD THIS LINE:
        if (!_initializing) SaveSettings();
    }

    private async Task RunBatchAnalysisAsync(string ffmpegExe, List<FileJob> jobsList)
    {
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _isRunning = true;

        // ✅ Get Jobs setting
        int jobs = (JobsCombo.SelectedItem is int j) ? j : 2;
        jobs = Math.Clamp(jobs, 1, MaxConcurrentJobs);

        await Dispatcher.InvokeAsync(() =>
        {
            RunBtn.IsEnabled = true;
            RunBtn.Content = "Cancel";
            PauseBtn.IsEnabled = false;

            Progress.Minimum = 0;
            Progress.Maximum = jobsList.Count;
            Progress.Value = 0;

            SetOverallProgressCompleted(false);
            Status($"Analyzing audio loudness (Jobs={jobs})…");

            if (TaskbarInfo != null)
            {
                TaskbarInfo.ProgressState = TaskbarItemProgressState.Normal;
                TaskbarInfo.ProgressValue = 0;
            }
        });

        StartFollowingActiveJobs();

        int done = 0;
        int measured = 0;

        // ✅ Create channel for FIFO order
        var ch = Channel.CreateUnbounded<(FileJob Job, int Index)>(new UnboundedChannelOptions
        {
            SingleWriter = true,
            SingleReader = false,
            AllowSynchronousContinuations = false
        });

        for (int i = 0; i < jobsList.Count; i++)
            ch.Writer.TryWrite((jobsList[i], i));

        ch.Writer.Complete();

        // ✅ FIFO start order (like encoding)
        var startTurn = new TaskCompletionSource<bool>[jobsList.Count + 1];
        for (int i = 0; i < startTurn.Length; i++)
            startTurn[i] = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        startTurn[0].TrySetResult(true); // first job may start

        // ✅ Semaphore to limit concurrent analysis
        using var analysisSem = new SemaphoreSlim(jobs, jobs);

        async Task AnalyzeOneAsync((FileJob Job, int Index) item)
        {
            var job = item.Job;
            int index = item.Index;
            bool releasedNext = false;

            void ReleaseNextStart()
            {
                if (releasedNext) return;
                releasedNext = true;
                startTurn[index + 1].TrySetResult(true);
            }

            try
            {
                if (token.IsCancellationRequested)
                {
                    ReleaseNextStart();
                    return;
                }

                string input = job.Path;
                string label = System.IO.Path.GetFileName(input);

                if (!File.Exists(input))
                {
                    Log($"[ANALYSIS][SKIP] Missing:  {input}", job);
                    ReleaseNextStart();
                    return;
                }

                // ✅ Wait for our turn (FIFO)
                await startTurn[index].Task.ConfigureAwait(false);

                // ✅ Acquire slot
                await analysisSem.WaitAsync(token).ConfigureAwait(false);

                try
                {
                    // Clear loudness data
                    job.LoudnessData = null;
                    job.TargetLufs = null;

                    // Set status to Running
                    await Dispatcher.InvokeAsync(() =>
                    {
                        job.Status = JobStatus.Running;
                        job.LastMessage = "Analyzing loudness…";
                    });

                    // ✅ Release FIFO baton before starting FFmpeg
                    ReleaseNextStart();

                    var stats = await AnalyzeFileLufsAsync(ffmpegExe, input, job, token);

                    if (stats != null)
                    {
                        Interlocked.Increment(ref measured);
                        job.LoudnessData = stats;
                        job.TargetLufs = _normalizeTargetLufs;

                        Log($"[ANALYSIS] {label}:  I={stats.InputI:0.0} LUFS, TP={stats.InputTP:0.0} dB, LRA={stats.InputLRA:0.0} LU", job);

                        await Dispatcher.InvokeAsync(() =>
                        {
                            job.Status = JobStatus.Analyzed;  // ✅ Changed from Pending
                            job.LastMessage = $"Analyzed: {stats.InputI:0.0} LUFS";
                        });
                    }
                    else
                    {
                        Log($"[ANALYSIS][WARN] Could not measure loudness for {label}", job);

                        await Dispatcher.InvokeAsync(() =>
                        {
                            job.Status = JobStatus.Pending;  // ✅ Keep as Pending if failed
                            job.LastMessage = "Analysis failed";
                        });
                    }
                }
                finally
                {
                    analysisSem.Release();
                }

                int d = Interlocked.Increment(ref done);
                await Dispatcher.InvokeAsync(() =>
                {
                    Progress.Value = d;
                    Status($"Analyzing audio loudness… {d}/{jobsList.Count}");

                    if (TaskbarInfo != null && Progress.Maximum > 0)
                        TaskbarInfo.ProgressValue = Progress.Value / Progress.Maximum;
                });
            }
            catch (OperationCanceledException)
            {
                ReleaseNextStart();
            }
            catch (Exception ex)
            {
                Log($"[ANALYSIS][EXCEPTION] {ex.Message}", job);
                ReleaseNextStart();
            }
        }

        async Task WorkerAsync()
        {
            while (await ch.Reader.WaitToReadAsync(token).ConfigureAwait(false))
            {
                while (ch.Reader.TryRead(out var item))
                {
                    await AnalyzeOneAsync(item).ConfigureAwait(false);
                }
            }
        }

        try
        {
            // ✅ Create workers (up to Jobs count)
            int workerCount = Math.Min(jobs, jobsList.Count);
            var workers = Enumerable.Range(0, workerCount).Select(_ => WorkerAsync()).ToArray();

            await Task.WhenAll(workers).ConfigureAwait(false);

            if (_wasCancelled || token.IsCancellationRequested)
            {
                Status("Analysis cancelled.");
                SetOverallProgressCancelled();
                return;
            }

            int finalMeasured = Volatile.Read(ref measured);
            if (finalMeasured == 0)
            {
                Status("Analysis failed – no loudness data.");
                SetOverallProgressCancelled();
                return;
            }

            _analysisValid = true;
            Status($"Analysis complete.  Target {_normalizeTargetLufs:0.0} LUFS (two-pass loudnorm ready).");
            SetOverallProgressCompleted(true);
        }
        catch (OperationCanceledException)
        {
            Status("Analysis cancelled.");
            SetOverallProgressCancelled();
        }
        finally
        {
            _isRunning = false;
            _cts?.Dispose();
            _cts = null;

            await Dispatcher.InvokeAsync(() =>
            {
                RunBtn.IsEnabled = true;
                PauseBtn.IsEnabled = false;
                PauseBtn.Content = "Pause";

                if (TaskbarInfo != null)
                {
                    TaskbarInfo.ProgressState = TaskbarItemProgressState.None;
                    TaskbarInfo.ProgressValue = 0;
                }

                UpdateRunButtonContent();

                // Scroll back to top when done
                if (_queue.Count > 0)
                {
                    FilesList.UpdateLayout();
                    FilesList.ScrollIntoView(_queue[0]);
                }

                // ✅ MOVE THIS INSIDE THE DISPATCHER BLOCK
                StopFollowingActiveJobs();
            });
        }
    }
    private void InvalidateAnalysis()
    {
        _analysisValid = false;

        foreach (var job in _queue)
        {
            job.LoudnessData = null;
            job.TargetLufs = null;
        }
    }

    private void UpdateRunButtonContent()
    {
        if (_initializing) return;

        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(UpdateRunButtonContent);
            return;
        }

        // ✅ STEP 1: Handle running state (button becomes "Cancel")
        if (_isRunning)
        {
            RunBtn.Content = "Cancel";
            RunBtn.IsEnabled = true;
            return;
        }

        // ✅ STEP 2: Check if FFmpeg is available
        string? ffmpegExe = ResolveFfmpegExeOrNull();
        if (ffmpegExe == null)
        {
            RunBtn.IsEnabled = false;
            RunBtn.Content = "Run";
            return;
        }

        // ✅ STEP 3: Determine button TEXT based on normalization state
        //    (Shows user what will happen when they add files)
        if (IsNormalizationEnabled() && !_analysisValid)
        {
            RunBtn.Content = "Analyze Audio";
        }
        else
        {
            RunBtn.Content = "Run";
        }

        // ✅ STEP 4: Button is only ENABLED if there are files
        RunBtn.IsEnabled = (_queue.Count > 0);
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
            if (NativeMethods.DwmSetWindowAttribute(handle, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref useImmersiveDarkMode, sizeof(int)) == 0)
                return true;

            // Fallback for older Windows 10 versions
            if (NativeMethods.DwmSetWindowAttribute(handle, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1, ref useImmersiveDarkMode, sizeof(int)) == 0)
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


    private static bool IsAllowedFile(string path) => FileUtils.IsAllowedFile(path);

    // ---------------- Portable settings ----------------

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
                    // Restore Normalization setting
                    if (!string.IsNullOrWhiteSpace(s.NormalizeTarget) &&
                        double.TryParse(s.NormalizeTarget, NumberStyles.Float, CultureInfo.InvariantCulture, out double target))
                    {
                        _normalizeEnabled = true;
                        _normalizeTargetLufs = target;

                        // Select the matching ComboBox item
                        foreach (ComboBoxItem item in NormalizeCombo.Items)
                        {
                            if (item.Tag is string tag &&
                                double.TryParse(tag, NumberStyles.Float, CultureInfo.InvariantCulture, out double itemValue) &&
                                Math.Abs(itemValue - target) < 0.1)
                            {
                                NormalizeCombo.SelectedItem = item;
                                break;
                            }
                        }
                    }
                    else
                    {
                        _normalizeEnabled = false;
                        // "Disabled" is already selected by IsSelected="True" in XAML
                    }
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
            UpdateRunButtonContent();
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
                DarkMode = _isDarkTheme,
                NormalizeTarget = _normalizeEnabled ? _normalizeTargetLufs.ToString(CultureInfo.InvariantCulture) : null  // ✅ ADD THIS
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
    static private string? ResolveFfmpegExeOrNull()
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

    static private string? GetFfmpegVersionString(string exePath)
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
    private void Status(string msg)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => Status(msg));
            return;
        }
        StatusText.Text = msg;
    }

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
        InvalidateAnalysis();
        UpdateRunButtonContent();
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
        UpdateRunButtonContent();
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (_isRunning) return;

        _queue.Clear();
        _queueSet.Clear();

        // Reset main progress bar
        Progress.Minimum = 0;
        Progress.Maximum = 1;
        Progress.Value = 0;

        // Reset overall progress style (remove green/red state)
        SetOverallProgressCompleted(false);

        // Reset taskbar progress
        if (TaskbarInfo != null)
        {
            TaskbarInfo.ProgressState = TaskbarItemProgressState.None;
            TaskbarInfo.ProgressValue = 0;
        }
        InvalidateAnalysis();
        UpdateRunButtonContent();

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
        InvalidateAnalysis();
        UpdateRunButtonContent();

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

    /// <summary>
    /// Analyzes a file using loudnorm filter (pass 1 of two-pass).
    /// Returns LoudnessStats or null if analysis fails.
    /// </summary>
    private async Task<LoudnessStats?> AnalyzeFileLufsAsync(
        string ffmpegExe,
        string inputPath,
        FileJob job,
        CancellationToken token)
    {
        // Use loudnorm with print_format=json for analysis
        string args = $"-hide_banner -nostdin -i \"{inputPath}\" " +
                      $"-af loudnorm=I={_normalizeTargetLufs.ToString(CultureInfo.InvariantCulture)}:" +
                      $"TP=-2.0:LRA=7.0:print_format=json " +
                      $"-f null -";

        var stderrBuilder = new StringBuilder();

        var result = await RunFfmpegAsync(
            ffmpegExe,
            args,
            showWindow: false,
            token: token,
            job: job,
            onStarted: null,
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

    /// <summary>
    /// Parses loudnorm JSON output from FFmpeg stderr.
    /// </summary>
    private LoudnessStats? ParseLoudnormStats(string stderr, FileJob job)
    {
        try
        {
            // Find the JSON block (starts with "{" and ends with "}")
            int jsonStart = stderr.LastIndexOf('{');
            int jsonEnd = stderr.LastIndexOf('}');

            if (jsonStart == -1 || jsonEnd == -1 || jsonEnd <= jsonStart)
            {
                Log("[NORMALIZE] Could not find JSON stats in FFmpeg output", job);
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

            Log($"[NORMALIZE] Measured:  I={inputI:F2} LUFS, TP={inputTP:F2} dB, LRA={inputLRA:F2} LU", job);

            return new LoudnessStats(inputI, inputTP, inputLRA, inputThresh, targetOffset);
        }
        catch (Exception ex)
        {
            Log($"[NORMALIZE] Failed to parse loudnorm JSON: {ex.Message}", job);
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


    // ---------------- Run (parallel jobs) ----------------
    private async void Run_Click(object sender, RoutedEventArgs e)
    {
        if (_isRunning)
        {
            _wasCancelled = true;
            _cts?.Cancel();
            return;
        }
        _wasCancelled = false;
        _cliErrorPopupShown = false;
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

        bool normalizationEnabled = IsNormalizationEnabled();
        bool needAnalysis = normalizationEnabled && !_analysisValid;

        if (needAnalysis)
        {
            await RunBatchAnalysisAsync(ffmpegExe, jobsList);
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

        // ---- UI: entering run ----
        _isRunning = true;
        RunBtn.Content = "Cancel";
        RunBtn.IsEnabled = true;
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
        static async Task TryDeleteFileAsync(string path) => await FileUtils.TryDeleteFileAsync(path);

        static string NormalizePath(string p) => FileUtils.NormalizePath(p);

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

                    // Start with the template for this job
                    string templateForJob = argsTemplate;

                    // 🔊 Batch normalization: per-file volume injection, only when safe
                    // 🔊 TWO-PASS LOUDNORM: Apply measured stats from batch analysis
                    if (IsNormalizationEnabled() && _analysisValid && job.HasNormalization)
                    {
                        var stats = job.LoudnessData!;
                        double target = job.TargetLufs!.Value;

                        // Build the loudnorm filter with measured values (pass 2)
                        string normFilter =
                            $"loudnorm=I={target.ToString(CultureInfo.InvariantCulture)}:" +
                            $"TP=-2.0:" +
                            $"LRA=7.0:" +
                            $"measured_I={stats.InputI.ToString(CultureInfo.InvariantCulture)}:" +
                            $"measured_TP={stats.InputTP.ToString(CultureInfo.InvariantCulture)}:" +
                            $"measured_LRA={stats.InputLRA.ToString(CultureInfo.InvariantCulture)}:" +
                            $"measured_thresh={stats.InputThresh.ToString(CultureInfo.InvariantCulture)}:" +
                            $"offset={stats.TargetOffset.ToString(CultureInfo.InvariantCulture)}:" +
                            $"linear=true";

                        // Check if user template already has audio filters
                        string lower = templateForJob.ToLowerInvariant();

                        bool hasCopyAudio =
                            lower.Contains("-c:a copy") ||
                            lower.Contains("-acodec copy");

                        bool hasAudioFilters =
                            Regex.IsMatch(lower, @"(^|\s)-af(\s|$)") ||
                            Regex.IsMatch(lower, @"(^|\s)-filter:a(\s|$)") ||
                            Regex.IsMatch(lower, @"(^|\s)-filter_complex(\s|$)");

                        if (hasAudioFilters)
                        {
                            Log("[NORMALIZE] Skipped (custom audio filters present in template)", job);
                        }
                        else if (hasCopyAudio)
                        {
                            // ✅ SMART REPLACEMENT: User wants normalization, so replace -c:a copy with re-encoding

                            // Determine audio encoder from input codec
                            string audioEncoder = "aac";  // Safe default
                            string audioBitrateArg = "-b:a 192k";  // Safe default

                            if (job.InputInfo != null && !string.IsNullOrEmpty(job.InputInfo.AudioCodec))
                            {
                                string inCodec = job.InputInfo.AudioCodec.ToLowerInvariant();

                                // Map common codecs
                                audioEncoder = inCodec switch
                                {
                                    "aac" => "aac",
                                    "mp3" => "libmp3lame",
                                    "opus" => "libopus",
                                    "vorbis" => "libvorbis",
                                    "ac3" => "ac3",
                                    "eac3" => "eac3",
                                    "flac" => "flac",
                                    "alac" => "alac",
                                    "pcm_s16le" => "pcm_s16le",
                                    _ => "aac"  // Default fallback
                                };

                                // Use input bitrate if available
                                if (job.InputInfo.AudioBitrate > 0)
                                {
                                    long kbps = job.InputInfo.AudioBitrate / 1000;
                                    audioBitrateArg = $"-b:a {kbps}k";
                                }
                            }

                            // Replace -c:a copy with smart re-encoding
                            templateForJob = Regex.Replace(
                                templateForJob,
                                @"-c:a\s+copy",   // ✅ Removed the space after the colon
                                $"-c:a {audioEncoder} {audioBitrateArg}",
                                RegexOptions.IgnoreCase);

                            templateForJob = Regex.Replace(
                                templateForJob,
                                @"-acodec\s+copy",
                                $"-acodec {audioEncoder} {audioBitrateArg}",
                                RegexOptions.IgnoreCase);

                            Log($"[NORMALIZE] Replaced '-c:a copy' with '-c:a {audioEncoder} {audioBitrateArg}' for normalization", job);

                            // Now inject the loudnorm filter
                            const StringComparison cmp = StringComparison.OrdinalIgnoreCase;

                            if (templateForJob.IndexOf("\"{out}\"", cmp) >= 0)
                            {
                                templateForJob = templateForJob.Replace(
                                    "\"{out}\"",
                                    $"-af {normFilter} \"{{out}}\"",
                                    cmp);
                            }
                            else
                            {
                                templateForJob = templateForJob.Replace(
                                    "{out}",
                                    $"-af {normFilter} {{out}}",
                                    cmp);
                            }

                            Log($"[NORMALIZE] Applying two-pass loudnorm to {fileLabel}", job);
                        }
                        else
                        {
                            // No conflicts, inject the loudnorm filter normally
                            const StringComparison cmp = StringComparison.OrdinalIgnoreCase;

                            if (templateForJob.IndexOf("\"{out}\"", cmp) >= 0)
                            {
                                templateForJob = templateForJob.Replace(
                                    "\"{out}\"",
                                    $"-af {normFilter} \"{{out}}\"",
                                    cmp);
                            }
                            else
                            {
                                templateForJob = templateForJob.Replace(
                                    "{out}",
                                    $"-af {normFilter} {{out}}",
                                    cmp);
                            }

                            Log($"[NORMALIZE] Applying two-pass loudnorm to {fileLabel}", job);
                        }
                    }

                    // Now do the usual token replacement
                    string userArgs = templateForJob
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

                                    // ✅ Updated with audio parameters (empty/zero for fallback)
                                    outInfo = new MediaProbeInfo(d, "", "", f, 0, fi.Length, "", 0);
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
                        if (result.CliError && !token.IsCancellationRequested)
                        {
                            // 🔴 Command line / preset is broken – abort whole batch
                            Log($"[{fileLabel}] [CLI ERROR] ExitCode={result.ExitCode}", job);
                            _failedFiles.Enqueue($"[CLI] {input} :: ExitCode={result.ExitCode}");
                            Interlocked.Increment(ref fail);

                            await TryDeleteFileAsync(tempOut).ConfigureAwait(false);
                            SetJob(job, JobStatus.Failed, "Command line error (batch aborted)");

                            // Cancel all remaining jobs in this run
                            try { _cts?.Cancel(); } catch { }

                            if (!_cliErrorPopupShown)
                            {
                                _cliErrorPopupShown = true;
                                _ = Dispatcher.BeginInvoke(new Action(() =>
                                {
                                    MessageBox.Show(
                                        this,
                                        "FFmpeg reported a command line error while processing this preset.\n\n" +
                                        "The batch has been aborted to avoid failing every file.\n\n" +
                                        "Check the log window for the exact FFmpeg error message and fix the preset.",
                                        "FFmpeg Command Line Error",
                                        MessageBoxButton.OK,
                                        MessageBoxImage.Error);
                                }));
                            }

                            return;
                        }

                        // Normal per-file failure (corrupt input, decode error, etc.)
                        Log($"[{fileLabel}] [FAIL] ExitCode={result.ExitCode}", job);
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
                PauseBtn.IsEnabled = false;
                PauseBtn.Content = "Pause";
                _isPaused = false;

                // Back to idle "Run" state
                RunBtn.IsEnabled = true;
                RunBtn.Content = "Run";
                UpdateFfmpegStatus();

                if (_wasCancelled)
                {
                    Status("Cancelled.");
                    SetOverallProgressCancelled();
                }
                else
                {
                    Status($"Done. OK={okNow}, FAIL={failNow}");
                    SetOverallProgressCompleted(true);

                    try
                    {
                        SystemSounds.Asterisk.Play();
                    }
                    catch { }
                }

                if (TaskbarInfo != null)
                {
                    TaskbarInfo.ProgressState = TaskbarItemProgressState.None;
                    TaskbarInfo.ProgressValue = 0;
                }

                UpdateJobsLiveHint();
            }, DispatcherPriority.Background);


            StopFollowingActiveJobs();

            Log($"=== Done. OK={okNow}, FAIL={failNow} ===");
        }
    }

    private static bool ContainsToken(string s, string token)
        => s.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0;

    static private string EnhanceArgs(string originalArgs, bool showWindow)
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

    private static bool LooksLikeCliError(string line)
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





    double InputTP,
    double InputLRA,
    double InputThresh,
    double TargetOffset
);


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
                        // ✅ Audio stream
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

    private static void SuspendProcess(Process process) =>
        ProcessThrottler.SuspendProcess(process);

    private static void ResumeProcess(Process process) =>
        ProcessThrottler.ResumeProcess(process);

    private void About_Click(object sender, RoutedEventArgs e)
    {
        var win = new AboutWindow
        {
            Owner = this
        };
        win.ShowDialog();
    }
}
    string Codec,           // Video codec
    string Resolution,
    double Fps,
    long Bitrate,          // Overall bitrate
    long SizeBytes,
    string AudioCodec,     // ✅ Audio codec name
    long AudioBitrate      // ✅ Audio bitrate
);

