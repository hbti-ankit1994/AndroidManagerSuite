using System.Collections.ObjectModel;

namespace AndroidManagerSuite.App;

public sealed record AdbDevice(string Serial, string State, string DisplayName);

public sealed record FileEntry(string Name, string Path, bool IsDirectory, string Size, string Modified)
{
    public string Type => IsDirectory ? "Folder" : "File";
}

public sealed record BatteryInfo(
    int? Level,
    string Status,
    string Health,
    string Temperature,
    string Voltage,
    string Technology,
    DateTimeOffset CapturedAt);

public sealed record PerformanceSnapshot(
    double CpuPercent,
    double MemoryPercent,
    double StoragePercent,
    string LoadAverage,
    string MemoryDetail,
    string StorageDetail,
    IReadOnlyList<ProcessMetric> Processes,
    DateTimeOffset CapturedAt);

public sealed record ProcessMetric(string Pid, string Name, string Cpu, string Memory);

public sealed class MainWindowState
{
    public ObservableCollection<AdbDevice> Devices { get; } = [];
    public ObservableCollection<FileEntry> Files { get; } = [];
    public ObservableCollection<string> Packages { get; } = [];
    public ObservableCollection<ProcessMetric> Processes { get; } = [];

    public AdbDevice? SelectedDevice { get; set; }
}
