using System.IO;
using System.Text.Json;
using DustLoadScada;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Newland.Fm430.Tests;

public sealed class ArchiveFingerprintTests
{
    [Fact]
    public void IdenticalLatchedResultsAreStoredOnceAcrossRestartAndDeletion()
    {
        using var folder = new TempFolder();
        var first = Result();
        var retry = first with { Elapsed = 18.5, Voltage = 30, RunState = 1 };
        Assert.Equal(first.ArchiveFingerprint(), retry.ArchiveFingerprint());

        var store = new StationStore(folder.Path);
        Assert.True(store.Record("plc:台架-01", first));
        Assert.False(store.Record("plc:台架-01", retry));
        Assert.True(store.Record("plc:台架-02", first));
        Assert.Equal(2, store.History(null, null, "").Count());

        store = new StationStore(folder.Path);
        Assert.False(store.Record("plc:台架-01", first with { Elapsed = 1 }));

        long id = store.History(null, null, "").Single(record => record.Source == "plc:台架-01").Id;
        store.DeleteRecord(id);
        Assert.True(store.Record("plc:台架-01", first));
    }

    [Fact]
    public void ChangedBarcodeModelOrMeasurementIsANewResult()
    {
        using var folder = new TempFolder();
        var store = new StationStore(folder.Path);
        var first = Result();
        Assert.True(store.Record("plc:台架-01", first));
        Assert.True(store.Record("plc:台架-01", first with { Barcode = "SN-2" }));
        Assert.True(store.Record("plc:台架-01", first with { Model = "其他型号" }));
        Assert.True(store.Record("plc:台架-01", first with { NormalVoltage = 0 }));
        Assert.True(store.Record("plc:台架-01", first with { Result = Verdict.Fail }));
        Assert.False(store.Record("plc:台架-01", first with { Stage = TestStage.Normal }));
        Assert.False(store.Record("plc:台架-01", first with { Result = Verdict.Pending }));
        Assert.Equal(5, store.History(null, null, "").Count());
    }

    [Fact]
    public void SimulationKeepsEachCompletedRun()
    {
        using var folder = new TempFolder();
        var store = new StationStore(folder.Path);
        var first = Result();
        Assert.True(store.Record("simulation", first));
        Assert.True(store.Record("simulation", first with { Elapsed = 4 }));
        Assert.Equal(2, store.History(null, null, "").Count());
    }

    [Fact]
    public void ExistingHistoryIsFingerprintedOnce()
    {
        using var folder = new TempFolder();
        var saved = Result();
        var store = new StationStore(folder.Path);
        Assert.True(store.Record("plc:台架-01", saved));
        string database = Path.Combine(folder.Path, "station.db");
        using (var db = new SqliteConnection($"Data Source={database}"))
        {
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = """
                DELETE FROM result_fingerprints;
                DELETE FROM settings WHERE key='archive_fingerprint_v1';
                """;
            command.ExecuteNonQuery();
        }

        store = new StationStore(folder.Path);
        Assert.False(store.Record("plc:台架-01", saved with { Elapsed = 3, Voltage = 1 }));
        Assert.Single(store.History(null, null, ""));
    }

    [Fact]
    public void OpeningAnOldDatabaseDropsCycleStorage()
    {
        using var folder = new TempFolder();
        string database = Path.Combine(folder.Path, "station.db");
        using (var db = new SqliteConnection($"Data Source={database}"))
        {
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = """
                CREATE TABLE tests (
                    id INTEGER PRIMARY KEY AUTOINCREMENT, source TEXT NOT NULL, cycle_id INTEGER NOT NULL,
                    utc TEXT NOT NULL, barcode TEXT NOT NULL, data TEXT NOT NULL
                );
                CREATE TABLE plc_cycles (
                    source TEXT NOT NULL, cycle_id INTEGER NOT NULL, PRIMARY KEY (source, cycle_id)
                );
                INSERT INTO tests(source, cycle_id, utc, barcode, data)
                    VALUES ('plc:台架-01', 7, '2026-01-01T00:00:00.0000000+00:00', 'SN-1', $data);
                INSERT INTO plc_cycles VALUES ('plc:台架-01', 7);
                """;
            command.Parameters.AddWithValue("$data", JsonSerializer.Serialize(Result()));
            command.ExecuteNonQuery();
        }

        var store = new StationStore(folder.Path);
        Assert.False(store.Record("plc:台架-01", Result() with { Elapsed = 2 }));
        Assert.Single(store.History(null, null, ""));

        SqliteConnection.ClearAllPools();
        using var check = new SqliteConnection($"Data Source={database}");
        check.Open();
        using var query = check.CreateCommand();
        query.CommandText = "SELECT 1 FROM pragma_table_info('tests') WHERE name='cycle_id'";
        Assert.Null(query.ExecuteScalar());
        query.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name='plc_cycles'";
        Assert.Null(query.ExecuteScalar());
    }

    [Fact]
    public void CompletedSnapshotIgnoresTheFormerCycleWords()
    {
        var words = new ushort[PlcLayout.TelemetryWords];
        words[2] = 0xFFFF;
        words[3] = 0xFFFF;
        var (test, _) = PlcLayout.Decode(words, false, 3, _ => 1, "SN-1", true);

        Assert.Equal(TestStage.Completed, test.Stage);
        Assert.Equal(Verdict.Pass, test.Result);
        Assert.Equal("SN-1", test.Barcode);
    }

    [Fact]
    public void CompletedSnapshotStillRequiresAVerdict()
    {
        var words = new ushort[PlcLayout.TelemetryWords];
        var error = Assert.Throws<InvalidDataException>(() =>
            PlcLayout.Decode(words, false, 3, id => id == MonitorPointId.Result ? 0 : 1, "SN-1", true));

        Assert.Contains("总判定", error.Message);
    }

    private static TestSnapshot Result() => new()
    {
        Stage = TestStage.Completed,
        Barcode = "SN-1",
        Model = "MODEL",
        Elapsed = 8,
        Voltage = 24.2,
        NormalVoltage = 24.2,
        NormalCurrent = 1.25,
        DustCurrent = 1.8,
        NormalPower = 86.2,
        DustPower = 99.6,
        PowerDelta = 13.4,
        NormalSpeed = 2495,
        DustSpeed = 2280,
        SpeedDelta = 215,
        PowerVerdict = Verdict.Pass,
        SpeedVerdict = Verdict.Pass,
        DirectionVerdict = Verdict.Pass,
        BrushLightVerdict = Verdict.Pass,
        BrushLightOffVerdict = Verdict.Pass,
        NormalCurrentVerdict = Verdict.Pass,
        NormalPowerVerdict = Verdict.Pass,
        NormalSpeedVerdict = Verdict.Pass,
        DustCurrentVerdict = Verdict.Pass,
        DustPowerVerdict = Verdict.Pass,
        DustSpeedVerdict = Verdict.Pass,
        Result = Verdict.Pass
    };

    private sealed class TempFolder : IDisposable
    {
        public TempFolder() => Directory.CreateDirectory(Path);
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"DustLoadScada-archive-{Guid.NewGuid():N}");
        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(Path, true);
        }
    }
}
