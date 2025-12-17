using System;
using System.Globalization;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using ScreenRecorder.App.Helpers;
using ScreenRecorder.App.Services.Ffmpeg;
using Windows.Media.Core;
using Windows.Media.Editing;
using Windows.Media.Playback;
using Windows.Storage;
using WinRT.Interop;

namespace ScreenRecorder.App.Views;

public sealed partial class PostCaptureTrimmerWindow : Window
{
    private const string DefaultsStartKey = "PostCaptureTrim.StartSeconds";
    private const string DefaultsEndKey = "PostCaptureTrim.EndSeconds";

    private readonly string _path;
    private MediaPlayer? _player;
    private TimeSpan? _duration;
    private TimeSpan? _fileDuration;
    private readonly CancellationTokenSource _cts = new();
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _timelineTimer;
    private bool _isUserScrubbing;
    private bool _updatingTimelineFromPlayer;
    private bool _resumeAfterScrub;
    private TimeSpan? _lastSeekTarget;
    private bool _didInitialResize;

    public PostCaptureTrimmerWindow(string finalizedFilePath)
    {
        _path = finalizedFilePath ?? throw new ArgumentNullException(nameof(finalizedFilePath));

        InitializeComponent();

        // Ensure title bar uses the app name.
        try
        {
            Title = "Prism Capture";

            var hwnd = WindowInterop.GetWindowHandle(this);
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            var appWindow = AppWindow.GetFromWindowId(windowId);
            appWindow.Title = "Prism Capture";
        }
        catch { }

        // Best-effort initial size. We also resize again on first activation
        // because the underlying HWND/appwindow can be unavailable this early.
        TrySizeWindow(980, 720);

        FileText.Text = _path;

        LoadDefaultsIntoSliders();
        UpdateRangeText();

        // WinUI's MediaPlayerElement may not create an internal MediaPlayer until later.
        // Create our own upfront so preview is always available and controllable.
        try
        {
            _player = new MediaPlayer();
            Player.SetMediaPlayer(_player);

            _player.MediaOpened -= OnMediaOpened;
            _player.MediaOpened += OnMediaOpened;
            _player.MediaFailed -= OnMediaFailed;
            _player.MediaFailed += OnMediaFailed;

            try
            {
                _player.PlaybackSession.PlaybackStateChanged -= OnPreviewPlaybackStateChanged;
                _player.PlaybackSession.PlaybackStateChanged += OnPreviewPlaybackStateChanged;
            }
            catch { }
        }
        catch
        {
            _player = null;
        }

        // Initialize preview when the element is loaded (its internal MediaPlayer can be null before then).
        Player.Loaded += OnPlayerLoaded;

        Activated += OnActivated;

        Closed += OnClosed;
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        if (_didInitialResize)
        {
            return;
        }

        _didInitialResize = true;

        // Ensure the buttons are visible on first show, without requiring a manual resize.
        TrySizeWindow(980, 720);
    }

