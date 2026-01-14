using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using FfmpegDrop.Models;

namespace FfmpegDrop.Converters;

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
        if (jobObj is not FileJob fileJob) return;

        if (fileJob.InputInfo == null) return;

        // Only show "Diff View" if Status is OK **AND** we actually have Output Data
        bool showDiff = (fileJob.Status == JobStatus.Ok && fileJob.OutputInfo != null);


        if (!showDiff)
        {
            // --- PENDING / RUNNING / FALLBACK VIEW ---
            AddRun(tb, fileJob.InputInfo.Codec);
            AddSeparator(tb);
            AddRun(tb, fileJob.InputInfo.Resolution);
            AddSeparator(tb);
            if (fileJob.InputInfo.Fps > 0) AddRun(tb, $"{fileJob.InputInfo.Fps:0.##}fps");
            AddSeparator(tb);
            // Show Input Bitrate here
            if (fileJob.InputInfo.Bitrate > 0) AddRun(tb, FormatBitrate(fileJob.InputInfo.Bitrate));
            AddSeparator(tb);
            AddRun(tb, $"{fileJob.InputInfo.SizeBytes / 1024.0 / 1024.0:0.0} MB");

            // 🔊 If we have loudness measured (after analysis), show it at the end
            if (fileJob.LoudnessData != null)
            {
                AddSeparator(tb);
                AddRun(tb, $"{fileJob.LoudnessData.InputI:0.0} LUFS", isBold: false, isDim: true);
            }
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
                string newTxt = $"{outMb:0.0} MB ({pct:+0.0;-0.0}%)";

                AddRun(tb, oldTxt, isBold: false, isDim: true);
                AddArrow(tb);
                AddRun(tb, newTxt, isBold: true, isDim: false);
            }
            else
            {
                AddRun(tb, $"{outMb:0.0} MB", isBold: true);
            }

            // 🔊 Loudness summary at the end (if we have LUFS info)
            if (fileJob.LoudnessData != null)
            {
                AddSeparator(tb);

                if (fileJob.TargetLufs.HasValue)
                {
                    // Show "measured → target LUFS", similar visual to size diff
                    string oldTxt = $"{fileJob.LoudnessData.InputI:0.0} LUFS";
                    string newTxt = $"{fileJob.TargetLufs.Value:0.0} LUFS";

                    AddRun(tb, oldTxt, isBold: false, isDim: true);
                    AddArrow(tb);
                    AddRun(tb, newTxt, isBold: true, isDim: false);
                }
                else
                {
                    // We measured loudness but didn't apply normalization
                    AddRun(tb, $"{fileJob.LoudnessData.InputI:0.0} LUFS", isBold: false, isDim: true);
                }
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
        if (isDim) run.Foreground = Brushes.Gray;
        // Note: Non-dim runs inherit the TextBlock color (White/Black)

        tb.Inlines.Add(run);
    }

    private static void AddArrow(TextBlock tb)
    {
        tb.Inlines.Add(new System.Windows.Documents.Run(" → ") { Foreground = Brushes.Gray });
    }

    private static void AddSeparator(TextBlock tb)
    {
        if (tb.Inlines.Count > 0)
            tb.Inlines.Add(new System.Windows.Documents.Run(" • ") { Foreground = Brushes.DarkGray });
    }

    private static string FormatBitrate(long bps)
    {
        if (bps >= 1_000_000) return $"{bps / 1_000_000.0:0.0} Mb/s";
        return $"{bps / 1000.0:0} kb/s";
    }
}
