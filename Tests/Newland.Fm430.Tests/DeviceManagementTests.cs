using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Threading;
using DustLoadScada;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Newland.Fm430.Tests;

public sealed class DeviceManagementTests
{
    // WPF 每个 AppDomain 只允许一个 Application，且资源字典有线程亲和性，
    // 因此所有 UI 验证共享一个常驻 STA 线程与 Dispatcher。
    private static readonly object StaGate = new();
    private static Dispatcher? staDispatcher;

    private static void RunOnStaThread(Action work)
    {
        lock (StaGate)
        {
            if (staDispatcher is null)
            {
                using var ready = new ManualResetEventSlim();
                var thread = new Thread(() =>
                {
                    var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                    app.Resources.MergedDictionaries.Add(new ResourceDictionary
                    {
                        Source = new Uri("/DustLoadScada;component/Theme.xaml", UriKind.Relative)
                    });
                    staDispatcher = Dispatcher.CurrentDispatcher;
                    ready.Set();
                    Dispatcher.Run();
                }) { IsBackground = true };
                thread.SetApartmentState(ApartmentState.STA);
                thread.Start();
                Assert.True(ready.Wait(TimeSpan.FromSeconds(20)), "STA host startup timed out.");
            }
        }
        Exception? failure = null;
        var operation = staDispatcher!.InvokeAsync(() =>
        {
            try { work(); }
            catch (Exception ex) { failure = ex; }
        });
        Assert.True(operation.Task.Wait(TimeSpan.FromSeconds(30)), "Device management test timed out.");
        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
    }

    [Fact]
    public void SaveAllValidatesInputsPreservesSkippedDevicesAndReportsPartialWrites() => RunOnStaThread(VerifySaveAll);

