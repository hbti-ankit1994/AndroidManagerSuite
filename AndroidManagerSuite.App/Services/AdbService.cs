using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace AndroidManagerSuite.App.Services;

public sealed class AdbService
{
    private static readonly Regex FileLineRegex = new(
        @"^(?<perm>[bcdlps-][rwxstST-]{9})\s+\S+\s+(?<owner>\S+)\s+(?<group>\S+)\s+(?<size>\d+)\s+(?<date>\d{4}-\d{2}-\d{2})\s+(?<time>\d{2}:\d{2})\s+(?<name>.+)$",
        RegexOptions.Compiled);

    public AdbService(string platformToolsPath)
    {
        PlatformToolsPath = platformToolsPath;
        AdbPath = Path.Combine(platformToolsPath, "adb.exe");
    }

    public string PlatformToolsPath { get; }
    public string AdbPath { get; }
    public bool IsAvailable => File.Exists(AdbPath);

    public async Task<IReadOnlyList<AdbDevice>> GetDevicesAsync(CancellationToken cancellationToken)
    {
        var result = await RunAsync("devices -l", cancellationToken: cancellationToken);
        return result.StandardOutput.SplitLines().Skip(1).Select(ParseDevice).Where(device => device is not null).Cast<AdbDevice>().ToList();
    }

