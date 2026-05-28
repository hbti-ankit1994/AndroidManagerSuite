using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AndroidManagerSuite.App.Services;
using Microsoft.Win32;

namespace AndroidManagerSuite.App;

public partial class MainWindow : Window
{
    private static readonly string DefaultAdbSdkPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SDK", "platform-tools");
    private static readonly string ScrcpyPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SDK", "scrcpy", "scrcpy.exe");

    private readonly AdbService _adbService = new(DefaultAdbSdkPath);
    private readonly MainWindowState _state = new();
    private readonly DispatcherTimer _autoRefreshTimer;
    private readonly DispatcherTimer _toastTimer;
    private readonly DispatcherTimer _heartbeatTimer;
    private readonly List<string> _allPackages = [];
    private CancellationTokenSource? _workCancellation;
    private TaskCompletionSource<bool>? _dialogCompletion;
    private bool _suppressDeviceSelectionRefresh;
    private bool _performanceRefreshInFlight;
    private string? _lastDownloadDirectory;
    private Process? _scrcpyProcess;
    private string? _scrcpyWindowTitle;

    private static readonly HashSet<string> PreviewImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg",
        ".jpeg",
        ".png",
        ".bmp",
        ".gif"
    };

    public MainWindow()
    {
        InitializeComponent();
        DeviceComboBox.ItemsSource = _state.Devices;
        FilesListView.ItemsSource = _state.Files;
        PackagesListBox.ItemsSource = _state.Packages;
        ProcessesListView.ItemsSource = _state.Processes;

        _autoRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _autoRefreshTimer.Tick += async (_, _) =>
        {
            await RefreshPerformanceAsync();
            await RefreshBatteryAsync();
        };

        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            ToastBorder.Visibility = Visibility.Collapsed;
        };
        
        _heartbeatTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _heartbeatTimer.Tick += async (_, _) => await HandleHeartbeatAsync();

        AutoRefreshCheckBox.IsChecked = true;
        UpdateCommandState();
        LoadSavedIps();

        Loaded += async (_, _) => 
        {
            await RefreshDevicesAsync();
            _heartbeatTimer.Start();
        };
        Closing += MainWindow_OnClosing;
    }

    private async void RefreshDevicesButton_OnClick(object sender, RoutedEventArgs e)
    {
        await RefreshDevicesAsync();
    }

    private async void DeviceComboBox_OnSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        _state.SelectedDevice = DeviceComboBox.SelectedItem as AdbDevice;
        UpdateCommandState();
        if (!_suppressDeviceSelectionRefresh)
        {
            if (_state.SelectedDevice is not null)
            {
                await RefreshFilesAsync();
                await LoadDeviceSummaryAsync();
            }
            else
            {
                ClearDeviceData();
            }
        }
    }



    private async void RefreshFilesButton_OnClick(object sender, RoutedEventArgs e)
    {
        await RefreshFilesAsync();
    }

    private async void OpenPathButton_OnClick(object sender, RoutedEventArgs e)
    {
        await RefreshFilesAsync();
    }

    private async void RemotePathTextBox_OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            await RefreshFilesAsync();
        }
    }

    private async void UpDirectoryButton_OnClick(object sender, RoutedEventArgs e)
    {
        RemotePathTextBox.Text = GetParentPath(RemotePathTextBox.Text);
        await RefreshFilesAsync();
    }

    private async void FilesListView_OnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FilesListView.SelectedItem is not FileEntry entry)
        {
            return;
        }

        if (entry.IsDirectory)
        {
            RemotePathTextBox.Text = entry.Path;
            await RefreshFilesAsync();
            return;
        }

        if (IsPreviewableImage(entry))
        {
            await PreviewImageAsync(entry);
        }
    }

    private void FilesListView_OnPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var item = FindAncestor<ListViewItem>((DependencyObject)e.OriginalSource);
        if (item is not null)
        {
            item.IsSelected = true;
            item.Focus();
        }
    }

    private async void PushFileButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (!TryGetSelectedSerial(out var serial))
        {
            return;
        }

        var dialog = new OpenFileDialog { Title = "Choose a file to push to device" };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        await RunWithStatusAsync("Pushing file...", async token =>
        {
            await _adbService.PushFileAsync(serial, dialog.FileName, RemotePathTextBox.Text, token);
            await RefreshFilesCoreAsync(token);
        });
    }

    private async void PullFileButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (!TryGetSelectedSerial(out var serial) || FilesListView.SelectedItem is not FileEntry entry)
        {
            SetStatus("Select a device and file first.");
            return;
        }

        var dialog = new OpenFolderDialog { Title = "Choose a local destination folder" };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        await RunWithStatusAsync("Pulling file...", async token =>
        {
            await _adbService.PullFileAsync(serial, entry.Path, dialog.FolderName, token);
            _lastDownloadDirectory = dialog.FolderName;
            ShowToast("Download complete", $"{entry.Name} saved to {dialog.FolderName}");
            SetStatus($"Downloaded {entry.Name} to {dialog.FolderName}.");
        });
    }

    private async void DownloadSelectedFileMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        await DownloadSelectedFileToDefaultFolderAsync();
    }

    private async void DeleteSelectedFileMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        await DeleteSelectedFileAsync();
    }

    private async void DeleteFileButton_OnClick(object sender, RoutedEventArgs e)
    {
        await DeleteSelectedFileAsync();
    }

    private async Task DeleteSelectedFileAsync()
    {
        if (!TryGetSelectedSerial(out var serial) || FilesListView.SelectedItem is not FileEntry entry)
        {
            SetStatus("Select a device and file first.");
            return;
        }

        if (!await ShowConfirmAsync("Delete device file", $"Delete {entry.Path} from the connected device?"))
        {
            return;
        }

        await RunWithStatusAsync("Deleting...", async token =>
        {
            await _adbService.DeleteFileAsync(serial, entry.Path, entry.IsDirectory, token);
            await RefreshFilesCoreAsync(token);
        });
    }

    private void BrowseApkButton_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose APK",
            Filter = "Android packages (*.apk)|*.apk|All files (*.*)|*.*"
        };

        if (dialog.ShowDialog(this) == true)
        {
            ApkPathTextBox.Text = dialog.FileName;
            UpdateCommandState();
        }
    }

    private void ApkPathTextBox_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateCommandState();
    }

    private async void InstallApkButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (!TryGetSelectedSerial(out var serial))
        {
            return;
        }

        if (!File.Exists(ApkPathTextBox.Text))
        {
            SetStatus("Choose a valid APK first.");
            UpdateCommandState();
            return;
        }

        await RunWithStatusAsync("Installing APK...", async token =>
        {
            await _adbService.InstallApkAsync(serial, ApkPathTextBox.Text, token);
            await RefreshPackagesCoreAsync(token);
        });
    }

    private async void RefreshPackagesButton_OnClick(object sender, RoutedEventArgs e)
    {
        await RefreshPackagesAsync();
    }

    private async void UninstallPackageButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (!TryGetSelectedSerial(out var serial) || PackagesListBox.SelectedItem is not string packageName)
        {
            SetStatus("Select a device and package first.");
            return;
        }

        if (!await ShowConfirmAsync("Uninstall package", $"Uninstall {packageName} from the connected device?"))
        {
            return;
        }

        await RunWithStatusAsync("Uninstalling package...", async token =>
        {
            await _adbService.UninstallPackageAsync(serial, packageName, token);
            await RefreshPackagesCoreAsync(token);
        });
    }

    private void PackageFilterTextBox_OnTextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        ApplyPackageFilter();
    }

    private async void RefreshBatteryButton_OnClick(object sender, RoutedEventArgs e)
    {
        await RefreshBatteryAsync();
    }

    private async void RefreshPerformanceButton_OnClick(object sender, RoutedEventArgs e)
    {
        await RefreshPerformanceAsync();
        await RefreshBatteryAsync();
    }

    private void AutoRefreshCheckBox_OnChanged(object sender, RoutedEventArgs e)
    {
        if (AutoRefreshCheckBox.IsChecked == true)
        {
            _autoRefreshTimer.Start();
        }
        else
        {
            _autoRefreshTimer.Stop();
        }
    }

    private async Task RefreshDevicesAsync(string? forceSelectSerial = null)
    {
        if (!_adbService.IsAvailable)
        {
            SetStatus($"ADB not found at {_adbService.AdbPath}");
            return;
        }

        await RunWithStatusAsync("Refreshing devices...", async token =>
        {
            var currentSerial = forceSelectSerial ?? _state.SelectedDevice?.Serial;
            var devices = await _adbService.GetDevicesAsync(token);
            
            _suppressDeviceSelectionRefresh = true;
            _state.Devices.Clear();
            foreach (var device in devices)
            {
                _state.Devices.Add(device);
            }

            try
            {
                var deviceToSelect = currentSerial == null ? null : _state.Devices.FirstOrDefault(d => d.Serial.StartsWith(currentSerial));
                
                DeviceComboBox.SelectedItem = deviceToSelect;
                _state.SelectedDevice = deviceToSelect;
            }
            finally
            {
                _suppressDeviceSelectionRefresh = false;
            }
            UpdateCommandState();
            SetStatus(_state.Devices.Count > 0 ? $"Found {_state.Devices.Count} device(s)." : "No devices found. Check USB debugging and authorization.");
        });

        if (_state.SelectedDevice is not null)
        {
            await RefreshFilesAsync();
            await LoadDeviceSummaryAsync();
        }
        else
        {
            ClearDeviceData();
        }
    }

    private async Task LoadDeviceSummaryAsync()
    {
        if (_state.SelectedDevice?.State != "device") return;
        await RefreshPackagesAsync();
        await RefreshBatteryAsync();
    }

    private Task RefreshFilesAsync()
    {
        if (_state.SelectedDevice?.State != "device") return Task.CompletedTask;
        return RunWithStatusAsync("Loading files...", RefreshFilesCoreAsync);
    }

    private async Task HandleHeartbeatAsync()
    {
        if (!_adbService.IsAvailable || (_workCancellation != null && !_workCancellation.IsCancellationRequested && Cursor == Cursors.Wait))
        {
            return; // Busy or unavailable
        }

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var devices = await _adbService.GetDevicesAsync(cts.Token);
            
            var connectedIps = devices.Select(d => d.Serial.Split(':')[0]).ToHashSet();
            var ipsToConnect = _savedIps.Where(ip => !connectedIps.Contains(ip)).ToList();
            if (ipsToConnect.Count > 0)
            {
                _ = Task.Run(async () =>
                {
                    foreach (var ip in ipsToConnect)
                    {
                        try
                        {
                            using var ping = new System.Net.NetworkInformation.Ping();
                            var reply = await ping.SendPingAsync(ip, 500);
                            if (reply.Status == System.Net.NetworkInformation.IPStatus.Success)
                            {
                                await Dispatcher.InvokeAsync(async () =>
                                {
                                    if (Cursor == Cursors.Wait) return;
                                    try
                                    {
                                        using var c = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                                        await _adbService.ConnectAsync(ip, c.Token);
                                        await RefreshDevicesAsync();
                                    }
                                    catch { }
                                });
                            }
                        }
                        catch { }
                    }
                });
            }
            
            var selectedSerial = _state.SelectedDevice?.Serial;
            var isStillConnected = devices.Any(d => d.Serial == selectedSerial);

            if (devices.Count != _state.Devices.Count || !isStillConnected)
            {
                _suppressDeviceSelectionRefresh = true;
                try
                {
                    _state.Devices.Clear();
                    foreach (var device in devices)
                    {
                        _state.Devices.Add(device);
                    }

                    if (isStillConnected && selectedSerial != null)
                    {
                        var deviceToSelect = _state.Devices.FirstOrDefault(d => d.Serial == selectedSerial);
                        DeviceComboBox.SelectedItem = deviceToSelect;
                        _state.SelectedDevice = deviceToSelect;
                    }
                    else if (selectedSerial != null)
                    {
                        ClearDeviceData();
                    }
                }
                finally
                {
                    _suppressDeviceSelectionRefresh = false;
                }
            }
        }
        catch
        {
            // Ignore heartbeat failures
        }
    }

    private void ClearDeviceData()
    {
        DeviceComboBox.SelectedIndex = -1;
        _state.SelectedDevice = null;
        
        _state.Files.Clear();
        _state.Packages.Clear();
        _allPackages.Clear();
        _state.Processes.Clear();
        
        CpuGauge.Value = 0;
        MemoryGauge.Value = 0;
        StorageGauge.Value = 0;
        BatteryGauge.Value = 0;
        

        LoadAverageTextBlock.Text = "-";
        MemoryDetailTextBlock.Text = "-";
        StorageDetailTextBlock.Text = "-";
        BatteryStatusTextBlock.Text = string.Empty;
        BatteryHealthTextBlock.Text = string.Empty;
        BatteryTemperatureTextBlock.Text = string.Empty;
        BatteryVoltageTextBlock.Text = string.Empty;
        BatteryTechTextBlock.Text = string.Empty;
        BatteryCapturedTextBlock.Text = string.Empty;
        PerformanceUpdatedTextBlock.Text = "Waiting for device...";
        
        UpdateCommandState();
        SetStatus("Device disconnected.");
        StopScrcpy();
    }

    private async Task RefreshFilesCoreAsync(CancellationToken cancellationToken)
    {
        if (!TryGetSelectedSerial(out var serial))
        {
            return;
        }

        var path = NormalizeRemotePath(RemotePathTextBox.Text);
        RemotePathTextBox.Text = path;
        var files = await _adbService.ListFilesAsync(serial, path, cancellationToken);
        _state.Files.Clear();
        foreach (var file in files)
        {
            _state.Files.Add(file);
        }

        SetStatus($"Loaded {_state.Files.Count} item(s) from {path}.");
    }

    private Task RefreshPackagesAsync()
    {
        return RunWithStatusAsync("Loading packages...", RefreshPackagesCoreAsync);
    }

    private async Task RefreshPackagesCoreAsync(CancellationToken cancellationToken)
    {
        if (!TryGetSelectedSerial(out var serial))
        {
            return;
        }

        var packages = await _adbService.ListPackagesAsync(serial, cancellationToken);
        _allPackages.Clear();
        _allPackages.AddRange(packages);
        ApplyPackageFilter();
        SetStatus($"Loaded {_allPackages.Count} package(s).");
    }

    private async Task RefreshBatteryAsync()
    {
        if (!TryGetSelectedSerial(out var serial))
        {
            return;
        }

        await RunWithStatusAsync("Reading battery...", async token =>
        {
            var battery = await _adbService.GetBatteryInfoAsync(serial, token);
            BatteryGauge.Value = battery.Level ?? 0;

            BatteryStatusTextBlock.Text = $"Status: {battery.Status}";
            BatteryHealthTextBlock.Text = $"Health: {battery.Health}";
            BatteryTemperatureTextBlock.Text = $"Temp: {battery.Temperature}";
            BatteryVoltageTextBlock.Text = $"Voltage: {battery.Voltage}";
            BatteryTechTextBlock.Text = $"Technology: {battery.Technology}";
            BatteryCapturedTextBlock.Text = $"Updated {battery.CapturedAt:HH:mm:ss}";
            SetStatus("Battery data refreshed.");
        });
    }

    private async Task RefreshPerformanceAsync()
    {
        if (!TryGetSelectedSerial(out var serial))
        {
            return;
        }

        if (_performanceRefreshInFlight)
        {
            return;
        }

        _performanceRefreshInFlight = true;
        SetStatus("Reading realtime performance...");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        try
        {
            var snapshot = await _adbService.GetPerformanceAsync(serial, cts.Token);
            CpuGauge.Value = snapshot.CpuPercent;
            MemoryGauge.Value = snapshot.MemoryPercent;
            StorageGauge.Value = snapshot.StoragePercent;
            MemoryGauge.Detail = snapshot.MemoryDetail;
            StorageGauge.Detail = snapshot.StorageDetail;
            LoadAverageTextBlock.Text = snapshot.LoadAverage;
            MemoryDetailTextBlock.Text = snapshot.MemoryDetail;
            StorageDetailTextBlock.Text = snapshot.StorageDetail;
            PerformanceUpdatedTextBlock.Text = $"Updated {snapshot.CapturedAt:HH:mm:ss}";

            _state.Processes.Clear();
            foreach (var process in snapshot.Processes)
            {
                _state.Processes.Add(process);
            }

            SetStatus("Performance data refreshed.");
        }
        catch (OperationCanceledException)
        {
            SetStatus("Performance refresh timed out.");
        }
        catch (Exception ex)
        {
            SetStatus($"Perf Error: {ex.Message}");
            
            if (AutoRefreshCheckBox.IsChecked != true)
            {
                await ShowInfoAsync("Performance refresh failed", ex.Message);
            }
        }
        finally
        {
            _performanceRefreshInFlight = false;
        }
    }

    private async Task RunWithStatusAsync(string status, Func<CancellationToken, Task> work)
    {
        SetStatus(status);
        SetBusy(true);
        var token = CreateOperationToken();

        try
        {
            await work(token);
        }
        catch (OperationCanceledException)
        {
            SetStatus("Operation canceled.");
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message);
            if (!ex.Message.Contains("not found") && !ex.Message.Contains("offline"))
            {
                await ShowInfoAsync("ADB operation failed", ex.Message);
            }
        }
        finally
        {
            SetBusy(false);
        }
    }

    private CancellationToken CreateOperationToken()
    {
        _workCancellation?.Cancel();
        _workCancellation?.Dispose();
        _workCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        return _workCancellation.Token;
    }

    private bool TryGetSelectedSerial(out string serial)
    {
        if (_state.SelectedDevice is null)
        {
            serial = string.Empty;
            SetStatus("Select a connected device first.");
            return false;
        }

        if (_state.SelectedDevice.State != "device")
        {
            serial = string.Empty;
            SetStatus($"Device is not fully connected ({_state.SelectedDevice.State}).");
            return false;
        }

        serial = _state.SelectedDevice.Serial;
        return true;
    }

    private void ApplyPackageFilter()
    {
        var filter = PackageFilterTextBox.Text.Trim();
        _state.Packages.Clear();
        foreach (var packageName in _allPackages.Where(packageName => packageName.Contains(filter, StringComparison.OrdinalIgnoreCase)))
        {
            _state.Packages.Add(packageName);
        }
    }

    private static string NormalizeRemotePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "/";
        }

        return path.StartsWith('/') ? path : $"/{path}";
    }

    private static string GetParentPath(string path)
    {
        path = NormalizeRemotePath(path).TrimEnd('/');
        if (path == string.Empty)
        {
            return "/";
        }

        var index = path.LastIndexOf('/');
        return index <= 0 ? "/" : path[..index];
    }

    private async Task DownloadSelectedFileToDefaultFolderAsync()
    {
        if (!TryGetSelectedSerial(out var serial) || FilesListView.SelectedItem is not FileEntry entry)
        {
            SetStatus("Select a device and file first.");
            return;
        }

        var downloadDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "AndroidManagerSuite");
        Directory.CreateDirectory(downloadDirectory);

        await RunWithStatusAsync($"Downloading {entry.Name}...", async token =>
        {
            await _adbService.PullFileAsync(serial, entry.Path, downloadDirectory, token);
            _lastDownloadDirectory = downloadDirectory;
            ShowToast("Download complete", $"{entry.Name} saved to {downloadDirectory}");
            SetStatus($"Downloaded {entry.Name} to {downloadDirectory}.");
        });
    }

    private void ShowToast(string title, string message, bool showOpenButton = true)
    {
        ToastTitleTextBlock.Text = title;
        ToastMessageTextBlock.Text = message;
        ToastOpenButton.Visibility = showOpenButton ? Visibility.Visible : Visibility.Collapsed;
        ToastBorder.Visibility = Visibility.Visible;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    private async Task PreviewImageAsync(FileEntry entry)
    {
        if (!TryGetSelectedSerial(out var serial))
        {
            return;
        }

        var previewDirectory = Path.Combine(Path.GetTempPath(), "AndroidManagerSuite", "Preview");
        Directory.CreateDirectory(previewDirectory);
        var localPath = Path.Combine(previewDirectory, entry.Name);

        await RunWithStatusAsync($"Loading preview for {entry.Name}...", async token =>
        {
            await _adbService.PullFileAsync(serial, entry.Path, previewDirectory, token);
            await Dispatcher.InvokeAsync(() => ShowImagePreview(entry, localPath));
            SetStatus($"Previewing {entry.Name}.");
        });
    }

    private void ShowImagePreview(FileEntry entry, string localPath)
    {
        if (!File.Exists(localPath))
        {
            SetStatus("Preview file was not created.");
            return;
        }

        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.UriSource = new Uri(localPath, UriKind.Absolute);
        image.EndInit();
        image.Freeze();

        ImagePreviewTitleTextBlock.Text = entry.Name;
        ImagePreviewPathTextBlock.Text = entry.Path;
        ImagePreviewControl.Source = image;
        SetImagePreviewFitMode();
        ImagePreviewOverlay.Visibility = Visibility.Visible;
    }

    private static bool IsPreviewableImage(FileEntry entry)
    {
        return PreviewImageExtensions.Contains(Path.GetExtension(entry.Name));
    }

    private void ImagePreviewCloseButton_OnClick(object sender, RoutedEventArgs e)
    {
        ImagePreviewControl.Source = null;
        ImagePreviewOverlay.Visibility = Visibility.Collapsed;
    }

    private void ImagePreviewActualSizeButton_OnClick(object sender, RoutedEventArgs e)
    {
        ImagePreviewControl.Stretch = Stretch.None;
        ImagePreviewControl.HorizontalAlignment = HorizontalAlignment.Center;
        ImagePreviewControl.VerticalAlignment = VerticalAlignment.Center;
        ImagePreviewScrollViewer.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        ImagePreviewScrollViewer.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
    }

    private void ImagePreviewFitButton_OnClick(object sender, RoutedEventArgs e)
    {
        SetImagePreviewFitMode();
    }

    private void SetImagePreviewFitMode()
    {
        ImagePreviewControl.Stretch = Stretch.Uniform;
        ImagePreviewControl.HorizontalAlignment = HorizontalAlignment.Stretch;
        ImagePreviewControl.VerticalAlignment = VerticalAlignment.Stretch;
        ImagePreviewScrollViewer.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        ImagePreviewScrollViewer.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
    }

    private void StartMirrorButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (!TryGetSelectedSerial(out var serial))
        {
            return;
        }

        if (!File.Exists(ScrcpyPath))
        {
            _ = ShowInfoAsync("scrcpy not found", $"scrcpy.exe was not found at {ScrcpyPath}");
            return;
        }

        StopScrcpy();
        _scrcpyProcess = Process.Start(new ProcessStartInfo
        {
            FileName = ScrcpyPath,
            Arguments = $"-s {serial} --window-title=\"Screen Mirror\"",
            WorkingDirectory = Path.GetDirectoryName(ScrcpyPath) ?? string.Empty,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        });

        if (_scrcpyProcess != null)
        {
            _autoRefreshTimer.Stop(); // Pause heavy polling while mirroring
            _scrcpyProcess.EnableRaisingEvents = true;
            _scrcpyProcess.OutputDataReceived += (s, ev) => { }; // Discard to prevent buffer full
            _scrcpyProcess.ErrorDataReceived += (s, ev) => { };  // Discard to prevent buffer full
            
            _scrcpyProcess.BeginOutputReadLine();
            _scrcpyProcess.BeginErrorReadLine();
            _scrcpyProcess.Exited += (s, ev) =>
            {
                Dispatcher.BeginInvoke(() =>
                {
                    if (_scrcpyProcess != null)
                    {
                        _scrcpyProcess.Dispose();
                        _scrcpyProcess = null;
                        UpdateCommandState();
                        SetStatus("Screen mirror closed.");
                    }
                });
            };
        }

        SetStatus("Screen mirror is running in a separate window.");
        UpdateCommandState();
    }

    private void StopMirrorButton_OnClick(object sender, RoutedEventArgs e)
    {
        StopScrcpy();
        SetStatus("Screen mirror stopped.");
    }

    private void ToastOpenButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_lastDownloadDirectory) || !Directory.Exists(_lastDownloadDirectory))
        {
            SetStatus("Download folder is not available yet.");
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"\"{_lastDownloadDirectory}\"",
            UseShellExecute = true
        });
    }

    private void UpdateCommandState()
    {
        InstallApkButton.IsEnabled = File.Exists(ApkPathTextBox.Text)
            && string.Equals(Path.GetExtension(ApkPathTextBox.Text), ".apk", StringComparison.OrdinalIgnoreCase)
            && _state.SelectedDevice is not null;
            
        StartMirrorButton.IsEnabled = _state.SelectedDevice is not null && _scrcpyProcess == null;
        StopMirrorButton.IsEnabled = _scrcpyProcess != null;
        
        RebootButton.IsEnabled = _state.SelectedDevice is not null;
        if (_state.SelectedDevice is null)
        {
            DisconnectButton.Visibility = Visibility.Collapsed;
        }
        else if (_state.SelectedDevice.Serial.Contains(':'))
        {
            DisconnectButton.Visibility = Visibility.Visible;
        }
        else
        {
            DisconnectButton.Visibility = Visibility.Collapsed;
        }
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match)
            {
                return match;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private void SetBusy(bool isBusy)
    {
        RefreshDevicesButton.IsEnabled = !isBusy;
        Cursor = isBusy ? Cursors.Wait : Cursors.Arrow;
    }

    private void SetStatus(string message)
    {
        StatusTextBlock.Text = message;
    }

    private void MainWindow_OnClosing(object? sender, CancelEventArgs e)
    {
        _heartbeatTimer.Stop();
        _autoRefreshTimer.Stop();
        _toastTimer.Stop();
        StopScrcpy();
        _workCancellation?.Cancel();
        _workCancellation?.Dispose();
    }

    private void StopScrcpy()
    {
        try
        {
            _scrcpyProcess?.Kill();
        }
        catch { }
        finally
        {
            if (_scrcpyProcess != null)
            {
                try { _scrcpyProcess.EnableRaisingEvents = false; } catch { }
                _scrcpyProcess.Dispose();
                _scrcpyProcess = null;
            }
            
            if (AutoRefreshCheckBox.IsChecked == true)
            {
                _autoRefreshTimer.Start(); // Resume polling
            }
            
            UpdateCommandState();
        }
    }



    private async void DisconnectButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (!TryGetSelectedSerial(out var serial)) return;

        await RunWithStatusAsync($"Disconnecting from {serial}...", async token =>
        {
            await _adbService.DisconnectAsync(serial, token);
            
            var ip = serial.Split(':')[0];
            _savedIps.Remove(ip);
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var configPath = Path.Combine(appData, "AndroidManagerSuite", "saved_ips.json");
            if (File.Exists(configPath))
            {
                await File.WriteAllTextAsync(configPath, System.Text.Json.JsonSerializer.Serialize(_savedIps), token);
            }

            await RefreshDevicesAsync();
            ShowToast("Disconnected", $"Disconnected from {serial}", showOpenButton: false);
        });
    }

    private void RebootButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.ContextMenu != null)
        {
            btn.ContextMenu.PlacementTarget = btn;
            btn.ContextMenu.IsOpen = true;
        }
    }

    private async void RebootNormalMenuItem_OnClick(object sender, RoutedEventArgs e) => await ExecuteRebootAsync("");
    private async void RebootRecoveryMenuItem_OnClick(object sender, RoutedEventArgs e) => await ExecuteRebootAsync("recovery");
    private async void RebootBootloaderMenuItem_OnClick(object sender, RoutedEventArgs e) => await ExecuteRebootAsync("bootloader");

    private async Task ExecuteRebootAsync(string mode)
    {
        if (!TryGetSelectedSerial(out var serial)) return;
        
        await RunWithStatusAsync($"Rebooting device...", async token =>
        {
            await _adbService.RebootAsync(serial, mode, token);
            ShowToast("Rebooting", $"The device is rebooting{(string.IsNullOrEmpty(mode) ? "" : " to " + mode)}.", showOpenButton: false);
            await Task.Delay(2000, token);
            await RefreshDevicesAsync();
        });
    }

    private static async Task<IntPtr> WaitForWindowAsync(string title, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.Now + timeout;
        while (DateTimeOffset.Now < deadline)
        {
            var handle = FindWindowByTitle(title);
            if (handle != IntPtr.Zero)
            {
                return handle;
            }

            await Task.Delay(200);
        }

        return IntPtr.Zero;
    }

    private static IntPtr FindWindowByTitle(string title)
    {
        var result = IntPtr.Zero;
        EnumWindows((handle, _) =>
        {
            var builder = new StringBuilder(512);
            GetWindowText(handle, builder, builder.Capacity);
            if (string.Equals(builder.ToString(), title, StringComparison.Ordinal))
            {
                result = handle;
                return false;
            }

            return true;
        }, IntPtr.Zero);

        return result;
    }

    private Task ShowInfoAsync(string title, string message)
    {
        return ShowDialogAsync(title, message, showCancel: false);
    }

    private async Task<bool> ShowConfirmAsync(string title, string message)
    {
        return await ShowDialogAsync(title, message, showCancel: true);
    }

    private Task<bool> ShowDialogAsync(string title, string message, bool showCancel)
    {
        _dialogCompletion?.TrySetResult(false);
        _inputCompletion?.TrySetResult(null);
        _dialogCompletion = new TaskCompletionSource<bool>();
        DialogTitleTextBlock.Text = title;
        DialogMessageTextBlock.Text = message;
        DialogInputTextBox.Visibility = Visibility.Collapsed;
        DialogCancelButton.Visibility = showCancel ? Visibility.Visible : Visibility.Collapsed;
        DialogOkButton.Content = showCancel ? "Continue" : "OK";
        DialogOverlay.Visibility = Visibility.Visible;
        return _dialogCompletion.Task;
    }

    private TaskCompletionSource<string?>? _inputCompletion;

    private Task<string?> PromptForInputAsync(string title, string message)
    {
        _dialogCompletion?.TrySetResult(false);
        _inputCompletion?.TrySetResult(null);
        _inputCompletion = new TaskCompletionSource<string?>();
        
        DialogTitleTextBlock.Text = title;
        DialogMessageTextBlock.Text = message;
        DialogInputTextBox.Text = "";
        DialogInputTextBox.Visibility = Visibility.Visible;
        DialogCancelButton.Visibility = Visibility.Visible;
        DialogOkButton.Content = "Connect";
        DialogOverlay.Visibility = Visibility.Visible;
        
        DialogInputTextBox.Focus();
        return _inputCompletion.Task;
    }

    private void DialogOkButton_OnClick(object sender, RoutedEventArgs e)
    {
        DialogOverlay.Visibility = Visibility.Collapsed;
        DialogInputTextBox.Visibility = Visibility.Collapsed;
        _dialogCompletion?.TrySetResult(true);
        _inputCompletion?.TrySetResult(DialogInputTextBox.Text);
    }

    private void DialogCancelButton_OnClick(object sender, RoutedEventArgs e)
    {
        DialogOverlay.Visibility = Visibility.Collapsed;
        DialogInputTextBox.Visibility = Visibility.Collapsed;
        _dialogCompletion?.TrySetResult(false);
        _inputCompletion?.TrySetResult(null);
    }

    private readonly string _savedIpsFilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AndroidManagerSuite", "saved_ips.json");
    private HashSet<string> _savedIps = new();

    private void LoadSavedIps()
    {
        try
        {
            if (File.Exists(_savedIpsFilePath))
            {
                var json = File.ReadAllText(_savedIpsFilePath);
                var ips = System.Text.Json.JsonSerializer.Deserialize<string[]>(json);
                if (ips != null) _savedIps = new HashSet<string>(ips);
            }
        }
        catch { }
    }

    private void SaveIpAddress(string ip)
    {
        if (string.IsNullOrWhiteSpace(ip)) return;
        var cleanIp = ip.Contains(':') ? ip.Split(':')[0] : ip;
        
        if (_savedIps.Add(cleanIp))
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_savedIpsFilePath)!);
                File.WriteAllText(_savedIpsFilePath, System.Text.Json.JsonSerializer.Serialize(_savedIps.ToArray()));
            }
            catch { }
        }
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc enumProc, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    private void ExitMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        Application.Current.Shutdown();
    }

    private async void AboutMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        await ShowInfoAsync("About Android Manager Suite", "Android Manager Suite v1.0\n\nA powerful desktop utility to manage Android devices over ADB. Features include file management, package uninstallation, live performance monitoring, and wireless connections.");
    }
}