    private static void VerifySaveAll()
    {
        string folder = Path.Combine(Path.GetTempPath(), $"DustLoadScada-devices-{Guid.NewGuid():N}");
        try
        {
            var store = new StationStore(folder);
            var original = new PeripheralSettings(new("camera.old"), new("COM5"));
            store.SavePeripherals(original);
            string plcPath = Path.Combine(folder, "plc.json");
            new PlcSettings { TelemetryAddress = 200, PollMilliseconds = 600 }.Save(plcPath);
            string initialPlc = File.ReadAllText(plcPath);
            int connections = 0;
            var page = new DeviceManagement();
            page.Initialize(store, folder,
                _ => { connections++; return Task.CompletedTask; },
                () => { connections++; return Task.CompletedTask; });
            page.CameraHost.Text = "camera.new";
            page.PlcHost.Text = "192.168.1.20";
            page.ScannerPort.Text = "invalid-port";

            Assert.Throws<InvalidOperationException>(() => page.SaveAllSettings());
            Assert.Equal(original, store.LoadPeripherals());
            Assert.Equal(initialPlc, File.ReadAllText(plcPath));

            page.ScannerPort.Text = "COM7";
            page.ScannerSendText.Text = "01 54 04";
            page.ScannerSendFormat.SelectedIndex = 1;
            page.ScannerSendEnding.SelectedIndex = 2;
            string notice = page.SaveAllSettings();
            Assert.Contains("相机、扫码枪、PLC", notice);
            Assert.Equal("camera.new", store.LoadPeripherals().Camera.Host);
            Assert.Equal("COM7", store.LoadPeripherals().Scanner.Port);
            var savedScanner = store.LoadPeripherals().Scanner;
            Assert.Equal("01 54 04", savedScanner.SendText);
            Assert.True(savedScanner.SendHex);
            Assert.Equal(2, savedScanner.SendEnding);

            var reloaded = new DeviceManagement();
            reloaded.Initialize(store, folder, _ => Task.CompletedTask, () => Task.CompletedTask);
            Assert.Equal("01 54 04", reloaded.ScannerSendText.Text);
            Assert.Equal(1, reloaded.ScannerSendFormat.SelectedIndex);
            Assert.Equal(2, reloaded.ScannerSendEnding.SelectedIndex);
            var savedPlc = PlcSettings.Load(plcPath);
            Assert.Equal("192.168.1.20", savedPlc.Host);
            Assert.Equal((ushort)200, savedPlc.TelemetryAddress);
            Assert.Equal(600, savedPlc.PollMilliseconds);

            page.CameraMessage.Text = "switch 1";
            page.CameraEnding.SelectedIndex = 3;
            page.PlcTestAreaPicker.SelectedValue = "Coils";
            page.PlcTestAddress.Text = "100";
            page.PlcTestCount.Text = "8";
            page.PlcTestValue.Text = "1";
            page.PlcTestResult.Text = "PLC 已确认写入 100 = 1。";
            page.SaveAllSettings();
            var savedCamera = store.LoadPeripherals().Camera;
            Assert.Equal("switch 1", savedCamera.SendText);
            Assert.Equal(3, savedCamera.SendEnding);
            var savedState = store.LoadDevicePageState();
            Assert.Equal("Coils", savedState.PlcTestArea);
            Assert.Equal("100", savedState.PlcTestAddress);
            Assert.Equal("8", savedState.PlcTestCount);
            Assert.Equal("1", savedState.PlcTestValue);
            Assert.Equal("PLC 已确认写入 100 = 1。", savedState.PlcTestResult);

            var restored = new DeviceManagement();
            restored.Initialize(store, folder, _ => Task.CompletedTask, () => Task.CompletedTask);
            Assert.Equal("switch 1", restored.CameraMessage.Text);
            Assert.Equal(3, restored.CameraEnding.SelectedIndex);
            Assert.Equal("Coils", restored.PlcTestAreaPicker.SelectedValue);
            Assert.Equal("100", restored.PlcTestAddress.Text);
            Assert.Equal("8", restored.PlcTestCount.Text);
            Assert.Equal("1", restored.PlcTestValue.Text);
            Assert.Equal("PLC 已确认写入 100 = 1。", restored.PlcTestResult.Text);

            page.CameraHost.Text = "";
            page.ScannerPort.Text = "";
            page.PlcHost.Text = "";
            Assert.Throws<InvalidOperationException>(() => page.SaveAllSettings());

            page.CameraHost.Text = "camera.only";
            string currentPlc = File.ReadAllText(plcPath);
            Assert.Contains("保留原配置", page.SaveAllSettings());
            Assert.Equal("camera.only", store.LoadPeripherals().Camera.Host);
            Assert.Equal("COM7", store.LoadPeripherals().Scanner.Port);
            Assert.Equal(currentPlc, File.ReadAllText(plcPath));

            Directory.CreateDirectory(plcPath + ".tmp");
            page.CameraHost.Text = "camera.partial";
            page.PlcHost.Text = "192.168.1.30";
            var error = Assert.Throws<InvalidOperationException>(() => page.SaveAllSettings());
            Assert.Contains("已保存：相机", error.Message);
            Assert.Equal("camera.partial", store.LoadPeripherals().Camera.Host);
            Assert.Equal(currentPlc, File.ReadAllText(plcPath));
            Assert.Equal(0, connections);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void WorkbenchSelectionLogsAndStatusRefresh() => RunOnStaThread(VerifyWorkbench);

    private static void VerifyWorkbench()
    {
        string folder = Path.Combine(Path.GetTempPath(), $"DustLoadScada-workbench-{Guid.NewGuid():N}");
        try
        {
            var store = new StationStore(folder);
            store.SavePeripherals(new PeripheralSettings(new("camera.local", 5001), new("COM3", 115200)));
            var page = new DeviceManagement();
            page.Initialize(store, folder, _ => Task.CompletedTask, () => Task.CompletedTask);
            page.Measure(new Size(1200, 800));
            page.Arrange(new Rect(0, 0, 1200, 800));
            page.UpdateLayout();

            Assert.Equal(Visibility.Visible, page.CameraPanel.Visibility);
            Assert.Equal(Visibility.Collapsed, page.PlcPanel.Visibility);
            Assert.Equal("active", page.RailCameraButton.Tag);
            Assert.Equal("camera.local:5001", page.RailCameraEndpoint.Text);
            Assert.Equal("COM3 · 115200", page.RailScannerEndpoint.Text);

            page.RailPlcButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Assert.Equal(Visibility.Visible, page.PlcPanel.Visibility);
            Assert.Equal(Visibility.Collapsed, page.CameraPanel.Visibility);
            Assert.Equal("active", page.RailPlcButton.Tag);
            Assert.Null(page.RailCameraButton.Tag);

            var tabs = FindTab(page.PlcPanel);
            tabs.SelectedIndex = 2;
            page.UpdateLayout();
            Assert.Equal(Visibility.Visible, page.PlcLogEmpty.Visibility);

            page.RailScannerButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Assert.Equal(Visibility.Visible, page.ScannerPanel.Visibility);
            Assert.NotEqual("0", page.LogCountScanner.Text);
            Assert.Equal(Visibility.Collapsed, page.ScannerLogEmpty.Visibility);
            Assert.Contains("刷新串口", page.ScannerLog.Items.Cast<DeviceLogEntry>().Last().Text);

            var refresh = typeof(DeviceManagement).GetMethod("RefreshStatus",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            refresh!.Invoke(page, null);
            Assert.Equal("未连接", page.RailCameraState.Text);
            Assert.Equal(page.FindResource("StateOff"), page.RailCameraDot.Fill);
            Assert.Equal(page.FindResource("StateOff"), page.CameraDot.Fill);

            var append = typeof(DeviceManagement).GetMethod("AppendLog",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            append!.Invoke(page, ["camera", "TX", "probe"]);
            Assert.Equal("1", page.LogCountCamera.Text);
            Assert.Equal(Visibility.Collapsed, page.CameraLogEmpty.Visibility);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
    }

    private static System.Windows.Controls.TabControl FindTab(DependencyObject root)
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is System.Windows.Controls.TabControl tabs)
                return tabs;
            var found = FindTab(child);
            if (found is not null)
                return found;
        }
        return null!;
    }
}
