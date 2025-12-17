using System;
using System.IO;
using System.Threading.Tasks;
using Windows.Graphics.Capture;
using Windows.Media.Capture;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
using Microsoft.UI.Xaml;
using ScreenRecorder.App.Helpers;
using Windows.Graphics;

namespace ScreenRecorder.App.ViewModels;

public class MainViewModel : ViewModelBase
{
    private const string DonateUrl = "https://buymeacoffee.com/rorrimaesu";
    private const string MysticsMirrorUrl = "https://mysticsmirror.com";
    private const string AuthorUrl = "https://author-andrewjgreen.com";
    private const string GithubUrl = "https://github.com/RorriMaesu?tab=repositories";
    private string _statusMessage = "Idle";
    private bool _isRecording;
    private string _selectedTab = "Screen"; // Screen, Window, Region
    private string _outputFolder = GetDefaultOutputFolder();
    private string _timeEstimate = "Disk Space: Calculating...";
    private string _selectedSourceDisplay = "None";
    private string _previewPlaceholderText = "Live Preview\n(choose a source)";

    private GraphicsCaptureItem? _selectedCaptureItem;
    private string? _selectedCaptureTab;

    private RectInt32? _selectedRegion;
    private Visibility _regionOverlayVisibility = Visibility.Collapsed;

    private double _micRms;
    private double _systemRms;
    private double _micGain = 1.0;
    private double _systemGain = 1.0;
    private bool _isMonitoring;
    private bool _useHardwareEncoding = true;

    private string? _currentRecordingFileName;
    private string? _lastSavedFileName;

    public event EventHandler<GraphicsCaptureItem?>? CaptureItemChanged;
    public event EventHandler<bool>? RecordingToggled;

    public GraphicsCaptureItem? SelectedCaptureItem => _selectedCaptureItem;

    public string? SelectedCaptureTab
    {
        get => _selectedCaptureTab;
        private set => SetProperty(ref _selectedCaptureTab, value);
    }

    public RectInt32? SelectedRegion
    {
        get => _selectedRegion;
        set => SetProperty(ref _selectedRegion, value);
    }

    public Visibility RegionOverlayVisibility
    {
        get => _regionOverlayVisibility;
        set => SetProperty(ref _regionOverlayVisibility, value);
    }

    private bool _canOpenSettings;
    private string _openSettingsButtonText = "Open Settings";
    private string? _settingsUri;
    private Visibility _openSettingsButtonVisibility = Visibility.Collapsed;

    public MainViewModel()
    {
        RecordCommand = new RelayCommand(ToggleRecording, () => true);
        PickOutputFolderCommand = new AsyncRelayCommand(PickOutputFolderAsync);
        PickCaptureSourceCommand = new AsyncRelayCommand(PickCaptureSourceAsync);
        OpenSettingsCommand = new AsyncRelayCommand(OpenSettingsAsync, () => CanOpenSettings);
        OpenOutputFolderCommand = new AsyncRelayCommand(OpenOutputFolderAsync, () => CanOpenOutputFolder);
        DonateCommand = new AsyncRelayCommand(OpenDonatePageAsync);
        OpenMysticsMirrorCommand = new AsyncRelayCommand(() => OpenExternalLinkAsync(MysticsMirrorUrl, "Opening MysticsMirror…"));
        OpenAuthorCommand = new AsyncRelayCommand(() => OpenExternalLinkAsync(AuthorUrl, "Opening author site…"));
        OpenGithubCommand = new AsyncRelayCommand(() => OpenExternalLinkAsync(GithubUrl, "Opening GitHub…"));

        OnPropertyChanged(nameof(SaveLocationText));
        OnPropertyChanged(nameof(CanOpenOutputFolder));
        OpenOutputFolderCommand.RaiseCanExecuteChanged();

        _ = RefreshDiskEstimateAsync();
    }