    private async void OnPlayerLoaded(object sender, RoutedEventArgs e)
    {
        Player.Loaded -= OnPlayerLoaded;

        var mp = _player;
        if (mp is null)
        {
            StatusText.Text = "Preview player unavailable.";
            return;
        }

        try
        {
            StatusText.Text = string.Empty;
            // Use a StorageFile-backed source to avoid URI parsing quirks for Windows paths
            // and to improve seeking reliability.
            var file = await StorageFile.GetFileFromPathAsync(_path);

            // Prefer a duration computed from file properties; MediaPlayer.PlaybackSession.NaturalDuration
            // can be wildly incorrect (or effectively "unknown") for some MP4s.
            try
            {
                var props = await file.Properties.GetVideoPropertiesAsync();
                var fd = props.Duration;
                Breadcrumbs.Write($"PostCaptureTrimmer: file duration={fd}");

                if (IsUsableDuration(fd))
                {
                    _fileDuration = fd;
                    _duration ??= fd;
                }
            }
            catch (Exception ex)
            {
                Breadcrumbs.Write($"PostCaptureTrimmer: file duration probe failed: {ex.Message}");
            }

            // Fallback 1: MediaClip duration sometimes succeeds when file properties return 0.
            if (_fileDuration is null)
            {
                try
                {
                    var clip = await MediaClip.CreateFromFileAsync(file);
                    var cd = clip.OriginalDuration;
                    Breadcrumbs.Write($"PostCaptureTrimmer: media clip duration={cd}");

                    if (IsUsableDuration(cd))
                    {
                        _fileDuration = cd;
                        _duration ??= cd;
                    }
                }
                catch (Exception ex)
                {
                    Breadcrumbs.Write($"PostCaptureTrimmer: media clip duration probe failed: {ex.Message}");
                }
            }

            // Fallback 2: ffprobe (if bundled alongside ffmpeg) to get accurate container duration.
            if (_fileDuration is null)
            {
                try
                {
                    var probed = await TryProbeDurationWithFfprobeAsync(_path, _cts.Token);
                    if (probed is not null && IsUsableDuration(probed.Value))
                    {
                        Breadcrumbs.Write($"PostCaptureTrimmer: ffprobe duration={probed}");
                        _fileDuration = probed;
                        _duration ??= probed;
                    }
                }
                catch (Exception ex)
                {
                    Breadcrumbs.Write($"PostCaptureTrimmer: ffprobe duration probe failed: {ex.Message}");
                }
            }

            mp.Source = MediaSource.CreateFromStorageFile(file);

            EnsureTimelineTimer();
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
    }

    private static async Task<TimeSpan?> TryProbeDurationWithFfprobeAsync(string inputPath, CancellationToken ct)
    {
        try
        {
            if (!FfmpegAvMuxer.IsFfmpegAvailable(out var ffmpeg, out _))
            {
                return null;
            }

            var ffprobe = ResolveFfprobePath(ffmpeg);
            if (string.IsNullOrWhiteSpace(ffprobe))
            {
                return null;
            }

            var psi = new ProcessStartInfo
            {
                FileName = ffprobe,
                Arguments = $"-v error -show_entries format=duration -of default=noprint_wrappers=1:nokey=1 \"{inputPath}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var p = Process.Start(psi);
            if (p is null)
            {
                return null;
            }

            var stdoutTask = p.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = p.StandardError.ReadToEndAsync(ct);

            await p.WaitForExitAsync(ct);

            var stdout = (await stdoutTask).Trim();
            var stderr = (await stderrTask).Trim();

            if (p.ExitCode != 0)
            {
                if (!string.IsNullOrWhiteSpace(stderr))
                {
                    Breadcrumbs.Write($"PostCaptureTrimmer: ffprobe exit={p.ExitCode} err={stderr}");
                }

                return null;
            }

            if (double.TryParse(stdout, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && seconds > 0)
            {
                return TimeSpan.FromSeconds(seconds);
            }

            if (!string.IsNullOrWhiteSpace(stderr))
            {
                Breadcrumbs.Write($"PostCaptureTrimmer: ffprobe parse failed stdout='{stdout}' err='{stderr}'");
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    private static string? ResolveFfprobePath(string? ffmpegPath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(ffmpegPath))
            {
                return null;
            }

            // If ffmpeg is resolved as a full path, try a sibling ffprobe.exe.
            if (Path.IsPathRooted(ffmpegPath))
            {
                var dir = Path.GetDirectoryName(ffmpegPath);
                if (!string.IsNullOrWhiteSpace(dir))
                {
                    var candidate = Path.Combine(dir, "ffprobe.exe");
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
            }

            // Otherwise fall back to PATH resolution.
            return "ffprobe";
        }
        catch
        {
            return null;
        }
    }

    private static bool IsUsableDuration(TimeSpan ts)
    {
        // Treat 0 / negative as unknown and reject absurd durations that indicate metadata failure.
        if (ts <= TimeSpan.Zero)
        {
            return false;
        }

        if (ts == TimeSpan.MaxValue)
        {
            return false;
        }

        // A screen recording longer than a year is almost certainly a broken duration.
        if (ts.TotalDays > 365)
        {
            return false;
        }

        return true;
    }

    private void EnsureTimelineTimer()
    {
        if (_timelineTimer is not null)
        {
            return;
        }

        _timelineTimer = DispatcherQueue.CreateTimer();
        _timelineTimer.Interval = TimeSpan.FromMilliseconds(200);
        _timelineTimer.Tick += (_, _) =>
        {
            try
            {
                if (_isUserScrubbing)
                {
                    return;
                }

                var mp = _player;
                if (mp is null)
                {
                    return;
                }

                var dur = _duration;
                if (dur is null || dur.Value <= TimeSpan.Zero)
                {
                    return;
                }

                var pos = mp.PlaybackSession.Position;
                var maxSeconds = dur.Value.TotalSeconds;

                _updatingTimelineFromPlayer = true;
                TimelineSlider.Maximum = maxSeconds;

                var seconds = pos.TotalSeconds;
                if (seconds < 0) seconds = 0;
                if (seconds > maxSeconds) seconds = maxSeconds;
                TimelineSlider.Value = seconds;
                _updatingTimelineFromPlayer = false;

                TimelineValueText.Text = $"{Format(pos)} / {Format(dur.Value)}";
            }
            catch
            {
                _updatingTimelineFromPlayer = false;
            }
        };
        _timelineTimer.Start();
    }

    private void TrySizeWindow(int width, int height)
    {
        try
        {
            var hwnd = WindowNative.GetWindowHandle(this);
            var windowId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            var appWindow = AppWindow.GetFromWindowId(windowId);
            appWindow.Resize(new Windows.Graphics.SizeInt32(width, height));
        }
        catch
        {
            // Best-effort only.
        }
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        try { _cts.Cancel(); } catch { }

        try
        {
            if (_timelineTimer is not null)
            {
                _timelineTimer.Stop();
                _timelineTimer = null;
            }
        }
        catch { }

        try { _player?.Pause(); } catch { }
        try
        {
            if (_player is not null)
            {
                _player.Source = null;
            }
        }
        catch { }
        try { DetachAndDisposePlayer(); } catch { }
        try { _cts.Dispose(); } catch { }
    }

    private void DetachAndDisposePlayer()
    {
        // Aggressively detach the MediaPlayer from the element and dispose it.
        // This avoids file locks and helps avoid native crashes when the underlying file is replaced
        // while the media pipeline is still tearing down.
        try { Player.SetMediaPlayer(null); } catch { }

        var mp = _player;
        _player = null;

        try { mp?.Pause(); } catch { }
        try { if (mp is not null) mp.Source = null; } catch { }
        try { mp?.Dispose(); } catch { }
    }

    private void OnMediaOpened(MediaPlayer sender, object args)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                var natural = sender.PlaybackSession.NaturalDuration;
                var chosen = IsUsableDuration(natural) ? natural : (_fileDuration ?? _duration);

                if (IsUsableDuration(natural))
                {
                    Breadcrumbs.Write($"PostCaptureTrimmer: natural duration usable={natural}");
                }
                else
                {
                    Breadcrumbs.Write($"PostCaptureTrimmer: natural duration UNUSABLE={natural}");
                }

                _duration = chosen;

                if (_duration is null || !IsUsableDuration(_duration.Value))
                {
                    TimelineSlider.IsEnabled = false;
                    RangeText.Text = "Loading duration…";
                    StatusText.Text = "Unable to determine clip duration for scrubbing.";
                    return;
                }

                try
                {
                    // Only enable the slider once the player reports a usable duration.
                    TimelineSlider.IsEnabled = _duration.Value > TimeSpan.Zero && sender.PlaybackSession.CanSeek;
                }
                catch
                {
                    TimelineSlider.IsEnabled = _duration.Value > TimeSpan.Zero;
                }

                if (_duration.Value > TimeSpan.Zero)
                {
                    _updatingTimelineFromPlayer = true;
                    TimelineSlider.Maximum = _duration.Value.TotalSeconds;
                    TimelineSlider.Value = 0;
                    _updatingTimelineFromPlayer = false;
                    TimelineValueText.Text = $"{Format(TimeSpan.Zero)} / {Format(_duration.Value)}";
                }

                // If the user hasn't saved defaults, apply a friendly default (5s off start/end for long clips).
                if (!HasSavedDefaults())
                {
                    var d = _duration.Value;
                    var canDefaultFive = d.TotalSeconds >= 15;

                    StartTrimSlider.Value = canDefaultFive ? 5.0 : 0.0;
                    EndTrimSlider.Value = canDefaultFive ? 5.0 : 0.0;
                }

                UpdateRangeText();
                TrySeekToStart();

                try
                {
                    Breadcrumbs.Write($"PostCaptureTrimmer: media opened duration={_duration} canSeek={sender.PlaybackSession.CanSeek} state={sender.PlaybackSession.PlaybackState}");
                }
                catch { }
            }
            catch (Exception ex)
            {
                StatusText.Text = ex.Message;
            }
        });
    }

    private void OnPreviewPlaybackStateChanged(MediaPlaybackSession sender, object args)
    {
        try
        {
            // Keep this lightweight; it helps diagnose why playback restarts from 0.
            var pos = sender.Position;
            Breadcrumbs.Write($"PostCaptureTrimmer: playback state={sender.PlaybackState} pos={pos}");
        }
        catch
        {
        }
    }

    private static bool HasSavedDefaults()
    {
        try
        {
            var settings = ApplicationData.Current.LocalSettings;
            return settings.Values.ContainsKey(DefaultsStartKey) || settings.Values.ContainsKey(DefaultsEndKey);
        }
        catch
        {
            return false;
        }
    }

    private void OnMediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            StatusText.Text = string.IsNullOrWhiteSpace(args.ErrorMessage)
                ? args.Error.ToString()
                : $"{args.Error}: {args.ErrorMessage}";
        });
    }

    private void OnTrimValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        // Keep the preview aligned to the chosen start trim.
        if (ReferenceEquals(sender, StartTrimSlider))
        {
            TrySeekToStart();
        }

        UpdateRangeText();
    }

    private void TrySeekToStart()
    {
        try
        {
            if (_duration is null)
            {
                return;
            }

            var start = TimeSpan.FromSeconds(StartTrimSlider.Value);
            var mp = _player;
            if (mp is null)
            {
                return;
            }

            // Clamp seek inside the media duration.
            var max = _duration.Value;
            if (start < TimeSpan.Zero) start = TimeSpan.Zero;
            if (start > max) start = max;

            SeekTo(mp, start, "trim-start");

            if (!_isUserScrubbing)
            {
                _updatingTimelineFromPlayer = true;
                TimelineSlider.Value = start.TotalSeconds;
                _updatingTimelineFromPlayer = false;
                TimelineValueText.Text = $"{Format(start)} / {Format(_duration.Value)}";
            }
        }
        catch
        {
        }
    }

    private void OnTimelinePointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _isUserScrubbing = true;

        // If the user scrubs while playing, pause during the drag and resume on release.
        try
        {
            var mp = _player;
            if (mp is not null)
            {
                _resumeAfterScrub = mp.PlaybackSession.PlaybackState == MediaPlaybackState.Playing;
                if (_resumeAfterScrub)
                {
                    mp.Pause();
                }
            }
        }
        catch
        {
            _resumeAfterScrub = false;
        }
    }

    private void OnTimelinePointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _isUserScrubbing = false;
        // Apply a final seek to the released position.
        TrySeekToTimelineValue();

        if (_resumeAfterScrub)
        {
            try { _player?.Play(); } catch { }
        }

        _resumeAfterScrub = false;
    }

    private void OnTimelineValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        // Seek on any user-driven slider changes (click, drag, keyboard).
        // Avoid seeking when we're updating the slider due to playback.
        if (_updatingTimelineFromPlayer)
        {
            return;
        }

        TrySeekToTimelineValue();
    }

    private void TrySeekToTimelineValue()
    {
        try
        {
            var mp = _player;
            var dur = _duration;
            if (mp is null || dur is null)
            {
                return;
            }

            try
            {
                if (!mp.PlaybackSession.CanSeek)
                {
                    StatusText.Text = "Preview is not seekable yet.";
                    Breadcrumbs.Write("PostCaptureTrimmer: seek requested but PlaybackSession.CanSeek=false");
                    return;
                }
            }
            catch
            {
                // If CanSeek throws for some reason, still attempt a best-effort seek.
            }

            var maxSeconds = dur.Value.TotalSeconds;
            if (maxSeconds <= 0)
            {
                return;
            }

            var seconds = TimelineSlider.Value;
            if (seconds < 0) seconds = 0;
            if (seconds > maxSeconds) seconds = maxSeconds;

            var target = TimeSpan.FromSeconds(seconds);

            // Some pipelines ignore position changes while transitioning playback states.
            // Ensure the seek sticks by pausing when not actively scrubbing.
            var wasPlaying = false;
            try { wasPlaying = mp.PlaybackSession.PlaybackState == MediaPlaybackState.Playing; } catch { }

            if (!(_isUserScrubbing || wasPlaying))
            {
                try { mp.Pause(); } catch { }
            }

            SeekTo(mp, target, "timeline");

            if (wasPlaying && !_isUserScrubbing)
            {
                try { mp.Play(); } catch { }
            }

            TimelineValueText.Text = $"{Format(target)} / {Format(dur.Value)}";
        }
        catch
        {
        }
    }

    private void SeekTo(MediaPlayer mp, TimeSpan target, string reason)
    {
        try
        {
            _lastSeekTarget = target;

            var wasPlaying = false;
            try { wasPlaying = mp.PlaybackSession.PlaybackState == MediaPlaybackState.Playing; } catch { }

            if (!(_isUserScrubbing || wasPlaying))
            {
                try { mp.Pause(); } catch { }
            }

            // Prefer setting the session position; MediaPlayerElement transport controls track this.
            mp.PlaybackSession.Position = target;

            TimeSpan after;
            try { after = mp.PlaybackSession.Position; } catch { after = TimeSpan.FromSeconds(-1); }

            Breadcrumbs.Write($"PostCaptureTrimmer: seek reason={reason} target={target} after={after} state={mp.PlaybackSession.PlaybackState}");

            _ = VerifySeekAsync(target, reason);

            if (wasPlaying && !_isUserScrubbing)
            {
                try { mp.Play(); } catch { }
            }
        }
        catch
        {
        }
    }

    private async Task VerifySeekAsync(TimeSpan target, string reason)
    {
        try
        {
            await Task.Delay(250, _cts.Token);

            var mp = _player;
            if (mp is null)
            {
                return;
            }

            var pos = mp.PlaybackSession.Position;
            var state = mp.PlaybackSession.PlaybackState;

            // If it snaps back to ~0, we'll catch that in Breadcrumbs.
            Breadcrumbs.Write($"PostCaptureTrimmer: seek-check reason={reason} target={target} pos={pos} state={state}");
        }
        catch
        {
        }
    }

    private void LoadDefaultsIntoSliders()
    {
        try
        {
            var settings = ApplicationData.Current.LocalSettings;
            if (settings.Values.TryGetValue(DefaultsStartKey, out var sObj) && sObj is double s)
            {
                StartTrimSlider.Value = ClampSeconds(s);
            }

            if (settings.Values.TryGetValue(DefaultsEndKey, out var eObj) && eObj is double e)
            {
                EndTrimSlider.Value = ClampSeconds(e);
            }
        }
        catch
        {
        }
    }

    private static double ClampSeconds(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return 0;
        }

        if (value < 0) return 0;
        if (value > 5) return 5;
        return value;
    }

    private void OnSaveDefaults(object sender, RoutedEventArgs e)
    {
        try
        {
            var settings = ApplicationData.Current.LocalSettings;
            settings.Values[DefaultsStartKey] = ClampSeconds(StartTrimSlider.Value);
            settings.Values[DefaultsEndKey] = ClampSeconds(EndTrimSlider.Value);
            StatusText.Text = "Defaults saved.";
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
        }
    }

    private void UpdateRangeText()
    {
        var inv = CultureInfo.InvariantCulture;

        StartTrimValueText.Text = StartTrimSlider.Value.ToString("0.00", inv) + "s";
        EndTrimValueText.Text = EndTrimSlider.Value.ToString("0.00", inv) + "s";

        if (_duration is null)
        {
            RangeText.Text = "Loading duration…";
            TrimButton.IsEnabled = false;
            return;
        }

        var total = _duration.Value;
        var start = TimeSpan.FromSeconds(StartTrimSlider.Value);
        var endTrim = TimeSpan.FromSeconds(EndTrimSlider.Value);
        var outDuration = total - start - endTrim;

        if (outDuration <= TimeSpan.FromMilliseconds(250))
        {
            RangeText.Text = $"Resulting clip would be empty (total {Format(total)}).";
            TrimButton.IsEnabled = false;
            return;
        }

        RangeText.Text = $"Total {Format(total)} → output {Format(outDuration)} (start {Format(start)}, end trim {Format(endTrim)}).";
        TrimButton.IsEnabled = true;
    }

    private static string Format(TimeSpan ts)
    {
        if (ts.TotalHours >= 1)
        {
            return ts.ToString(@"hh\:mm\:ss");
        }

        return ts.ToString(@"mm\:ss");
    }

    private void SetBusy(bool busy)
    {
        BusyRing.IsActive = busy;
        BusyRing.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        KeepButton.IsEnabled = !busy;
        TrimButton.IsEnabled = !busy;
        StartTrimSlider.IsEnabled = !busy;
        EndTrimSlider.IsEnabled = !busy;

        if (!busy)
        {
            UpdateRangeText();
        }
    }

    private void OnKeep(object sender, RoutedEventArgs e)
    {
        try
        {
            Close();
        }
        catch
        {
        }
    }

    private async void OnTrim(object sender, RoutedEventArgs e)
    {
        if (_duration is null)
        {
            return;
        }

        StatusText.Text = string.Empty;

        if (!File.Exists(_path))
        {
            StatusText.Text = "File not found.";
            return;
        }

        var total = _duration.Value;
        var start = TimeSpan.FromSeconds(StartTrimSlider.Value);
        var endTrim = TimeSpan.FromSeconds(EndTrimSlider.Value);
        var outDuration = total - start - endTrim;

        if (outDuration <= TimeSpan.FromMilliseconds(250))
        {
            StatusText.Text = "Trim would create an empty file.";
            return;
        }

        // Release the file handle held by the preview player before invoking ffmpeg and replacing the file.
        // Also detach the MediaPlayerElement entirely to avoid native pipeline issues while overwriting the file.
        try
        {
            if (_timelineTimer is not null)
            {
                _timelineTimer.Stop();
                _timelineTimer = null;
            }
        }
        catch { }

        try { DetachAndDisposePlayer(); } catch { }

        // Give the media pipeline a moment to release resources before we overwrite the file.
        try { await Task.Delay(150, _cts.Token); } catch { }

        var closed = false;
        try
        {
            SetBusy(true);
            Breadcrumbs.Write($"PostCaptureTrimmer: trimming in-place path={_path} start={start} duration={outDuration}");

            await FfmpegTrimmer.TrimInPlaceAsync(_path, start, outDuration, _cts.Token);

            Breadcrumbs.Write("PostCaptureTrimmer: trim succeeded");
            SetBusy(false);

            // Closing can fail (e.g., if the window is already closing/closed). Treat that as non-fatal.
            try
            {
                Close();
            }
            catch (COMException ex) when ((uint)ex.HResult == 0x80004004)
            {
                Breadcrumbs.Write("PostCaptureTrimmer: Close() aborted (window likely already closing)");
            }
            catch (Exception ex)
            {
                Breadcrumbs.Write("PostCaptureTrimmer: Close() failed");
                Breadcrumbs.Write(ex);

                // Keep the window open if we can't close; show success so the user isn't told trim failed.
                StatusText.Text = "Trim complete.";
            }

            closed = true;
        }
        catch (Exception ex)
        {
            Breadcrumbs.Write("PostCaptureTrimmer: trim failed");
            Breadcrumbs.Write(ex);
            StatusText.Text = ex.Message;
        }
        finally
        {
            if (!closed)
            {
                SetBusy(false);
            }
        }
    }
}
