using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;

namespace DustLoadScada;

public sealed class StationStore
{
    private static readonly JsonSerializerOptions PointJson = new()
    {
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };
    private readonly string connectionString;

    public StationStore(string folder)
    {
        Directory.CreateDirectory(folder);
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(folder, "station.db"),
            DefaultTimeout = 3
        }.ToString();

        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS recipes (
                id TEXT PRIMARY KEY, name TEXT NOT NULL UNIQUE COLLATE NOCASE, data TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS settings (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS tests (
                id INTEGER PRIMARY KEY AUTOINCREMENT, source TEXT NOT NULL,
                utc TEXT NOT NULL, barcode TEXT NOT NULL, data TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS result_fingerprints (
                source TEXT NOT NULL, fingerprint TEXT NOT NULL, PRIMARY KEY (source, fingerprint)
            );
            CREATE INDEX IF NOT EXISTS ix_tests_utc ON tests(utc);
            """;
        command.ExecuteNonQuery();
        RemoveCycleStorage(db);
        EnsureArchiveFingerprints(db);
        if (Recipes().Count == 0)
            SaveRecipe(new Recipe(), true);
    }

    private SqliteConnection Open()
    {
        var db = new SqliteConnection(connectionString);
        db.Open();
        return db;
    }

    private static void RemoveCycleStorage(SqliteConnection db)
    {
        using var check = db.CreateCommand();
        check.CommandText = "SELECT 1 FROM pragma_table_info('tests') WHERE name='cycle_id'";
        if (check.ExecuteScalar() is not null)
        {
            using var alter = db.CreateCommand();
            alter.CommandText = "ALTER TABLE tests DROP COLUMN cycle_id";
            alter.ExecuteNonQuery();
        }

        using var drop = db.CreateCommand();
        drop.CommandText = "DROP TABLE IF EXISTS plc_cycles";
        drop.ExecuteNonQuery();
    }

    // Existing history was deduplicated by cycle id. Give those rows a fingerprint once so a replay of the same result is still ignored.
    private static void EnsureArchiveFingerprints(SqliteConnection db)
    {
        using var check = db.CreateCommand();
        check.CommandText = "SELECT 1 FROM settings WHERE key='archive_fingerprint_v1'";
        if (check.ExecuteScalar() is not null)
            return;

        var pending = new List<(string Source, string Fingerprint)>();
        using (var read = db.CreateCommand())
        {
            read.CommandText = "SELECT source, data FROM tests WHERE source != 'simulation'";
            using var reader = read.ExecuteReader();
            while (reader.Read())
            {
                var test = JsonSerializer.Deserialize<TestSnapshot>(reader.GetString(1))
                    ?? throw new InvalidDataException("历史记录为空，无法建立结果指纹。");
                pending.Add((reader.GetString(0), test.ArchiveFingerprint()));
            }
        }

        using var transaction = db.BeginTransaction();
        using var insert = db.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = "INSERT OR IGNORE INTO result_fingerprints VALUES ($source, $fingerprint)";
        var sourceParameter = insert.Parameters.Add("$source", SqliteType.Text);
        var fingerprintParameter = insert.Parameters.Add("$fingerprint", SqliteType.Text);
        foreach (var (source, fingerprint) in pending)
        {
            sourceParameter.Value = source;
            fingerprintParameter.Value = fingerprint;
            insert.ExecuteNonQuery();
        }

        using var mark = db.CreateCommand();
        mark.Transaction = transaction;
        mark.CommandText = "INSERT INTO settings VALUES ('archive_fingerprint_v1', '1')";
        mark.ExecuteNonQuery();
        transaction.Commit();
    }

    public Dictionary<MonitorPointId, MonitorPoint> LoadMonitorPoints() => LoadPoints<MonitorPointId, MonitorPoint>("monitor_points");
    internal BarcodePoint? LoadBarcodePoint() => LoadSetting<BarcodePoint>("barcode_point");
    internal ushort? LoadRecordTrigger() => LoadSetting<RecordTriggerPoint>("record_trigger")?.Address;
    internal ushort? LoadTimerTrigger() => LoadSetting<RecordTriggerPoint>("timer_trigger")?.Address;
    internal ScannerTriggerSettings LoadScannerTrigger()
    {
        var settings = LoadSetting<ScannerTriggerSettings>("scanner_trigger") ?? new();
        settings.Validate();
        return settings;
    }

    internal void SaveScannerTrigger(ScannerTriggerSettings settings)
    {
        settings.Validate();
        SaveSetting("scanner_trigger", settings);
    }

    public Dictionary<ManualPointId, ManualPoint> LoadManualPoints() => LoadPoints<ManualPointId, ManualPoint>("manual_points");
    public Dictionary<RecipePointId, ushort> LoadRecipePoints() => LoadPoints<RecipePointId, ushort>("recipe_points");
    public Dictionary<string, InspectionPoint> LoadInspectionPoints() => LoadPoints<string, InspectionPoint>("inspection_points");
    public PeripheralSettings LoadPeripherals() => LoadSetting<PeripheralSettings>("peripherals") ?? new(new(), new());

    private Dictionary<TKey, TValue> LoadPoints<TKey, TValue>(string key) where TKey : notnull =>
        LoadSetting<Dictionary<TKey, TValue>>(key) ?? new();

    private T? LoadSetting<T>(string key) where T : class
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT value FROM settings WHERE key=$key";
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar() is string json
            ? JsonSerializer.Deserialize<T>(json, PointJson) ?? throw new InvalidDataException("本地配置为空。")
            : null;
    }

    public void SaveMonitorPoints(IReadOnlyDictionary<MonitorPointId, MonitorPoint> points) => SavePoints("monitor_points", points);
    internal void SaveRecordTrigger(ushort? address) => SaveSetting("record_trigger", new RecordTriggerPoint(address));
    internal void SaveTimerTrigger(ushort? address) => SaveSetting("timer_trigger", new RecordTriggerPoint(address));

    private sealed record RecordTriggerPoint(ushort? Address);

    internal ushort? LoadRuntimePoint() => LoadSetting<RuntimePoint>("runtime_point")?.Address;
    internal void SaveRuntimePoint(ushort? address) => SaveSetting("runtime_point", new RuntimePoint(address));
    internal TimeSpan LoadRuntimeTotal() =>
        TimeSpan.FromMilliseconds(LoadSetting<RuntimeTotal>("runtime_total")?.Milliseconds ?? 0);
    internal void SaveRuntimeTotal(TimeSpan total) =>
        SaveSetting("runtime_total", new RuntimeTotal((long)total.TotalMilliseconds));

    private sealed record RuntimePoint(ushort? Address);
    private sealed record RuntimeTotal(long Milliseconds);
    internal void SaveBarcodePoint(BarcodePoint? point)
    {
        if (point is not null)
        {
            SaveSetting("barcode_point", point);
            return;
        }

        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = "DELETE FROM settings WHERE key='barcode_point'";
        command.ExecuteNonQuery();
    }
    public void SaveManualPoints(IReadOnlyDictionary<ManualPointId, ManualPoint> points) => SavePoints("manual_points", points);
    public void SaveRecipePoints(IReadOnlyDictionary<RecipePointId, ushort> points) => SavePoints("recipe_points", points);
    public void SaveInspectionPoints(IReadOnlyDictionary<string, InspectionPoint> points) => SavePoints("inspection_points", points);
    public void SavePeripherals(PeripheralSettings settings) => SaveSetting("peripherals", settings);

    private void SavePoints<TKey, TValue>(string key, IReadOnlyDictionary<TKey, TValue> points) where TKey : notnull =>
        SaveSetting(key, points);

    private void SaveSetting<T>(string key, T value)
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = """
            INSERT INTO settings VALUES ($key, $value)
            ON CONFLICT(key) DO UPDATE SET value=excluded.value
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", JsonSerializer.Serialize(value, PointJson));
        command.ExecuteNonQuery();
    }

    public List<Recipe> Recipes()
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT data FROM recipes ORDER BY name";
        using var reader = command.ExecuteReader();
        var result = new List<Recipe>();
        while (reader.Read())
            result.Add(JsonSerializer.Deserialize<Recipe>(reader.GetString(0))!);
        return result;
    }

    public Recipe? ActiveRecipe()
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT value FROM settings WHERE key='active_recipe_data'";
        if (command.ExecuteScalar() is string json)
            return JsonSerializer.Deserialize<Recipe>(json);

        command.CommandText = """
            SELECT data FROM recipes WHERE id=(SELECT value FROM settings WHERE key='active_recipe')
            """;
        return command.ExecuteScalar() is string legacy ? JsonSerializer.Deserialize<Recipe>(legacy) : null;
    }

