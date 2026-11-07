# SteelSeries Headset Battery Tray

A lightweight Windows system-tray app that shows the battery percentage reported by a SteelSeries Arctis 7 wireless headset.

## Download and run

1. Open the [latest release](https://github.com/BasSchurink/SteelSeriesHeadsetBatteryTray/releases/latest).
2. Download `SteelSeriesHeadsetBatteryTray-win-x64.exe`.
3. Run the EXE. It appears in the notification area (near the clock); right-click it for the current status, **Refresh**, **Start with Windows**, and **Exit**.

The published file is a self-contained, single-file Windows x64 application. It does not require .NET, an installer, or a separate icon folder. Windows may show a SmartScreen warning because release executables are not code-signed.

The tray icon displays the current percentage. Hover over it or right-click for the full status. The app checks every five seconds; use **Refresh** to check immediately. With no compatible receiver attached, it displays `?` and reports that the headset was not found rather than showing a misleading 0%.

**Supported hardware:** SteelSeries Arctis 7 (2017, USB product ID `1260`) and Arctis 7 (2019, USB product ID `12AD`). Connect the headset's wireless USB transmitter; this app reads the receiver's HID battery report. Other SteelSeries models are not claimed as supported.

## Run or build from source

Requires the .NET 10 SDK and Windows (the application uses Windows Forms and the Windows registry).

To run it directly from source:

```powershell
dotnet run --project .\src\SteelSeriesHeadsetBatteryTray.csproj
```

To create the standalone EXE:

```powershell
dotnet publish .\src\SteelSeriesHeadsetBatteryTray.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o .\publish
```

Run `.\publish\SteelSeriesHeadsetBatteryTray.exe`. A plain `dotnet build` puts its framework-dependent EXE under `src\bin\Debug\net10.0-windows\` (or `Release` if built in Release mode), not in the repository root. Close the app before rebuilding; Windows locks the EXE while it is running.

## Diagnostics

When a battery read fails, the app records the error and exception details to `%LOCALAPPDATA%\SteelSeriesHeadsetBatteryTray\app.log`. Right-click the tray icon and choose **Open diagnostic log** to view it. Errors are logged only when the error changes or returns after a successful read, not on every polling interval.

## Releases

Push a version tag such as `v1.0.0` to build and attach the standalone executable to a GitHub Release:

```powershell
git tag v1.0.0
git push origin v1.0.0
```

The build workflow is `.github/workflows/release.yml`. It can also be run manually to verify a build; manual runs upload the executable as a workflow artifact rather than publishing a release.

## Reference and license

This project was inspired by [crazyklatsch/Arctis7_BatteryReader](https://github.com/crazyklatsch/Arctis7_BatteryReader) and its [Rust successor](https://github.com/crazyklatsch/Arctis7_BatteryReader_rs), which document the Arctis receiver HID report. This repository's implementation and generated tray icon are original. The referenced projects use their own licenses; see their repositories for their terms.

This project is distributed under the MIT License; see [LICENSE](LICENSE).

SteelSeries is not affiliated with this project.