    private static string GetDefaultOutputFolder()
    {
        // Default to a project-relative "recordings" folder.
        // In this workspace, Environment.CurrentDirectory is typically "D:\\ScreenRecorder",
        // so this resolves to "D:\\ScreenRecorder\\recordings".
        try
        {
            var cwd = Environment.CurrentDirectory;
            if (!string.IsNullOrWhiteSpace(cwd))
            {
                var recordings = Path.GetFullPath(Path.Combine(cwd, "recordings"));
                Directory.CreateDirectory(recordings);
                return recordings;
            }
        }
        catch
        {
            // Fall back below.
        }

        return Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    public bool IsRecording
    {
        get => _isRecording;
        set
        {
            if (SetProperty(ref _isRecording, value))
            {
                StatusMessage = value ? "Recording..." : "Idle";
                OnPropertyChanged(nameof(RecordButtonText));
                OnPropertyChanged(nameof(RecordButtonVisibility));
                OnPropertyChanged(nameof(StopButtonVisibility));
                OnPropertyChanged(nameof(CurrentRecordingText));
            }
        }
    }

    public string RecordButtonText => IsRecording ? "Stop" : "Record";

    public Visibility RecordButtonVisibility => IsRecording ? Visibility.Collapsed : Visibility.Visible;
    public Visibility StopButtonVisibility => IsRecording ? Visibility.Visible : Visibility.Collapsed;

    public string SelectedTab
    {
        get => _selectedTab;
        set
        {
            if (SetProperty(ref _selectedTab, value))
            {
                if (!string.Equals(_selectedTab, "Region", StringComparison.OrdinalIgnoreCase))
                {
                    RegionOverlayVisibility = Visibility.Collapsed;
                    SelectedRegion = null;
                }

                // Prevent recording the wrong source when switching tabs.
                // The user expectation is that Screen/Window/Region are distinct modes.
                if (_selectedCaptureItem is not null)
                {
                    _selectedCaptureItem = null;
                    SelectedCaptureTab = null;
                    SelectedSourceDisplay = "None";
                    CaptureItemChanged?.Invoke(this, null);
                    StatusMessage = "Choose a capture source.";
                }

                PreviewPlaceholderText = _selectedCaptureItem is null
                    ? "Live Preview\n(choose a source)"
                    : $"Live Preview\n{_selectedTab}: {_selectedSourceDisplay}";
            }
        }
    }

    public string OutputFolder
    {
        get => _outputFolder;
        set
        {
            if (SetProperty(ref _outputFolder, value))
            {
                _ = RefreshDiskEstimateAsync();
                OnPropertyChanged(nameof(SaveLocationText));
                OnPropertyChanged(nameof(CanOpenOutputFolder));
                OpenOutputFolderCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool CanOpenOutputFolder => !string.IsNullOrWhiteSpace(OutputFolder) && Directory.Exists(OutputFolder);

    public string SaveLocationText => $"Saves to: {OutputFolder}";

    public string? CurrentRecordingFileName
    {
        get => _currentRecordingFileName;
        set
        {
            if (SetProperty(ref _currentRecordingFileName, value))
            {
                OnPropertyChanged(nameof(CurrentRecordingText));
            }
        }
    }

    public string CurrentRecordingText => IsRecording && !string.IsNullOrWhiteSpace(CurrentRecordingFileName)
        ? $"Recording: {CurrentRecordingFileName}"
        : string.Empty;

    public string? LastSavedFileName
    {
        get => _lastSavedFileName;
        set
        {
            if (SetProperty(ref _lastSavedFileName, value))
            {
                OnPropertyChanged(nameof(LastSavedText));
            }
        }
    }

    public string LastSavedText => !string.IsNullOrWhiteSpace(LastSavedFileName)
        ? $"Last saved: {LastSavedFileName}"
        : string.Empty;

    public string TimeEstimate
    {
        get => _timeEstimate;
        set => SetProperty(ref _timeEstimate, value);
    }

    public string SelectedSourceDisplay
    {
        get => _selectedSourceDisplay;
        set => SetProperty(ref _selectedSourceDisplay, value);
    }

    public string PreviewPlaceholderText
    {
        get => _previewPlaceholderText;
        set => SetProperty(ref _previewPlaceholderText, value);
    }

    public double MicRms
    {
        get => _micRms;
        set => SetProperty(ref _micRms, value);
    }

    public double SystemRms
    {
        get => _systemRms;
        set => SetProperty(ref _systemRms, value);
    }

    public double MicGain
    {
        get => _micGain;
        set => SetProperty(ref _micGain, value);
    }

    public double SystemGain
    {
        get => _systemGain;
        set => SetProperty(ref _systemGain, value);
    }

    public bool IsMonitoring
    {
        get => _isMonitoring;
        set => SetProperty(ref _isMonitoring, value);
    }

    public bool UseHardwareEncoding
    {
        get => _useHardwareEncoding;
        set => SetProperty(ref _useHardwareEncoding, value);
    }

    public bool CanOpenSettings
    {
        get => _canOpenSettings;
        set
        {
            if (SetProperty(ref _canOpenSettings, value))
            {
                OpenSettingsButtonVisibility = value ? Visibility.Visible : Visibility.Collapsed;
                OpenSettingsCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string OpenSettingsButtonText
    {
        get => _openSettingsButtonText;
        set => SetProperty(ref _openSettingsButtonText, value);
    }

    public Visibility OpenSettingsButtonVisibility
    {
        get => _openSettingsButtonVisibility;
        set => SetProperty(ref _openSettingsButtonVisibility, value);
    }

    // Commands
    public RelayCommand RecordCommand { get; }
    public AsyncRelayCommand PickOutputFolderCommand { get; }
    public AsyncRelayCommand PickCaptureSourceCommand { get; }
    public AsyncRelayCommand OpenSettingsCommand { get; }
    public AsyncRelayCommand OpenOutputFolderCommand { get; }
    public AsyncRelayCommand DonateCommand { get; }
    public AsyncRelayCommand OpenMysticsMirrorCommand { get; }
    public AsyncRelayCommand OpenAuthorCommand { get; }
    public AsyncRelayCommand OpenGithubCommand { get; }

    public async Task InitializeAsync()
    {
        await RefreshDiskEstimateAsync();
        await CheckPermissionsAsync();
    }

    private void ToggleRecording()
    {
        if (!IsRecording && _selectedCaptureItem is null)
        {
            StatusMessage = "Choose a capture source first.";
            return;
        }

        if (!IsRecording && !CanOpenOutputFolder)
        {
            StatusMessage = "Choose a valid output folder first.";
            return;
        }

        IsRecording = !IsRecording;
        if (IsRecording)
        {
            StatusMessage = "Recording...";
        }

        RecordingToggled?.Invoke(this, IsRecording);
    }

    private async Task PickOutputFolderAsync()
    {
        try
        {
            var picker = new FolderPicker();
            picker.FileTypeFilter.Add("*");

            if (ScreenRecorder.App.App.MainWindow is null)
            {
                StatusMessage = "Window not ready for picker.";
                return;
            }

            ScreenRecorder.App.Helpers.WindowInterop.InitializeWithWindow(picker, ScreenRecorder.App.App.MainWindow);
            StorageFolder? folder = await picker.PickSingleFolderAsync();
            if (folder is null)
            {
                return;
            }

            OutputFolder = folder.Path;
            StatusMessage = $"Output folder: {OutputFolder}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to pick folder: {ex.Message}";
        }
    }

    private async Task OpenOutputFolderAsync()
    {
        try
        {
            if (!CanOpenOutputFolder)
            {
                StatusMessage = "Output folder not available.";
                return;
            }

            var folder = await StorageFolder.GetFolderFromPathAsync(OutputFolder);
            await Launcher.LaunchFolderAsync(folder);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to open folder: {ex.Message}";
        }
    }

    private async Task OpenDonatePageAsync()
    {
        try
        {
            StatusMessage = "Opening support page…";
            await Launcher.LaunchUriAsync(new Uri(DonateUrl));
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to open link: {ex.Message}";
        }
    }

    private async Task OpenExternalLinkAsync(string url, string status)
    {
        try
        {
            StatusMessage = status;
            await Launcher.LaunchUriAsync(new Uri(url));
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to open link: {ex.Message}";
        }
    }

    private async Task PickCaptureSourceAsync()
    {
        try
        {
            Breadcrumbs.Write($"PickCaptureSourceAsync: begin tab={SelectedTab}");

            if (!GraphicsCaptureSession.IsSupported())
            {
                StatusMessage = "Windows Graphics Capture is not supported on this system.";
                Breadcrumbs.Write("PickCaptureSourceAsync: GraphicsCaptureSession.IsSupported=false");
                return;
            }

            // IMPORTANT:
            // - Picker-based capture always works (with user selection).
            // - Programmatic capture (monitor/window handles) can fail on some configurations.
            //   When it does, we fall back to the picker instead of silently doing nothing.

            if (string.Equals(SelectedTab, "Screen", StringComparison.OrdinalIgnoreCase)
                || string.Equals(SelectedTab, "Region", StringComparison.OrdinalIgnoreCase))
            {
                var hMonitor = MonitorInterop.GetPrimaryMonitor();
                Breadcrumbs.Write($"PickCaptureSourceAsync: primary monitor handle=0x{hMonitor.ToInt64():X}");

                if (hMonitor != IntPtr.Zero)
                {
                    Breadcrumbs.Write("PickCaptureSourceAsync: trying CreateForMonitor(primary)");
                    _selectedCaptureItem = CaptureItemFactory.TryCreateForMonitor(hMonitor);
                    if (_selectedCaptureItem is not null)
                    {
                        Breadcrumbs.Write($"PickCaptureSourceAsync: CreateForMonitor(primary) ok itemSize={_selectedCaptureItem.Size.Width}x{_selectedCaptureItem.Size.Height} name='{_selectedCaptureItem.DisplayName}'");
                    }
                }

                if (_selectedCaptureItem is null)
                {
                    Breadcrumbs.Write("PickCaptureSourceAsync: CreateForMonitor failed; will fall back to picker");
                }

                // Fallback: use the system picker (still lets user pick a display).
                if (_selectedCaptureItem is null)
                {
                    StatusMessage = "Opening capture picker…";
                    Breadcrumbs.Write("PickCaptureSourceAsync: opening picker (fallback)");
                    _selectedCaptureItem = await PickWithSystemPickerAsync("screen");
                    Breadcrumbs.Write($"PickCaptureSourceAsync: fallback picker result null={_selectedCaptureItem is null}");
                    if (_selectedCaptureItem is null)
                    {
                        StatusMessage = "No capture source selected (picker cancelled or hidden).";
                        return;
                    }

                    Breadcrumbs.Write($"PickCaptureSourceAsync: picker selected itemSize={_selectedCaptureItem.Size.Width}x{_selectedCaptureItem.Size.Height} name='{_selectedCaptureItem.DisplayName}'");

                    StatusMessage = "Selected from picker (direct screen selection unavailable).";
                }

                if (string.Equals(SelectedTab, "Region", StringComparison.OrdinalIgnoreCase))
                {
                    // Region is selected by dragging on the preview. Turn on overlay.
                    SelectedRegion = null;
                    RegionOverlayVisibility = Visibility.Visible;
                    StatusMessage = "Drag on the preview to select a region.";
                }
                else
                {
                    RegionOverlayVisibility = Visibility.Collapsed;
                    SelectedRegion = null;
                }
            }
            else
            {
                StatusMessage = "Opening capture picker…";
                Breadcrumbs.Write("PickCaptureSourceAsync: opening picker");
                _selectedCaptureItem = await PickWithSystemPickerAsync("window");

                Breadcrumbs.Write($"PickCaptureSourceAsync: picker result null={_selectedCaptureItem is null}");

                // Window mode: no region overlay.
                RegionOverlayVisibility = Visibility.Collapsed;
                SelectedRegion = null;
            }

            if (_selectedCaptureItem is null)
            {
                Breadcrumbs.Write("PickCaptureSourceAsync: end (no selection)");
                return;
            }

            SelectedCaptureTab = SelectedTab;

            CaptureItemChanged?.Invoke(this, _selectedCaptureItem);

            SelectedSourceDisplay = _selectedCaptureItem.DisplayName;
            if (string.IsNullOrWhiteSpace(SelectedSourceDisplay)
                && string.Equals(SelectedTab, "Screen", StringComparison.OrdinalIgnoreCase))
            {
                SelectedSourceDisplay = "Primary Display";
            }

            PreviewPlaceholderText = $"Live Preview\n{SelectedTab}: {SelectedSourceDisplay}";
            StatusMessage = $"Selected: {SelectedSourceDisplay}";
            Breadcrumbs.Write($"PickCaptureSourceAsync: end SelectedSourceDisplay={SelectedSourceDisplay}");
        }
        catch (Exception ex)
        {
            Breadcrumbs.Write("PickCaptureSourceAsync: exception");
            Breadcrumbs.Write(ex);
            StatusMessage = $"Failed to pick source: {ex.Message}";
        }
    }

    private static async Task<GraphicsCaptureItem?> PickWithSystemPickerAsync(string mode)
    {
        Breadcrumbs.Write($"PickWithSystemPickerAsync: begin mode={mode}");

        var window = ScreenRecorder.App.App.MainWindow;
        if (window is null)
        {
            Breadcrumbs.Write("PickWithSystemPickerAsync: MainWindow null");
            return null;
        }

        try
        {
            // Foreground the app; if we aren't foreground, the picker can appear behind us
            // or effectively look like "nothing happened".
            window.Activate();
        }
        catch (Exception ex)
        {
            Breadcrumbs.Write($"PickWithSystemPickerAsync: window.Activate failed: {ex.GetType().Name}: {ex.Message}");
        }

        // Give the message pump a moment to process activation.
        await Task.Yield();

        var picker = new GraphicsCapturePicker();
        try
        {
            var hwnd = ScreenRecorder.App.Helpers.WindowInterop.GetWindowHandle(window);
            Breadcrumbs.Write($"PickWithSystemPickerAsync: hwnd=0x{hwnd.ToInt64():X}");
            ScreenRecorder.App.Helpers.WindowInterop.InitializeWithWindow(picker, window);
        }
        catch (Exception ex)
        {
            Breadcrumbs.Write($"PickWithSystemPickerAsync: InitializeWithWindow failed: {ex.GetType().Name}: {ex.Message}");
            throw;
        }

        var item = await picker.PickSingleItemAsync();
        Breadcrumbs.Write($"PickWithSystemPickerAsync: end selected null={item is null}");
        return item;
    }

    private async Task CheckPermissionsAsync()
    {
        CanOpenSettings = false;
        _settingsUri = null;

        // Microphone access check
        try
        {
            var capture = new MediaCapture();
            var settings = new MediaCaptureInitializationSettings
            {
                StreamingCaptureMode = StreamingCaptureMode.Audio,
                MemoryPreference = MediaCaptureMemoryPreference.Cpu
            };

            await capture.InitializeAsync(settings);
            capture.Dispose();
        }
        catch (UnauthorizedAccessException)
        {
            StatusMessage = "Microphone permission is blocked. Enable it in Privacy settings.";
            CanOpenSettings = true;
            OpenSettingsButtonText = "Open Microphone Settings";
            _settingsUri = "ms-settings:privacy-microphone";
        }
        catch
        {
            // Non-fatal: device may be absent; we handle real device selection later.
        }
    }

    private async Task OpenSettingsAsync()
    {
        if (string.IsNullOrWhiteSpace(_settingsUri))
        {
            return;
        }

        try
        {
            _ = await Launcher.LaunchUriAsync(new Uri(_settingsUri));
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to open Settings: {ex.Message}";
        }
    }

    private Task RefreshDiskEstimateAsync()
    {
        try
        {
            // MVP placeholder: assume ~12 Mbps video + 192 kbps audio.
            // This is intentionally conservative and will be replaced by real preset math.
            const long reserveBytes = 500L * 1024L * 1024L;
            const double bitsPerSecond = 12_000_000 + 192_000;
            var bytesPerSecond = bitsPerSecond / 8.0;

            var root = Path.GetPathRoot(OutputFolder);
            if (string.IsNullOrWhiteSpace(root))
            {
                TimeEstimate = "Disk Space: Unknown";
                return Task.CompletedTask;
            }

            var drive = new DriveInfo(root);
            var free = Math.Max(0, drive.AvailableFreeSpace - reserveBytes);
            var seconds = free / bytesPerSecond;
            var ts = TimeSpan.FromSeconds(seconds);

            TimeEstimate = $"Disk Space: {FormatBytes(drive.AvailableFreeSpace)} (~{FormatDuration(ts)} available)";
        }
        catch
        {
            TimeEstimate = "Disk Space: Unknown";
        }

        return Task.CompletedTask;
    }

    private static string FormatDuration(TimeSpan ts)
    {
        if (ts.TotalDays >= 1)
        {
            return $"{(int)ts.TotalDays}d {ts.Hours}h";
        }

        if (ts.TotalHours >= 1)
        {
            return $"{(int)ts.TotalHours}h {ts.Minutes}m";
        }

        return $"{Math.Max(0, ts.Minutes)}m";
    }

    private static string FormatBytes(long bytes)
    {
        string[] suffixes = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var suffix = 0;
        while (value >= 1024 && suffix < suffixes.Length - 1)
        {
            value /= 1024;
            suffix++;
        }

        return $"{value:0.#}{suffixes[suffix]}";
    }
}
