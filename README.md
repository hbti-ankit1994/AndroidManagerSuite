# Android Manager Suite

A powerful, standalone Windows desktop utility to manage Android devices over ADB. This tool features a sleek modern WPF interface and bundles all required Android Debug Bridge (ADB) and Scrcpy binaries, so you don't need to install any external Android SDKs.

## Features

- **Modern Dark UI**: A clean, responsive dashboard designed for ease of use.
- **Wireless Debugging**: Connect to devices wirelessly via IP or automatically discover mDNS wireless connections.
- **File Explorer**: Browse, push, pull, delete, and preview image files right from your device.
- **Live Performance Dashboard**: Monitor real-time CPU, memory, and top processes using `dumpsys` and `top`.
- **Package Management**: Install APKs from your local machine and view/uninstall existing packages.
- **Screen Mirroring**: Built-in integration with `scrcpy` for high-performance, low-latency device screen mirroring.
- **Battery Monitoring**: View precise battery levels, temperature, voltage, and health status.

## Getting Started

Because the SDK binaries (`platform-tools` and `scrcpy`) are bundled inside the project, you can simply clone and run the application!

### Prerequisites
- [.NET 8.0 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- Windows OS (WPF application)

### Run

```powershell
dotnet run --project AndroidManagerSuite.App\AndroidManagerSuite.App.csproj
```

### Usage
1. Connect your Android device via USB with **USB Debugging** enabled.
2. Accept the authorization prompt on the device screen.
3. The app will automatically detect and connect to your device.
4. From the top-right corner, you can switch to Wi-Fi, mirror your screen, or reboot the device.

## Bundled Tools
- **ADB**: Used for all low-level device communication.
- **Scrcpy**: Used for the screen mirror functionality.

All tools are located in the `AndroidManagerSuite.App/SDK/` directory and are automatically copied to the output folder during the build process.
