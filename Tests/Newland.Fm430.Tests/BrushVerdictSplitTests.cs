using System.IO;
using DustLoadScada;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Newland.Fm430.Tests;

public sealed class BrushVerdictSplitTests
{
    [Fact]
    public void SharedBrushVerdictsMoveToIndependentWordsOnStartup()
    {
        using var folder = new TempFolder();
        var store = new StationStore(folder.Path);
        store.SaveBarcodePoint(new BarcodePoint(4000, 80));
        store.SaveMonitorPoints(SharedPoints());
        ClearSplitMark(folder.Path);
        var settings = new PlcSettings();
        var barcode = new BarcodePoint(4000, 80);
        var error = Assert.Throws<InvalidOperationException>(() => new MonitorReadPlan(settings, store.LoadMonitorPoints(), barcode));
        Assert.Contains("重叠", error.Message);

        store = new StationStore(folder.Path);
        var points = store.LoadMonitorPoints();
        Assert.Equal((ushort)38, points[MonitorPointId.BrushLightVerdict].Address);
        Assert.Equal((ushort)41, points[MonitorPointId.BrushLightOffVerdict].Address);
        Assert.Equal((ushort)39, points[MonitorPointId.HeadlightIndicatorCameraVerdict].Address);
        Assert.Equal((ushort)40, points[MonitorPointId.IndicatorOffCameraVerdict].Address);
        _ = new MonitorReadPlan(settings, points, barcode);

        store = new StationStore(folder.Path);
        Assert.Equal((ushort)38, store.LoadMonitorPoints()[MonitorPointId.BrushLightVerdict].Address);
        Assert.Equal((ushort)41, store.LoadMonitorPoints()[MonitorPointId.BrushLightOffVerdict].Address);
    }

    [Fact]
    public void UnsharedBrushVerdictsAreNotMoved()
    {
        using var folder = new TempFolder();
        var store = new StationStore(folder.Path);
        store.SaveBarcodePoint(new BarcodePoint(4000, 80));
        var points = SharedPoints();
        points[MonitorPointId.BrushLightVerdict] = new(25, RegisterDataType.UInt16);
        points[MonitorPointId.BrushLightOffVerdict] = new(26, RegisterDataType.UInt16);
        store.SaveMonitorPoints(points);
        ClearSplitMark(folder.Path);

        store = new StationStore(folder.Path);
        var loaded = store.LoadMonitorPoints();
        Assert.Equal((ushort)25, loaded[MonitorPointId.BrushLightVerdict].Address);
        Assert.Equal((ushort)26, loaded[MonitorPointId.BrushLightOffVerdict].Address);
    }

    [Fact]
    public void OccupiedTargetWordBlocksOnlyThatPair()
    {
        using var folder = new TempFolder();
        var store = new StationStore(folder.Path);
        store.SaveBarcodePoint(new BarcodePoint(4000, 80));
        var points = SharedPoints();
        points[MonitorPointId.Result] = new(38, RegisterDataType.UInt16);
        store.SaveMonitorPoints(points);
        ClearSplitMark(folder.Path);

        store = new StationStore(folder.Path);
        var loaded = store.LoadMonitorPoints();
        Assert.Equal((ushort)39, loaded[MonitorPointId.BrushLightVerdict].Address);
        Assert.Equal((ushort)41, loaded[MonitorPointId.BrushLightOffVerdict].Address);
    }

    [Fact]
    public void BarcodeBlockCoveringTheTargetWordsBlocksTheSplit()
    {
        using var folder = new TempFolder();
        var store = new StationStore(folder.Path);
        store.SaveBarcodePoint(new BarcodePoint(30, 16));
        store.SaveMonitorPoints(SharedPoints());
        ClearSplitMark(folder.Path);

        store = new StationStore(folder.Path);
        var loaded = store.LoadMonitorPoints();
        Assert.Equal((ushort)39, loaded[MonitorPointId.BrushLightVerdict].Address);
        Assert.Equal((ushort)40, loaded[MonitorPointId.BrushLightOffVerdict].Address);
    }

    // 模拟旧版本数据库：配置已存在但尚无迁移标记，新版本首次打开时执行迁移。
    private static void ClearSplitMark(string folder)
    {
        using var db = new SqliteConnection($"Data Source={Path.Combine(folder, "station.db")}");
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = "DELETE FROM settings WHERE key='brush_verdict_split_v1'";
        command.ExecuteNonQuery();
    }

    private static Dictionary<MonitorPointId, MonitorPoint> SharedPoints() => new()
    {
        [MonitorPointId.HeadlightIndicatorCameraVerdict] = new(39, RegisterDataType.UInt16),
        [MonitorPointId.IndicatorOffCameraVerdict] = new(40, RegisterDataType.UInt16),
        [MonitorPointId.BrushLightVerdict] = new(39, RegisterDataType.UInt16),
        [MonitorPointId.BrushLightOffVerdict] = new(40, RegisterDataType.UInt16),
        [MonitorPointId.PowerVerdict] = new(42, RegisterDataType.UInt16),
        [MonitorPointId.SpeedVerdict] = new(43, RegisterDataType.UInt16)
    };

    private sealed class TempFolder : IDisposable
    {
        public TempFolder() => Directory.CreateDirectory(Path);
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"DustLoadScada-brushsplit-{Guid.NewGuid():N}");
        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(Path, true);
        }
    }
}
