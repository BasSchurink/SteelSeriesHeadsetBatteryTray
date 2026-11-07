using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using Microsoft.Win32;
using HidSharp;

namespace SteelSeriesHeadsetBatteryTray;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(initiallyOwned: true, "Local\\SteelSeriesHeadsetBatteryTray", out var createdNew);
        if (!createdNew)
        {
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApplicationContext());
    }
}

internal sealed class TrayApplicationContext : ApplicationContext
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "SteelSeriesHeadsetBatteryTray";

    private readonly NotifyIcon _trayIcon;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _refreshItem;
    private readonly ToolStripMenuItem _startupItem;
    private readonly System.Windows.Forms.Timer _pollTimer;
    private string? _lastLoggedReadError;
    private bool _refreshInProgress;

    public TrayApplicationContext()
    {
        _statusItem = new ToolStripMenuItem("Checking headset…") { Enabled = false };
        _refreshItem = new ToolStripMenuItem("Refresh", null, async (_, _) => await RefreshAsync());
        _startupItem = new ToolStripMenuItem("Start with Windows")
        {
            CheckOnClick = true,
            Checked = IsStartupEnabled()
        };
        _startupItem.CheckedChanged += StartupItemCheckedChanged;

        var menu = new ContextMenuStrip();
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_refreshItem);
        menu.Items.Add(new ToolStripMenuItem("Open diagnostic log", null, (_, _) => OpenDiagnosticLog()));
        menu.Items.Add(_startupItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => ExitThread()));

        _trayIcon = new NotifyIcon
        {
            Icon = TrayIconFactory.Create(null),
            Text = "Arctis 7 battery: checking…",
            ContextMenuStrip = menu,
            Visible = true
        };
        _trayIcon.DoubleClick += async (_, _) => await RefreshAsync();

        _pollTimer = new System.Windows.Forms.Timer { Interval = 5000 };
        _pollTimer.Tick += async (_, _) => await RefreshAsync();
        _pollTimer.Start();
        _ = RefreshAsync();
    }

    protected override void ExitThreadCore()
    {
        _pollTimer.Stop();
        _pollTimer.Dispose();
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        base.ExitThreadCore();
    }

    private async Task RefreshAsync()
    {
        if (_refreshInProgress)
        {
            return;
        }

        _refreshInProgress = true;
        _refreshItem.Enabled = false;
        try
        {
            var result = await Task.Run(HeadsetReader.ReadBattery);
            UpdateStatus(result);
        }
        catch (Exception exception) when (exception is IOException or TimeoutException or UnauthorizedAccessException or InvalidOperationException)
        {
            UpdateStatus(BatteryStatus.Error(exception.Message, exception));
        }
        finally
        {
            _refreshItem.Enabled = true;
            _refreshInProgress = false;
        }
    }

    private void UpdateStatus(BatteryStatus status)
    {
        var menuText = status.MenuText;
        if (status.ErrorMessage is null)
        {
            _lastLoggedReadError = null;
        }
        else if (status.ErrorMessage != _lastLoggedReadError)
        {
            try
            {
                AppLogger.WriteReadError(status.ErrorMessage, status.Exception);
                _lastLoggedReadError = status.ErrorMessage;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException or ArgumentException or NotSupportedException)
            {
                menuText += $" (log write failed: {exception.Message})";
            }
        }

        _statusItem.Text = menuText;
        _trayIcon.Text = status.Tooltip.Length > 63 ? status.Tooltip[..63] : status.Tooltip;

        var replacement = TrayIconFactory.Create(status.Percentage);
        var previous = _trayIcon.Icon;
        _trayIcon.Icon = replacement;
        previous?.Dispose();
    }

    private static void OpenDiagnosticLog()
    {
        try
        {
            if (!File.Exists(AppLogger.LogFilePath))
            {
                MessageBox.Show(
                    $"No read errors have been logged yet. The log will be created here:\n{AppLogger.LogFilePath}",
                    "Arctis 7 Battery",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            Process.Start(new ProcessStartInfo(AppLogger.LogFilePath) { UseShellExecute = true });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            MessageBox.Show(
                $"Could not open the diagnostic log: {exception.Message}",
                "Arctis 7 Battery",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private static bool IsStartupEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return key?.GetValue(RunValueName) is string;
    }

    private void SetStartupEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (key is null)
            {
                throw new InvalidOperationException("The Windows startup registry key could not be opened.");
            }

            if (enabled)
            {
                key.SetValue(RunValueName, $"\"{Application.ExecutablePath}\"");
            }
            else
            {
                key.DeleteValue(RunValueName, throwOnMissingValue: false);
            }
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or InvalidOperationException)
        {
            _startupItem.CheckedChanged -= StartupItemCheckedChanged;
            _startupItem.Checked = !enabled;
            _startupItem.CheckedChanged += StartupItemCheckedChanged;
            MessageBox.Show(
                $"Could not update the Windows startup setting: {exception.Message}",
                "Arctis 7 Battery",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private void StartupItemCheckedChanged(object? sender, EventArgs e) => SetStartupEnabled(_startupItem.Checked);
}

internal static class HeadsetReader
{
    private const int SteelSeriesVendorId = 0x1038;
    private const int Arctis7_2017ProductId = 0x1260;
    private const int Arctis7_2019ProductId = 0x12AD;

    public static BatteryStatus ReadBattery()
    {
        var compatibleDeviceFound = false;
        var lastFailure = "The Arctis 7 receiver did not return a battery report.";
        Exception? lastException = null;

        foreach (var device in DeviceList.Local.GetHidDevices(SteelSeriesVendorId))
        {
            var model = device.ProductID switch
            {
                Arctis7_2017ProductId => "Arctis 7 (2017)",
                Arctis7_2019ProductId => "Arctis 7 (2019)",
                _ => null
            };

            if (model is null)
            {
                continue;
            }

            compatibleDeviceFound = true;
            var outputReportLength = device.GetMaxOutputReportLength();
            var inputReportLength = device.GetMaxInputReportLength();
            if (outputReportLength < 2 || inputReportLength < 3)
            {
                lastFailure = $"{model} reported unsupported HID report sizes (input: {inputReportLength}, output: {outputReportLength}).";
                lastException = null;
                continue;
            }

            if (!device.TryOpen(out HidStream stream))
            {
                lastFailure = $"Could not open the {model} HID interface (input: {inputReportLength}, output: {outputReportLength}).";
                lastException = null;
                continue;
            }

            using (stream)
            {
                stream.ReadTimeout = 500;
                var request = new byte[outputReportLength];
                request[0] = 0x06;
                request[1] = 0x18;
                try
                {
                    stream.Write(request);

                    var report = new byte[inputReportLength];
                    var bytesRead = stream.Read(report, 0, report.Length);
                    if (bytesRead <= 2)
                    {
                        lastFailure = $"{model} returned an incomplete battery report ({bytesRead} bytes; input report length {inputReportLength}).";
                        lastException = null;
                        continue;
                    }

                    var percentage = report[2];
                    if (percentage > 100)
                    {
                        lastFailure = $"{model} returned an invalid battery percentage ({percentage}).";
                        lastException = null;
                        continue;
                    }

                    return BatteryStatus.Connected(model, percentage);
                }
                catch (Exception exception) when (exception is IOException or TimeoutException or UnauthorizedAccessException or InvalidOperationException)
                {
                    lastFailure = $"{model} HID read failed (input: {inputReportLength}, output: {outputReportLength}): {exception.Message}";
                    lastException = exception;
                }
            }
        }

        return compatibleDeviceFound
            ? BatteryStatus.Error(lastFailure, lastException)
            : BatteryStatus.Disconnected();
    }
}

internal sealed record BatteryStatus(int? Percentage, string MenuText, string Tooltip, string? ErrorMessage = null, Exception? Exception = null)
{
    public static BatteryStatus Connected(string model, int percentage) =>
        new(percentage, $"{model}: {percentage}%", $"{model} battery: {percentage}%");

    public static BatteryStatus Disconnected() =>
        new(null, "No compatible Arctis 7 receiver found", "Arctis 7 battery: not detected");

    public static BatteryStatus Error(string message) =>
        Error(message, null);

    public static BatteryStatus Error(string message, Exception? exception) =>
        new(null, $"Battery read failed: {message}", "Arctis 7 battery: read error", message, exception);
}

internal static class AppLogger
{
    public static string LogFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SteelSeriesHeadsetBatteryTray",
        "app.log");

    public static void WriteReadError(string message, Exception? exception)
    {
        var entry = new StringBuilder()
            .Append('[')
            .Append(DateTimeOffset.Now.ToString("O"))
            .AppendLine("] Arctis 7 battery read error")
            .AppendLine(message);

        if (exception is not null)
        {
            entry.AppendLine(exception.ToString());
        }

        entry.AppendLine();
        Directory.CreateDirectory(Path.GetDirectoryName(LogFilePath)!);
        File.AppendAllText(LogFilePath, entry.ToString(), Encoding.UTF8);
    }
}

internal static class TrayIconFactory
{
    public static Icon Create(int? percentage)
    {
        using var bitmap = new Bitmap(32, 32, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        using (var background = new SolidBrush(percentage is null
                   ? Color.DimGray
                   : percentage >= 50 ? Color.SeaGreen : percentage >= 20 ? Color.DarkGoldenrod : Color.Firebrick))
        using (var font = new Font("Segoe UI", percentage is >= 100 ? 9 : 12, FontStyle.Bold, GraphicsUnit.Pixel))
        using (var textBrush = new SolidBrush(Color.White))
        using (var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.FillEllipse(background, 1, 1, 30, 30);
            graphics.DrawString(percentage?.ToString() ?? "?", font, textBrush, new RectangleF(1, 1, 30, 30), format);
        }

        var handle = bitmap.GetHicon();
        try
        {
            using var icon = Icon.FromHandle(handle);
            return (Icon)icon.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);
}