    public async Task<IReadOnlyList<FileEntry>> ListFilesAsync(string serial, string path, CancellationToken cancellationToken)
    {
        var listPath = NormalizeListPath(path);
        var result = await RunAsync($"-s {Quote(serial)} shell ls -la {EscapeShellArgument(listPath)}", cancellationToken: cancellationToken);
        return result.StandardOutput.SplitLines()
            .Select(line => ParseFileLine(line, path))
            .Where(entry => entry is not null)
            .Cast<FileEntry>()
            .OrderByDescending(entry => entry.IsDirectory)
            .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public Task PushFileAsync(string serial, string localPath, string remoteDirectory, CancellationToken cancellationToken)
    {
        var remotePath = CombineRemotePath(remoteDirectory, Path.GetFileName(localPath));
        return RunEnsuredAsync($"-s {Quote(serial)} push {Quote(localPath)} {Quote(remotePath)}", cancellationToken);
    }

    public Task PullFileAsync(string serial, string remotePath, string localDirectory, CancellationToken cancellationToken)
    {
        return RunEnsuredAsync($"-s {Quote(serial)} pull {Quote(remotePath)} {Quote(localDirectory)}", cancellationToken);
    }

    public Task DeleteFileAsync(string serial, string remotePath, bool isDirectory, CancellationToken cancellationToken)
    {
        var command = isDirectory ? "rm -rf" : "rm -f";
        return RunEnsuredAsync($"-s {Quote(serial)} shell {command} {EscapeShellArgument(remotePath)}", cancellationToken);
    }

    public Task InstallApkAsync(string serial, string apkPath, CancellationToken cancellationToken)
    {
        return RunEnsuredAsync($"-s {Quote(serial)} install -r {Quote(apkPath)}", cancellationToken);
    }

    public Task UninstallPackageAsync(string serial, string packageName, CancellationToken cancellationToken)
    {
        return RunEnsuredAsync($"-s {Quote(serial)} uninstall {Quote(packageName)}", cancellationToken);
    }

    public async Task<IReadOnlyList<string>> ListPackagesAsync(string serial, CancellationToken cancellationToken)
    {
        var result = await RunAsync($"-s {Quote(serial)} shell pm list packages", cancellationToken: cancellationToken);
        return result.StandardOutput.SplitLines()
            .Select(line => line.StartsWith("package:", StringComparison.OrdinalIgnoreCase) ? line[8..] : line)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .OrderBy(line => line, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<BatteryInfo> GetBatteryInfoAsync(string serial, CancellationToken cancellationToken)
    {
        var result = await RunAsync($"-s {Quote(serial)} shell dumpsys battery", cancellationToken: cancellationToken);
        var values = result.StandardOutput.SplitLines()
            .Select(line => line.Split(':', 2))
            .Where(parts => parts.Length == 2)
            .ToDictionary(parts => parts[0].Trim(), parts => parts[1].Trim(), StringComparer.OrdinalIgnoreCase);

        values.TryGetValue("level", out var levelText);

        return new BatteryInfo(
            int.TryParse(levelText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var level) ? level : null,
            GetMappedBatteryValue(values, "status"),
            GetMappedBatteryValue(values, "health"),
            values.TryGetValue("temperature", out var temperature) && int.TryParse(temperature, out var temp) ? $"{temp / 10.0:F1} C" : "Unknown",
            values.TryGetValue("voltage", out var voltage) ? $"{voltage} mV" : "Unknown",
            values.TryGetValue("technology", out var technology) ? technology : "Unknown",
            DateTimeOffset.Now);
    }

    public async Task<(int Width, int Height)> GetDeviceResolutionAsync(string serial, CancellationToken cancellationToken)
    {
        var result = await RunAsync($"-s {Quote(serial)} shell wm size", cancellationToken: cancellationToken, throwOnError: false);
        var text = result.StandardOutput;
        var match = Regex.Match(text, @"Physical size:\s*(?<width>\d+)x(?<height>\d+)");
        if (match.Success && int.TryParse(match.Groups["width"].Value, out var w) && int.TryParse(match.Groups["height"].Value, out var h))
        {
            return (w, h);
        }
        return (9, 19); // Fallback portrait ratio
    }

    public async Task SwitchToTcpIpAsync(string serial, CancellationToken cancellationToken)
    {
        await RunAsync($"-s {Quote(serial)} tcpip 5555", cancellationToken: cancellationToken, throwOnError: false);
    }

    public async Task ConnectAsync(string ipAddress, CancellationToken cancellationToken)
    {
        var result = await RunAsync($"connect {Quote(ipAddress)}:5555", cancellationToken: cancellationToken);
        if (result.StandardOutput.Contains("failed") || result.StandardOutput.Contains("cannot"))
        {
            throw new Exception(result.StandardOutput.Trim());
        }
    }

    public async Task DisconnectAsync(string serial, CancellationToken cancellationToken)
    {
        await RunAsync($"disconnect {Quote(serial)}", cancellationToken: cancellationToken, throwOnError: false);
    }

    public async Task RebootAsync(string serial, string mode, CancellationToken cancellationToken)
    {
        var arg = string.IsNullOrWhiteSpace(mode) ? "reboot" : $"reboot {mode}";
        await RunEnsuredAsync($"-s {Quote(serial)} {arg}", cancellationToken);
    }

    public async Task<string?> GetDeviceIpAddressAsync(string serial, CancellationToken cancellationToken)
    {
        var result = await RunAsync($"-s {Quote(serial)} shell ip route", cancellationToken: cancellationToken, throwOnError: false);
        
        // Find the wlan0 IP address (typically default or a src attribute on wlan0)
        // e.g., "192.168.1.0/24 dev wlan0 proto kernel scope link src 192.168.1.100"
        var match = Regex.Match(result.StandardOutput, @"wlan0.*src\s+(?<ip>\d+\.\d+\.\d+\.\d+)");
        if (match.Success)
        {
            return match.Groups["ip"].Value;
        }

        // Fallback: ip addr show wlan0
        var fallback = await RunAsync($"-s {Quote(serial)} shell ip addr show wlan0 | grep 'inet '", cancellationToken: cancellationToken, throwOnError: false);
        var fbMatch = Regex.Match(fallback.StandardOutput, @"inet\s+(?<ip>\d+\.\d+\.\d+\.\d+)");
        if (fbMatch.Success)
        {
            return fbMatch.Groups["ip"].Value;
        }

        return null;
    }

    public async Task<PerformanceSnapshot> GetPerformanceAsync(string serial, CancellationToken cancellationToken)
    {
        const string command = "cat /proc/stat; echo __AndroidManagerSuite_MEM__; cat /proc/meminfo; echo __AndroidManagerSuite_LOAD__; cat /proc/loadavg; echo __AndroidManagerSuite_DF__; df /data; echo __AndroidManagerSuite_TOP__; top -b -n 1 -m 8";
        var result = await RunAsync($"-s {Quote(serial)} shell {command}", cancellationToken: cancellationToken, throwOnError: false);
        var text = string.IsNullOrWhiteSpace(result.StandardOutput) ? result.StandardError : result.StandardOutput;

        var statText = SectionBefore(text, "__AndroidManagerSuite_MEM__");
        var memText = SectionBetween(text, "__AndroidManagerSuite_MEM__", "__AndroidManagerSuite_LOAD__");
        var loadText = SectionBetween(text, "__AndroidManagerSuite_LOAD__", "__AndroidManagerSuite_DF__");
        var dfText = SectionBetween(text, "__AndroidManagerSuite_DF__", "__AndroidManagerSuite_TOP__");
        var topText = SectionAfter(text, "__AndroidManagerSuite_TOP__");

        var memory = ParseMemory(memText);
        var storage = ParseStorage(dfText);

        return new PerformanceSnapshot(
            ParseCpuUsage(statText),
            memory.Percent,
            storage.Percent,
            ParseLoad(loadText),
            memory.Detail,
            storage.Detail,
            ParseTopProcesses(topText),
            DateTimeOffset.Now);
    }

    private async Task RunEnsuredAsync(string arguments, CancellationToken cancellationToken)
    {
        await RunAsync(arguments, cancellationToken: cancellationToken);
    }

    private async Task<AdbResult> RunAsync(string arguments, CancellationToken cancellationToken, bool throwOnError = true)
    {
        if (!IsAvailable)
        {
            throw new FileNotFoundException("ADB executable was not found.", AdbPath);
        }

        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = AdbPath,
            Arguments = arguments,
            WorkingDirectory = PlatformToolsPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        process.Start();
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        var result = new AdbResult(await outputTask, await errorTask, process.ExitCode);
        if (throwOnError && result.ExitCode != 0)
        {
            throw new InvalidOperationException(result.StandardError.Trim().Length > 0 ? result.StandardError.Trim() : result.StandardOutput.Trim());
        }

        return result;
    }

    private static AdbDevice? ParseDevice(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return null;
        }

        var serial = parts[0];
        var state = parts[1];
        if (state == "offline")
        {
            return null;
        }
        var model = parts.FirstOrDefault(part => part.StartsWith("model:", StringComparison.OrdinalIgnoreCase))?.Split(':', 2)[1];
        var product = parts.FirstOrDefault(part => part.StartsWith("product:", StringComparison.OrdinalIgnoreCase))?.Split(':', 2)[1];
        var display = string.Join(" ", new[] { model, product }.Where(value => !string.IsNullOrWhiteSpace(value)));

        return new AdbDevice(serial, state, string.IsNullOrWhiteSpace(display) ? serial : $"{display} ({serial})");
    }

    private static FileEntry? ParseFileLine(string line, string parentPath)
    {
        var match = FileLineRegex.Match(line);
        if (!match.Success)
        {
            return null;
        }

        var name = match.Groups["name"].Value;
        if (name is "." or "..")
        {
            return null;
        }

        if (name.Contains(" -> ", StringComparison.Ordinal))
        {
            name = name.Split(" -> ", 2, StringSplitOptions.TrimEntries)[0];
        }

        var isDirectory = match.Groups["perm"].Value[0] is 'd' or 'l';
        var size = isDirectory ? string.Empty : FormatBytes(long.Parse(match.Groups["size"].Value, CultureInfo.InvariantCulture));
        var modified = $"{match.Groups["date"].Value} {match.Groups["time"].Value}";

        return new FileEntry(name, CombineRemotePath(parentPath, name), isDirectory, size, modified);
    }

    private static string CombineRemotePath(string directory, string child)
    {
        var cleanDirectory = string.IsNullOrWhiteSpace(directory) ? "/" : directory.TrimEnd('/');
        return cleanDirectory == "/" ? $"/{child}" : $"{cleanDirectory}/{child}";
    }

    private static string NormalizeListPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path == "/")
        {
            return "/";
        }

        return path.EndsWith('/') ? path : $"{path}/";
    }