    public void SaveRecipe(Recipe recipe, bool activate)
    {
        recipe.Validate();
        using var db = Open();
        using var transaction = db.BeginTransaction();
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO recipes VALUES ($id, $name, $data)
            ON CONFLICT(id) DO UPDATE SET name=excluded.name, data=excluded.data
            """;
        command.Parameters.AddWithValue("$id", recipe.Id);
        command.Parameters.AddWithValue("$name", recipe.Name);
        command.Parameters.AddWithValue("$data", JsonSerializer.Serialize(recipe));
        try { command.ExecuteNonQuery(); }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            throw new InvalidOperationException("配方名称已存在。", ex);
        }

        if (activate)
        {
            command.CommandText = """
                INSERT INTO settings VALUES ('active_recipe', $id), ('active_recipe_data', $data)
                ON CONFLICT(key) DO UPDATE SET value=excluded.value
                """;
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public void DeleteRecipe(string id)
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = """
            DELETE FROM recipes WHERE id=$id AND (SELECT COUNT(*) FROM recipes)>1
                AND id IS NOT (SELECT value FROM settings WHERE key='active_recipe')
            """;
        command.Parameters.AddWithValue("$id", id);
        if (command.ExecuteNonQuery() == 0)
            throw new InvalidOperationException("请先切换到其他配方，再删除此配方。");
    }

    public bool Record(string source, TestSnapshot test)
    {
        if (test.Stage != TestStage.Completed || test.Result == Verdict.Pending)
            return false;

        using var db = Open();
        using var transaction = db.BeginTransaction();
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.Parameters.AddWithValue("$source", source);
        if (source != "simulation")
        {
            command.Parameters.AddWithValue("$fingerprint", test.ArchiveFingerprint());
            command.CommandText = "INSERT OR IGNORE INTO result_fingerprints VALUES ($source, $fingerprint)";
            if (command.ExecuteNonQuery() == 0)
                return false;
        }

        command.CommandText = "INSERT INTO tests(source, utc, barcode, data) VALUES ($source, $utc, $barcode, $data)";
        command.Parameters.AddWithValue("$utc", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$barcode", test.Barcode);
        command.Parameters.AddWithValue("$data", JsonSerializer.Serialize(test));
        command.ExecuteNonQuery();
        transaction.Commit();
        return true;
    }

    public IEnumerable<TestRecord> History(DateTime? start, DateTime? end, string barcode)
    {
        if (start > end)
            throw new InvalidOperationException("开始日期须早于或等于结束日期。");

        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = """
            SELECT id, source, utc, data FROM tests
            WHERE ($start IS NULL OR utc >= $start) AND ($end IS NULL OR utc < $end)
              AND ($barcode = '' OR instr(barcode, $barcode) > 0)
            ORDER BY utc DESC, id DESC
            """;
        command.Parameters.AddWithValue("$start", start.HasValue ? UtcDate(start.Value) : DBNull.Value);
        command.Parameters.AddWithValue("$end", end.HasValue && end.Value.Date < DateTime.MaxValue.Date
            ? UtcDate(end.Value.AddDays(1)) : DBNull.Value);
        command.Parameters.AddWithValue("$barcode", barcode.Trim());
        using var reader = command.ExecuteReader();
        while (reader.Read())
            yield return new TestRecord(reader.GetInt64(0), reader.GetString(1),
                DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture),
                JsonSerializer.Deserialize<TestSnapshot>(reader.GetString(3))!);
    }

    private static string UtcDate(DateTime localDate) =>
        new DateTimeOffset(DateTime.SpecifyKind(localDate.Date, DateTimeKind.Local).ToUniversalTime())
            .ToString("O", CultureInfo.InvariantCulture);

    public void DeleteRecord(long id)
    {
        using var db = Open();
        using var transaction = db.BeginTransaction();
        string? source = null;
        string? fingerprint = null;
        using (var read = db.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = "SELECT source, data FROM tests WHERE id=$id";
            read.Parameters.AddWithValue("$id", id);
            using var reader = read.ExecuteReader();
            if (reader.Read() && reader.GetString(0) != "simulation")
            {
                source = reader.GetString(0);
                fingerprint = (JsonSerializer.Deserialize<TestSnapshot>(reader.GetString(1))
                    ?? throw new InvalidDataException("历史记录为空，无法删除结果指纹。")).ArchiveFingerprint();
            }
        }

        using (var delete = db.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM tests WHERE id=$id";
            delete.Parameters.AddWithValue("$id", id);
            delete.ExecuteNonQuery();
        }

        if (source is not null && fingerprint is not null && !FingerprintStillUsed(db, transaction, source, fingerprint))
        {
            using var forget = db.CreateCommand();
            forget.Transaction = transaction;
            forget.CommandText = "DELETE FROM result_fingerprints WHERE source=$source AND fingerprint=$fingerprint";
            forget.Parameters.AddWithValue("$source", source);
            forget.Parameters.AddWithValue("$fingerprint", fingerprint);
            forget.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    private static bool FingerprintStillUsed(SqliteConnection db, SqliteTransaction transaction, string source, string fingerprint)
    {
        using var command = db.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT data FROM tests WHERE source=$source";
        command.Parameters.AddWithValue("$source", source);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var test = JsonSerializer.Deserialize<TestSnapshot>(reader.GetString(0));
            if (test?.ArchiveFingerprint() == fingerprint)
                return true;
        }
        return false;
    }

    public static void Export(string path, IEnumerable<TestRecord> records)
    {
        using var writer = new StreamWriter(path, false, new UTF8Encoding(true));
        writer.WriteLine("二维码,时间,正常电压,地刷灯亮判定,正常电流,正常电流判定结果,正常功率,正常功率判定结果,正常转速,正常转速判定结果,转速方向判定结果,地刷灯灭判定,灰尘测试电流,灰尘电流判定结果,灰尘测试功率,灰尘功率判定结果,灰尘测试转速,灰尘转速判定结果,总判定结果");
        static string? VerdictCell(Verdict? verdict) => verdict is Verdict value ? Texts.Verdict(value) : null;
        foreach (var record in records)
        {
            var test = record.Test;
            object?[] cells =
            [
                test.Barcode, record.DisplayTime,
                test.NormalVoltage, VerdictCell(test.BrushLightVerdict),
                test.NormalCurrent, VerdictCell(test.NormalCurrentVerdict),
                test.NormalPower, VerdictCell(test.NormalPowerVerdict),
                test.NormalSpeed, VerdictCell(test.NormalSpeedVerdict),
                VerdictCell(test.DirectionVerdict), VerdictCell(test.BrushLightOffVerdict),
                test.DustCurrent, VerdictCell(test.DustCurrentVerdict),
                test.DustPower, VerdictCell(test.DustPowerVerdict),
                test.DustSpeed, VerdictCell(test.DustSpeedVerdict),
                VerdictCell(test.Result)
            ];
            writer.WriteLine(string.Join(",", cells.Select(CsvCell)));
        }
    }

    private static string CsvCell(object? value)
    {
        string cell = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
        if (value is string && cell.TrimStart().FirstOrDefault() is '=' or '+' or '-' or '@')
            cell = "'" + cell;
        return "\"" + cell.Replace("\"", "\"\"") + "\"";
    }
}
