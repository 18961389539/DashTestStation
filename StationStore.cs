using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;

namespace DustLoadScada;

public sealed class StationStore
{
    private const int PasswordIterations = 600_000;
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
            CREATE TABLE IF NOT EXISTS users (
                name TEXT PRIMARY KEY COLLATE NOCASE, role INTEGER NOT NULL, enabled INTEGER NOT NULL,
                salt BLOB NOT NULL, hash BLOB NOT NULL
            );
            CREATE TABLE IF NOT EXISTS tests (
                id INTEGER PRIMARY KEY AUTOINCREMENT, source TEXT NOT NULL, cycle_id INTEGER NOT NULL,
                utc TEXT NOT NULL, barcode TEXT NOT NULL, data TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS plc_cycles (
                source TEXT NOT NULL, cycle_id INTEGER NOT NULL, PRIMARY KEY (source, cycle_id)
            );
            CREATE INDEX IF NOT EXISTS ix_tests_utc ON tests(utc);
            """;
        command.ExecuteNonQuery();
        if (Recipes().Count == 0)
            SaveRecipe(new Recipe(), true);
    }

    private SqliteConnection Open()
    {
        var db = new SqliteConnection(connectionString);
        db.Open();
        return db;
    }

    public Dictionary<MonitorPointId, MonitorPoint> LoadMonitorPoints() => LoadPoints<MonitorPointId, MonitorPoint>("monitor_points");
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
        command.Parameters.AddWithValue("$cycle", (long)test.CycleId);
        if (source != "simulation")
        {
            if (test.CycleId == 0)
                throw new InvalidOperationException("PLC 已完成测试，但流水号为 0，无法存档。");
            command.CommandText = "INSERT OR IGNORE INTO plc_cycles VALUES ($source, $cycle)";
            if (command.ExecuteNonQuery() == 0)
                return false;
        }

        command.CommandText = "INSERT INTO tests(source, cycle_id, utc, barcode, data) VALUES ($source, $cycle, $utc, $barcode, $data)";
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
        using var command = db.CreateCommand();
        command.CommandText = "DELETE FROM tests WHERE id=$id";
        command.Parameters.AddWithValue("$id", id);
        command.ExecuteNonQuery();
    }

    public List<UserAccount> Users()
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT name, role, enabled FROM users ORDER BY name";
        using var reader = command.ExecuteReader();
        var users = new List<UserAccount>();
        while (reader.Read())
            users.Add(new UserAccount(reader.GetString(0), (UserRole)reader.GetInt32(1), reader.GetBoolean(2)));
        return users;
    }

    public UserAccount? Authenticate(string name, string password)
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT name, role, salt, hash FROM users WHERE name=$name AND enabled=1";
        command.Parameters.AddWithValue("$name", name.Trim());
        using var reader = command.ExecuteReader();
        if (!reader.Read() || !CryptographicOperations.FixedTimeEquals(
            (byte[])reader[3], PasswordHash(password, (byte[])reader[2])))
            return null;
        return new UserAccount(reader.GetString(0), (UserRole)reader.GetInt32(1), true);
    }

    public void CreateUser(string name, string password, UserRole role, bool firstAccount)
    {
        name = name.Trim();
        if (name.Length is < 1 or > 50 || password.Length is < 8 or > 128)
            throw new InvalidOperationException("用户名须为 1–50 个字符，密码须为 8–128 个字符。");
        var salt = RandomNumberGenerator.GetBytes(16);
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = firstAccount
            ? "INSERT INTO users SELECT $name, $role, 1, $salt, $hash WHERE NOT EXISTS (SELECT 1 FROM users)"
            : "INSERT INTO users VALUES ($name, $role, 1, $salt, $hash)";
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$role", (int)role);
        command.Parameters.AddWithValue("$salt", salt);
        command.Parameters.AddWithValue("$hash", PasswordHash(password, salt));
        try
        {
            if (command.ExecuteNonQuery() != 1)
                throw new InvalidOperationException("负责人账号已初始化，请登录。");
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            throw new InvalidOperationException("用户名已存在。", ex);
        }
    }

    public void ToggleUser(UserAccount user)
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = """
            UPDATE users SET enabled=1-enabled WHERE name=$name AND
                (enabled=0 OR role<>1 OR (SELECT COUNT(*) FROM users WHERE role=1 AND enabled=1)>1)
            """;
        command.Parameters.AddWithValue("$name", user.Name);
        if (command.ExecuteNonQuery() == 0)
            throw new InvalidOperationException("至少保留一个启用的负责人账号。");
    }

    private static byte[] PasswordHash(string password, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(password, salt, PasswordIterations, HashAlgorithmName.SHA256, 32);

    public static void Export(string path, IEnumerable<TestRecord> records)
    {
        using var writer = new StreamWriter(path, false, new UTF8Encoding(true));
        writer.WriteLine("序号,时间,来源,扫码信息,产品型号,电压(V),正常功率(W),灰尘功率(W),功率差(W),正常转速(r/min),灰尘转速(r/min),转速差(r/min),功率判定,转速判定,方向判定,地刷灯判定,总判定,用时(s),测试流水号");
        foreach (var record in records)
        {
            var test = record.Test;
            object?[] cells = [record.Id, record.DisplayTime, record.SourceText, record.Test.Barcode, record.Test.Model,
                test.Voltage, test.NormalPower, test.DustPower, test.PowerDelta, test.NormalSpeed, test.DustSpeed, test.SpeedDelta,
                Texts.Verdict(test.PowerVerdict), Texts.Verdict(test.SpeedVerdict), Texts.Verdict(test.DirectionVerdict),
                Texts.Verdict(test.BrushLightVerdict), Texts.Verdict(test.Result), test.Elapsed, test.CycleId];
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