    private static string FormatBytes(long bytes)
    {
        string[] suffixes = ["B", "KB", "MB", "GB"];
        var value = (double)bytes;
        var index = 0;
        while (value >= 1024 && index < suffixes.Length - 1)
        {
            value /= 1024;
            index++;
        }

        return index == 0 ? $"{bytes} B" : $"{value:F1} {suffixes[index]}";
    }

    private static string EscapeShellArgument(string value)
    {
        return $"'{value.Replace("'", "'\\''")}'";
    }

    private static string Quote(string value)
    {
        return $"\"{value.Replace("\"", "\\\"")}\"";
    }

    private static string GetMappedBatteryValue(IReadOnlyDictionary<string, string> values, string key)
    {
        if (!values.TryGetValue(key, out var value))
        {
            return "Unknown";
        }

        return key.Equals("status", StringComparison.OrdinalIgnoreCase)
            ? value switch
            {
                "1" => "Unknown",
                "2" => "Charging",
                "3" => "Discharging",
                "4" => "Not charging",
                "5" => "Full",
                _ => value
            }
            : value switch
            {
                "1" => "Unknown",
                "2" => "Good",
                "3" => "Overheat",
                "4" => "Dead",
                "5" => "Over voltage",
                "6" => "Unspecified failure",
                "7" => "Cold",
                _ => value
            };
    }

    private static double ParseCpuUsage(string statText)
    {
        var cpuLine = statText.SplitLines().FirstOrDefault(line => line.StartsWith("cpu ", StringComparison.Ordinal));
        if (cpuLine is null)
        {
            return 0;
        }

        var values = cpuLine.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1)
            .Select(value => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0)
            .ToArray();
        if (values.Length < 5)
        {
            return 0;
        }

        var idle = values[3] + values.ElementAtOrDefault(4);
        var total = values.Sum();
        return total <= 0 ? 0 : Math.Clamp((total - idle) * 100 / total, 0, 100);
    }

    private static (double Percent, string Detail) ParseMemory(string memText)
    {
        var values = memText.SplitLines()
            .Select(line => line.Split(':', 2))
            .Where(parts => parts.Length == 2)
            .ToDictionary(parts => parts[0].Trim(), parts => ParseKilobytes(parts[1]), StringComparer.OrdinalIgnoreCase);

        values.TryGetValue("MemTotal", out var total);
        values.TryGetValue("MemAvailable", out var available);
        if (available <= 0 && values.TryGetValue("MemFree", out var free))
        {
            available = free;
        }

        var used = Math.Max(0, total - available);
        var percent = total <= 0 ? 0 : used * 100.0 / total;
        return (Math.Clamp(percent, 0, 100), $"{FormatBytes((long)used * 1024)} / {FormatBytes((long)total * 1024)}");
    }

    private static (double Percent, string Detail) ParseStorage(string dfText)
    {
        var line = dfText.SplitLines().LastOrDefault(value => value.Contains("/data", StringComparison.OrdinalIgnoreCase));
        if (line is null)
        {
            return (0, "No /data storage data");
        }

        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var usedPercentText = parts.FirstOrDefault(part => part.EndsWith('%'));
        var percent = double.TryParse(usedPercentText?.TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;

        string detail;
        if (parts.Length >= 4 && long.TryParse(parts[^5], out var size) && long.TryParse(parts[^4], out var used))
        {
            var sizeGb = size / 1024.0 / 1024.0;
            var usedGb = used / 1024.0 / 1024.0;
            detail = $"{usedGb:0.0} GB / {sizeGb:0.0} GB";
        }
        else
        {
            detail = parts.Length >= 4 ? $"{parts[^4]} used of {parts[^5]}" : line;
        }

        return (Math.Clamp(percent, 0, 100), detail);
    }

    private static string ParseLoad(string loadText)
    {
        var firstLine = loadText.SplitLines().FirstOrDefault(line => !string.IsNullOrWhiteSpace(line));
        if (firstLine is null)
        {
            return "Load unavailable";
        }

        var values = firstLine.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(3);
        return string.Join(" / ", values);
    }

    private static IReadOnlyList<ProcessMetric> ParseTopProcesses(string topText)
    {
        var rows = new List<ProcessMetric>();
        int pidIdx = -1, cpuIdx = -1, memIdx = -1, nameIdx = -1;

        foreach (var line in topText.SplitLines())
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith("Tasks:", StringComparison.OrdinalIgnoreCase) || 
                trimmed.StartsWith("Mem:", StringComparison.OrdinalIgnoreCase) || 
                trimmed.StartsWith("Swap:", StringComparison.OrdinalIgnoreCase) ||
                trimmed.EndsWith("%host", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var parts = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            
            if (pidIdx == -1)
            {
                if (parts.Length > 0 && parts[0].Equals("PID", StringComparison.OrdinalIgnoreCase))
                {
                    pidIdx = Array.FindIndex(parts, p => p.Equals("PID", StringComparison.OrdinalIgnoreCase));
                    cpuIdx = Array.FindIndex(parts, p => p.Contains("CPU", StringComparison.OrdinalIgnoreCase));
                    memIdx = Array.FindIndex(parts, p => p.Contains("MEM", StringComparison.OrdinalIgnoreCase));
                    nameIdx = Array.FindIndex(parts, p => p.Equals("ARGS", StringComparison.OrdinalIgnoreCase) || p.Equals("Name", StringComparison.OrdinalIgnoreCase) || p.Equals("CMD", StringComparison.OrdinalIgnoreCase));
                }
                continue;
            }

            if (parts.Length < 2 || !parts[0].All(char.IsDigit))
            {
                continue;
            }

            var cpu = (cpuIdx >= 0 && cpuIdx < parts.Length) ? parts[cpuIdx] : "-";
            var memory = (memIdx >= 0 && memIdx < parts.Length) ? parts[memIdx] : "-";
            
            if (cpu != "-" && !cpu.EndsWith("%") && double.TryParse(cpu, out _)) cpu += "%";
            if (memory != "-" && !memory.EndsWith("%") && double.TryParse(memory, out _)) memory += "%";
            
            var name = (nameIdx >= 0 && nameIdx < parts.Length) ? string.Join(" ", parts.Skip(nameIdx)) : parts[^1];
            rows.Add(new ProcessMetric(parts[0], name, cpu, memory));

            if (rows.Count == 8)
            {
                break;
            }
        }

        return rows;
    }

    private static long ParseKilobytes(string value)
    {
        var number = value.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return long.TryParse(number, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
    }

    private static string SectionBefore(string text, string marker)
    {
        var index = text.IndexOf(marker, StringComparison.Ordinal);
        return index < 0 ? text : text[..index];
    }

    private static string SectionBetween(string text, string start, string end)
    {
        var startIndex = text.IndexOf(start, StringComparison.Ordinal);
        if (startIndex < 0)
        {
            return string.Empty;
        }

        startIndex += start.Length;
        var endIndex = text.IndexOf(end, startIndex, StringComparison.Ordinal);
        return endIndex < 0 ? text[startIndex..] : text[startIndex..endIndex];
    }

    private static string SectionAfter(string text, string marker)
    {
        var index = text.IndexOf(marker, StringComparison.Ordinal);
        return index < 0 ? string.Empty : text[(index + marker.Length)..];
    }

    private sealed record AdbResult(string StandardOutput, string StandardError, int ExitCode);
}

internal static class StringExtensions
{
    public static IEnumerable<string> SplitLines(this string value)
    {
        using var reader = new StringReader(value);
        while (reader.ReadLine() is { } line)
        {
            yield return line.TrimEnd();
        }
    }
}
