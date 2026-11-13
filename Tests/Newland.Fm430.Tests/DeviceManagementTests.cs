using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows;
using DustLoadScada;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Newland.Fm430.Tests;

public sealed class DeviceManagementTests
{
    [Fact]
    public void SaveAllValidatesInputsPreservesSkippedDevicesAndReportsPartialWrites()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { VerifySaveAll(); }
            catch (Exception ex) { failure = ex; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "Device management test timed out.");
        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static void VerifySaveAll()
    {
        string folder = Path.Combine(Path.GetTempPath(), $"DustLoadScada-devices-{Guid.NewGuid():N}");
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        try
        {
            app.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("/DustLoadScada;component/Theme.xaml", UriKind.Relative)
            });
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
            string notice = page.SaveAllSettings();
            Assert.Contains("相机、扫码枪、PLC", notice);
            Assert.Equal("camera.new", store.LoadPeripherals().Camera.Host);
            Assert.Equal("COM7", store.LoadPeripherals().Scanner.Port);
            var savedPlc = PlcSettings.Load(plcPath);
            Assert.Equal("192.168.1.20", savedPlc.Host);
            Assert.Equal((ushort)200, savedPlc.TelemetryAddress);
            Assert.Equal(600, savedPlc.PollMilliseconds);

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
            app.Shutdown();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
    }
}
