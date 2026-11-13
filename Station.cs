using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using NModbus;

namespace DustLoadScada;

public sealed record PlcSettings
{
    private static readonly JsonSerializerOptions FileOptions = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };
    public string Host { get; init; } = "192.168.1.10";
    public int Port { get; init; } = 502;
    public byte UnitId { get; init; } = 1;
    public string StationId { get; init; } = "台架-01";
    public int PollMilliseconds { get; init; } = 300;
    public int TimeoutMilliseconds { get; init; } = 1200;
    public ushort TelemetryAddress { get; init; } = 0;
    public bool UseInputRegisters { get; init; }
    public bool LowWordFirst { get; init; }
    public ushort? RecipeAddress { get; init; }
    public Dictionary<string, ushort> Commands { get; init; } = new();

    public static PlcSettings Load(string path)
    {
        if (!File.Exists(path))
            throw new InvalidOperationException("请先将 plc.example.json 复制到数据目录，命名为 plc.json 并填写现场参数。");

        var settings = JsonSerializer.Deserialize<PlcSettings>(File.ReadAllText(path), FileOptions)
            ?? throw new InvalidOperationException("PLC 配置为空。");
        settings.Validate();
        return settings;
    }

    internal void Save(string path)
    {
        Validate();
        string temporary = path + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(this, FileOptions));
            File.Move(temporary, path, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(Host) || string.IsNullOrWhiteSpace(StationId)
            || Port is < 1 or > 65535 || PollMilliseconds is < 100 or > 5000
            || TimeoutMilliseconds is < 200 or > 10000
            || TelemetryAddress + PlcLayout.TelemetryWords > 65536)
            throw new InvalidOperationException("请检查 PLC 主机、端口、站点标识、超时及读取地址。");

        if (Commands is null || Commands.Keys.Except(StationActions.Keys).Any()
            || Commands.Values.Distinct().Count() != Commands.Count)
            throw new InvalidOperationException("PLC 命令名称无效或线圈地址重复。");

        if (RecipeAddress is ushort address &&
            (address + PlcLayout.RecipeWords > 65536 || (!UseInputRegisters
                && address < TelemetryAddress + PlcLayout.TelemetryWords
                && TelemetryAddress < address + PlcLayout.RecipeWords)))
            throw new InvalidOperationException("配方地址超出范围或与监控保持寄存器重叠。");
    }
}

public static class StationActions
{
    public static readonly (string Key, string Label, string Group)[] Devices =
    [
        ("power", "通电", "电源与夹具"),
        ("head", "压头气缸", "电源与夹具"),
        ("upper", "上压气缸", "电源与夹具"),
        ("slide", "通电平移", "电源与夹具"),
        ("switch", "开关气缸", "电源与夹具"),
        ("mode", "模式气缸", "负载与检测"),
        ("dust", "灰尘模拟气缸", "负载与检测"),
        ("brush", "地刷通电气缸", "负载与检测"),
        ("encoder", "编码器气缸", "负载与检测"),
        ("scan", "扫码启动", "负载与检测"),
        ("cameraOn", "相机灯灭亮开关", "检测开关"),
        ("cameraOff", "相机灯灭开关", "检测开关"),
        ("scanner", "扫码开关", "检测开关")
    ];

    public static IEnumerable<string> Keys => Devices.Select(item => item.Key).Append("reset");

    public static bool IsInspectionSwitch(string key) => key is "cameraOn" or "cameraOff" or "scanner";
    public static bool IsRequest(string key) => key == "reset" || IsInspectionSwitch(key);
}

public interface IStation : IDisposable
{
    string Source { get; }
    int PollMilliseconds { get; }
    bool CanApplyRecipe { get; }
    bool Supports(string command);
    Task<StationReading> ReadAsync(CancellationToken cancellationToken);
    Task ResetAsync();
    Task SetInspectionSwitchAsync(string key, bool enabled);
    Task<ushort> ReadManualAsync(CancellationToken cancellationToken);
    Task SetManualAsync(ManualPointId id, bool pressed);
    Task ReleaseManualAsync();
    Task ApplyAsync(Recipe recipe);
}

internal enum PlcTestArea { HoldingRegisters, InputRegisters, Coils, DiscreteInputs }

public sealed class ModbusStation : IStation
{
    private PlcSettings settings = null!;
    private MonitorReadPlan readPlan = null!;
    private RecipeWritePlan recipePlan = null!;
    private IReadOnlyDictionary<RecipePointId, ushort> recipeOverrides = null!;
    private Dictionary<ManualPointId, ManualPoint> manualPoints = null!;
    private IReadOnlyDictionary<string, InspectionPoint> inspectionOverrides = null!;
    private ushort? recordTrigger;
    private ushort[] feedbackAddresses = [];
    private readonly HashSet<ushort> heldAddresses = [];
    private readonly HashSet<ushort> pendingReleases = [];
    private readonly SemaphoreSlim bus = new(1, 1);
    private IModbusMaster? master;
    private TcpClient? client;

    internal ModbusStation() { }

    public ModbusStation(PlcSettings settings, IReadOnlyDictionary<MonitorPointId, MonitorPoint>? monitorPoints = null,
        IReadOnlyDictionary<ManualPointId, ManualPoint>? manualOverrides = null,
        IReadOnlyDictionary<RecipePointId, ushort>? recipePoints = null,
        IReadOnlyDictionary<string, InspectionPoint>? inspectionPoints = null) =>
        Configure(settings, monitorPoints, manualOverrides, recipePoints, inspectionPoints);

    // The application stops polling and flushes manual releases before reconfiguring the shared PLC.
    internal void Configure(PlcSettings configuration, IReadOnlyDictionary<MonitorPointId, MonitorPoint>? monitorPoints,
        IReadOnlyDictionary<ManualPointId, ManualPoint>? manualOverrides,
        IReadOnlyDictionary<RecipePointId, ushort>? recipePoints, IReadOnlyDictionary<string, InspectionPoint>? inspectionPoints,
        BarcodePoint? barcodePoint = null, ushort? recordTrigger = null)
    {
        var monitoring = new MonitorReadPlan(configuration, monitorPoints ?? new Dictionary<MonitorPointId, MonitorPoint>(), barcodePoint);
        var recipes = new Dictionary<RecipePointId, ushort>(recipePoints ?? new Dictionary<RecipePointId, ushort>());
        var writePlan = new RecipeWritePlan(configuration, recipes, monitoring);
        var inspection = new Dictionary<string, InspectionPoint>(inspectionPoints ?? new Dictionary<string, InspectionPoint>());
        var manual = InspectionPoints.Validate(configuration, inspection, manualOverrides, recordTrigger);

        settings = configuration;
        readPlan = monitoring;
        recipeOverrides = recipes;
        recipePlan = writePlan;
        inspectionOverrides = inspection;
        this.recordTrigger = recordTrigger;
        manualPoints = manual;
        feedbackAddresses = FeedbackAddresses(manualPoints);
    }

    internal void CloseConnection() => Disconnect();

    internal PlcSettings Settings => settings;

    public string Source => $"plc:{settings.StationId}";
    public int PollMilliseconds => settings.PollMilliseconds;
    public bool CanApplyRecipe => recipePlan.Configured;
    public bool Supports(string command) => StationActions.IsRequest(command)
        && InspectionPoints.Resolve(settings, inspectionOverrides, command).WriteAddress.HasValue;

    private static ushort[] FeedbackAddresses(Dictionary<ManualPointId, ManualPoint> points) =>
        points.Values.Select(point => point.FeedbackAddress).Distinct().Order().ToArray();

    public Task<ushort> ReadManualAsync(CancellationToken cancellationToken) => ExchangeAsync(connection =>
    {
        ClearPendingReleases(connection);
        var values = new Dictionary<ushort, bool>(feedbackAddresses.Length);
        for (int i = 0; i < feedbackAddresses.Length;)
        {
            ushort start = feedbackAddresses[i++];
            int count = 1;
            while (i < feedbackAddresses.Length && feedbackAddresses[i] == start + count)
            {
                i++;
                count++;
            }
            var states = connection.ReadCoils(settings.UnitId, start, (ushort)count);
            for (int bit = 0; bit < count; bit++)
                values.Add((ushort)(start + bit), states[bit]);
        }
        ushort feedback = 0;
        foreach (var (id, point) in manualPoints)
            if (values[point.FeedbackAddress])
                feedback |= (ushort)(1 << (int)id);
        return feedback;
    }, cancellationToken);

    public Task SetManualAsync(ManualPointId id, bool pressed)
    {
        return ExchangeAsync(connection =>
        {
            ushort address = manualPoints[id].WriteAddress;
            if (pressed)
            {
                ClearPendingReleases(connection);
                pendingReleases.Add(address);
                connection.WriteSingleCoil(settings.UnitId, address, true);
                pendingReleases.Remove(address);
                heldAddresses.Add(address);
            }
            else
            {
                connection.WriteSingleCoil(settings.UnitId, address, false);
                pendingReleases.Remove(address);
            }
            return true;
        }, CancellationToken.None, pressed ? null : () =>
        {
            ushort address = manualPoints[id].WriteAddress;
            heldAddresses.Remove(address);
            pendingReleases.Add(address);
        });
    }

    public async Task ReleaseManualAsync()
    {
        await bus.WaitAsync();
        bool hasPending = pendingReleases.Count != 0 || heldAddresses.Count != 0;
        bus.Release();
        if (hasPending)
            await ExchangeAsync(connection => { ClearPendingReleases(connection); return true; }, CancellationToken.None, () =>
            {
                pendingReleases.UnionWith(heldAddresses);
                heldAddresses.Clear();
            });
    }

    private void ClearPendingReleases(IModbusMaster connection)
    {
        foreach (ushort address in pendingReleases.ToArray())
        {
            connection.WriteSingleCoil(settings.UnitId, address, false);
            pendingReleases.Remove(address);
        }
    }

    public async Task UpdateManualPointsAsync(IReadOnlyDictionary<ManualPointId, ManualPoint> points, Action save)
    {
        await bus.WaitAsync();
        try
        {
            if (pendingReleases.Count != 0 || heldAddresses.Count != 0)
                throw new InvalidOperationException("有手动命令尚未清零，请等待 PLC 通讯恢复。");
            var resolved = InspectionPoints.Validate(settings, inspectionOverrides, points, recordTrigger);
            save();
            manualPoints = resolved;
            feedbackAddresses = FeedbackAddresses(resolved);
        }
        finally { bus.Release(); }
    }

    public async Task<StationReading> ReadAsync(CancellationToken cancellationToken) =>
        await ExchangeAsync(Read, cancellationToken);

    private StationReading Read(IModbusMaster connection)
    {
        var (test, feedback) = readPlan.Read((address, count) => ReadRegisters(connection, address, count),
            readHistoryOnCompletion: recordTrigger is null);
        ushort unknown = 0;
        for (int bit = 0; bit < StationActions.Devices.Length; bit++)
        {
            var key = StationActions.Devices[bit].Key;
            if (inspectionOverrides.GetValueOrDefault(key)?.FeedbackAddress is not ushort address)
                continue;
            ushort mask = (ushort)(1 << bit);
            try
            {
                bool enabled = connection.ReadCoils(settings.UnitId, address, 1)[0];
                feedback = enabled ? (ushort)(feedback | mask) : (ushort)(feedback & ~mask);
            }
            catch (SlaveException)
            {
                unknown |= mask;
            }
            catch (Exception ex) when (ex is IOException or SocketException or TimeoutException)
            {
                Disconnect();
                // The remaining explicit feedback points share the failed transport.
                for (int remaining = bit; remaining < StationActions.Devices.Length; remaining++)
                    if (inspectionOverrides.GetValueOrDefault(StationActions.Devices[remaining].Key)?.FeedbackAddress.HasValue == true)
                        unknown |= (ushort)(1 << remaining);
                break;
            }
        }
        return new StationReading(test, feedback, unknown);
    }

    public async Task UpdateInspectionPointsAsync(IReadOnlyDictionary<string, InspectionPoint> points, Action save)
    {
        await bus.WaitAsync();
        try
        {
            _ = InspectionPoints.Validate(settings, points, manualPoints, recordTrigger);
            save();
            inspectionOverrides = new Dictionary<string, InspectionPoint>(points);
        }
        finally { bus.Release(); }
    }

    private ushort[] ReadRegisters(IModbusMaster connection, ushort address, ushort count) =>
        settings.UseInputRegisters
            ? connection.ReadInputRegisters(settings.UnitId, address, count)
            : connection.ReadHoldingRegisters(settings.UnitId, address, count);

    internal Task<ushort[]> ReadTestAsync(PlcTestArea area, ushort address, ushort count)
    {
        if (count is < 1 or > 125 || address + count > 65536)
            throw new ArgumentOutOfRangeException(nameof(count), "读取数量须为 1–125，且不能超出地址范围。");

        return ExchangeAsync(connection => area switch
        {
            PlcTestArea.HoldingRegisters => connection.ReadHoldingRegisters(settings.UnitId, address, count),
            PlcTestArea.InputRegisters => connection.ReadInputRegisters(settings.UnitId, address, count),
            PlcTestArea.Coils => Array.ConvertAll(connection.ReadCoils(settings.UnitId, address, count), value => (ushort)(value ? 1 : 0)),
            PlcTestArea.DiscreteInputs => Array.ConvertAll(connection.ReadInputs(settings.UnitId, address, count), value => (ushort)(value ? 1 : 0)),
            _ => throw new ArgumentOutOfRangeException(nameof(area))
        }, CancellationToken.None);
    }

    internal Task WriteTestAsync(PlcTestArea area, ushort address, ushort value)
    {
        if (area is not (PlcTestArea.HoldingRegisters or PlcTestArea.Coils))
            throw new InvalidOperationException("此数据区只读。");
        if (area == PlcTestArea.Coils && value > 1)
            throw new ArgumentOutOfRangeException(nameof(value), "线圈值只能是 0 或 1。");

        return ExchangeAsync(connection =>
        {
            EnsureWritable(ReadIdentity(connection).Stage, false);
            if (area == PlcTestArea.Coils)
            {
                if (address == recordTrigger)
                    throw new InvalidOperationException("记录触发线圈由存档流程管理。");
                ClearPendingReleases(connection);
                if (heldAddresses.Contains(address))
                    throw new InvalidOperationException("该线圈正由手动按钮操作。");
                connection.WriteSingleCoil(settings.UnitId, address, value != 0);
            }
            else
                connection.WriteSingleRegister(settings.UnitId, address, value);
            return true;
        }, CancellationToken.None);
    }

    private (TestStage Stage, uint Cycle, ushort Flags) ReadIdentity(IModbusMaster connection) =>
        PlcLayout.ReadIdentity(ReadRegisters(connection, settings.TelemetryAddress, PlcLayout.TelemetryWords), settings.LowWordFirst);

    internal Task UpdateRecordTriggerAsync(ushort? address, Action save) => ExchangeAsync(connection =>
    {
        if (ReadIdentity(connection).Stage is TestStage.Normal or TestStage.Dust)
            throw new InvalidOperationException("PLC 正在测试，请在测试结束后修改点位。");

        _ = InspectionPoints.Validate(settings, inspectionOverrides, manualPoints, address);
        save();
        recordTrigger = address;
        return true;
    }, CancellationToken.None);

    internal Task<bool> ReadCoilAsync(ushort address, CancellationToken cancellationToken) =>
        ExchangeAsync(connection => connection.ReadCoils(settings.UnitId, address, 1)[0], cancellationToken);

    internal Task<bool?> ProcessRecordTriggerAsync(Func<TestSnapshot, bool> archive, CancellationToken cancellationToken) =>
        ExchangeAsync<bool?>(connection =>
        {
            if (recordTrigger is not ushort trigger || !connection.ReadCoils(settings.UnitId, trigger, 1)[0])
                return null;

            var (test, _) = readPlan.Read((address, count) => ReadRegisters(connection, address, count), triggered: true);
            bool saved;
            try { saved = archive(test); }
            catch (Exception ex) { throw new InvalidOperationException($"数据库保存失败。{ex.Message}", ex); }

            // A duplicate cycle is already committed, including when a previous acknowledgement failed.
            try { connection.WriteSingleCoil(settings.UnitId, trigger, false); }
            catch (Exception ex) { throw new IOException($"记录已保存，触发线圈清零失败。{ex.Message}", ex); }
            return saved;
        }, cancellationToken);

    public Task UpdateMonitorPointsAsync(IReadOnlyDictionary<MonitorPointId, MonitorPoint> points, Action save) =>
        UpdateReadPlanAsync(() => readPlan.WithPoints(points), save);

    internal Task UpdateBarcodePointAsync(BarcodePoint? point, Action save) =>
        UpdateReadPlanAsync(() => readPlan.WithBarcode(point), save);

    private Task UpdateReadPlanAsync(Func<MonitorReadPlan> createPlan, Action save) =>
        ExchangeAsync(connection =>
        {
            var state = ReadIdentity(connection);
            if (state.Stage is TestStage.Normal or TestStage.Dust)
                throw new InvalidOperationException("PLC 正在测试，请在测试结束后修改点位。");

            var next = createPlan();
            var updatedRecipePlan = new RecipeWritePlan(settings, recipeOverrides, next);
            save();
            readPlan = next;
            recipePlan = updatedRecipePlan;
            return true;
        }, CancellationToken.None);

    public async Task UpdateRecipePointsAsync(IReadOnlyDictionary<RecipePointId, ushort> points, Action save)
    {
        await bus.WaitAsync();
        try
        {
            var next = new RecipeWritePlan(settings, points, readPlan);
            save();
            recipeOverrides = new Dictionary<RecipePointId, ushort>(points);
            recipePlan = next;
        }
        finally { bus.Release(); }
    }

    public Task ResetAsync()
    {
        if (!Supports("reset"))
            throw new InvalidOperationException("PLC 未配置复位线圈。");

        return ExchangeAsync(connection =>
        {
            if (InspectionPoints.Resolve(settings, inspectionOverrides, "reset").WriteAddress is not ushort address)
                throw new InvalidOperationException("PLC 未配置复位线圈。");
            EnsureWritable(ReadIdentity(connection).Stage, true);
            if (recordTrigger is ushort trigger && connection.ReadCoils(settings.UnitId, trigger, 1)[0])
                throw new InvalidOperationException("PLC 正等待历史记录保存，请稍后复位。");
            if (connection.ReadCoils(settings.UnitId, address, 1)[0])
                throw new InvalidOperationException("PLC 尚未清除上一次命令，请检查程序或稍后重试。");
            connection.WriteSingleCoil(settings.UnitId, address, true);
            return true;
        }, CancellationToken.None);
    }

    public Task SetInspectionSwitchAsync(string key, bool enabled)
    {
        return ExchangeAsync(connection =>
        {
            if (!StationActions.IsInspectionSwitch(key)
                || InspectionPoints.Resolve(settings, inspectionOverrides, key).WriteAddress is not ushort address)
                throw new InvalidOperationException("该检测开关未配置写入线圈。");
            EnsureWritable(ReadIdentity(connection).Stage, false);
            connection.WriteSingleCoil(settings.UnitId, address, enabled);
            return true;
        }, CancellationToken.None);
    }

    public Task ApplyAsync(Recipe recipe)
    {
        if (!CanApplyRecipe)
            throw new InvalidOperationException("PLC 未配置配方写入地址。");

        return ExchangeAsync(connection =>
        {
            var writes = recipePlan.Encode(recipe);
            EnsureWritable(ReadIdentity(connection).Stage, false);
            foreach (var (address, words) in writes)
            {
                try { connection.WriteMultipleRegisters(settings.UnitId, address, words); }
                catch (Exception ex) when (writes.Count > 1)
                {
                    throw new IOException($"配方分段下发在地址 {address} 处未确认，PLC 可能已收到部分参数。请检查后重新配置。{ex.Message}", ex);
                }
            }
            return true;
        }, CancellationToken.None);
    }

    private static void EnsureWritable(TestStage stage, bool reset)
    {
        if (stage is TestStage.Normal or TestStage.Dust || (stage == TestStage.Fault && !reset))
            throw new InvalidOperationException("PLC 正在测试或处于故障状态，当前命令不可执行。");
    }

    private async Task<T> ExchangeAsync<T>(Func<IModbusMaster, T> action, CancellationToken cancellationToken,
        Action? beforeConnect = null)
    {
        await bus.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            beforeConnect?.Invoke();
            if (master is null)
            {
                client = new TcpClient { NoDelay = true };
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(settings.TimeoutMilliseconds);
                try { await client.ConnectAsync(settings.Host, settings.Port, timeout.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new TimeoutException("PLC 连接超时。");
                }
                master = new ModbusFactory().CreateMaster(client);
                master.Transport.ReadTimeout = settings.TimeoutMilliseconds;
                master.Transport.WriteTimeout = settings.TimeoutMilliseconds;
                master.Transport.Retries = 0;
            }
            return await Task.Run(() => action(master), cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            Disconnect();
            throw;
        }
        finally
        {
            bus.Release();
        }
    }

    private void Disconnect()
    {
        master?.Dispose();
        client?.Dispose();
        master = null;
        client = null;
    }

    public void Dispose()
    {
        Disconnect();
        bus.Dispose();
    }
}

public static class PlcLayout
{
    public const ushort TelemetryWords = 64;
    public const ushort RecipeWords = 64;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    private static uint Unsigned(ushort[] words, int offset, bool lowWordFirst) => lowWordFirst
        ? ((uint)words[offset + 1] << 16) | words[offset]
        : ((uint)words[offset] << 16) | words[offset + 1];

    internal static (TestStage Stage, uint Cycle, ushort Flags) ReadIdentity(ushort[] words, bool lowWordFirst)
    {
        if (words.Length != TelemetryWords || words[0] != 1 || words[1] > 4)
            throw new InvalidDataException("PLC 监控区的版本或状态值与配置协议不符。");
        return ((TestStage)words[1], Unsigned(words, 2, lowWordFirst), words[24]);
    }

    internal static (TestSnapshot Test, ushort Feedback) Decode(ushort[] words, bool lowWordFirst,
        Func<MonitorPointId, double?> readPoint, string barcode, bool triggered)
    {
        var (stage, cycle, flags) = ReadIdentity(words, lowWordFirst);
        if (triggered)
            stage = TestStage.Completed;
        Verdict ReadVerdict(MonitorPointId id, double? code) => code switch
        {
            0 => Verdict.Pending,
            1 => Verdict.Pass,
            2 => Verdict.Fail,
            _ => throw new InvalidDataException($"{MonitorPointDefinition.For(id).Label}返回了未知判定值。")
        };
        Verdict RequiredVerdict(MonitorPointId id) => ReadVerdict(id, readPoint(id));
        Verdict? OptionalVerdict(MonitorPointId id) => readPoint(id) is double code ? ReadVerdict(id, code) : null;

        bool hasNormal = stage is TestStage.Dust or TestStage.Completed || (stage == TestStage.Normal && (flags & 1) != 0);
        bool hasDust = stage == TestStage.Completed || (stage == TestStage.Dust && (flags & 2) != 0);
        var result = RequiredVerdict(MonitorPointId.Result);
        if (stage == TestStage.Completed && (cycle == 0 || result == Verdict.Pending))
            throw new InvalidDataException("PLC 存档数据须同时提供非零流水号和有效总判定。");

        return (new TestSnapshot
        {
            Stage = stage,
            CycleId = cycle,
            Elapsed = Unsigned(words, 4, lowWordFirst) / 1000d,
            Voltage = readPoint(MonitorPointId.Voltage),
            NormalVoltage = readPoint(MonitorPointId.NormalVoltage),
            NormalCurrent = readPoint(MonitorPointId.NormalCurrent),
            DustCurrent = readPoint(MonitorPointId.DustCurrent),
            NormalPower = hasNormal ? readPoint(MonitorPointId.NormalPower) : null,
            DustPower = hasDust ? readPoint(MonitorPointId.DustPower) : null,
            PowerDelta = hasNormal && hasDust ? readPoint(MonitorPointId.PowerDelta) : null,
            NormalSpeed = hasNormal ? readPoint(MonitorPointId.NormalSpeed) : null,
            DustSpeed = hasDust ? readPoint(MonitorPointId.DustSpeed) : null,
            SpeedDelta = hasNormal && hasDust ? readPoint(MonitorPointId.SpeedDelta) : null,
            PowerVerdict = RequiredVerdict(MonitorPointId.PowerVerdict),
            SpeedVerdict = RequiredVerdict(MonitorPointId.SpeedVerdict),
            Result = result,
            DirectionVerdict = RequiredVerdict(MonitorPointId.DirectionVerdict),
            BrushLightVerdict = RequiredVerdict(MonitorPointId.BrushLightVerdict),
            BrushLightOffVerdict = OptionalVerdict(MonitorPointId.BrushLightOffVerdict),
            NormalCurrentVerdict = OptionalVerdict(MonitorPointId.NormalCurrentVerdict),
            NormalPowerVerdict = OptionalVerdict(MonitorPointId.NormalPowerVerdict),
            NormalSpeedVerdict = OptionalVerdict(MonitorPointId.NormalSpeedVerdict),
            DustCurrentVerdict = OptionalVerdict(MonitorPointId.DustCurrentVerdict),
            DustPowerVerdict = OptionalVerdict(MonitorPointId.DustPowerVerdict),
            DustSpeedVerdict = OptionalVerdict(MonitorPointId.DustSpeedVerdict),
            Barcode = barcode,
            Model = ReadString(words.AsSpan(42, 16))
        }, words[58]);
    }

    public static ushort[] Encode(Recipe recipe, bool lowWordFirst)
    {
        var words = new ushort[RecipeWords];
        words[0] = 1;
        foreach (var point in RecipePoint.All.Values)
            point.Encode(recipe, words.AsSpan(point.Offset, point.WordCount), lowWordFirst);
        words[31] = (ushort)recipe.BarcodeStartIndex;
        WriteString(words, 32, 16, recipe.Model, "产品型号");
        words[48] = (ushort)recipe.BarcodeCompareLength;
        return words;
    }

    internal static string ReadString(ReadOnlySpan<ushort> words)
    {
        var bytes = new byte[words.Length * 2];
        for (int i = 0; i < words.Length; i++)
        {
            bytes[i * 2] = (byte)(words[i] >> 8);
            bytes[i * 2 + 1] = (byte)words[i];
        }
        int end = Array.IndexOf(bytes, (byte)0);
        return Utf8.GetString(bytes, 0, end < 0 ? bytes.Length : end);
    }

    private static void WriteString(ushort[] words, int offset, int length, string value, string label)
    {
        byte[] bytes = Utf8.GetBytes(value);
        if (bytes.Length > length * 2 || value.Contains('\0'))
            throw new InvalidOperationException($"{label}须在 {length * 2} 个 UTF-8 字节以内，且不能含 NUL。");
        for (int i = 0; i < bytes.Length; i++)
            words[offset + i / 2] |= (ushort)(bytes[i] << (i % 2 == 0 ? 8 : 0));
    }
}

public sealed class SimulationStation : IStation
{
    private readonly Stopwatch timer = new();
    private Recipe recipe = new();
    private TestSnapshot test = new();
    private uint cycle;
    private ushort feedback;
    private ushort manualFeedback;
    private bool fail;

    public string Source => "simulation";
    public int PollMilliseconds => 180;
    public bool CanApplyRecipe => true;
    public bool Supports(string command) => StationActions.IsRequest(command);

    public void Start(string barcode, bool forceFailure)
    {
        if (test.Running)
            throw new InvalidOperationException("当前模拟测试尚未完成。");
        if (barcode.Length == 0)
            barcode = $"DEMO-{DateTime.Now:MMddHHmmss}-{cycle + 1:000}";
        if (barcode.Length > 512)
            throw new InvalidOperationException("条码不能超过 512 个字符。");

        fail = forceFailure;
        test = new TestSnapshot { Stage = TestStage.Normal, Barcode = barcode, Model = recipe.Model, CycleId = ++cycle };
        timer.Restart();
    }

    public Task<StationReading> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (test.Running)
        {
            double elapsed = timer.Elapsed.TotalSeconds;
            double end = recipe.NormalSeconds + recipe.DustSeconds;
            bool completed = elapsed >= end;
            bool dust = elapsed >= recipe.NormalSeconds;
            const double voltage = 24.2;
            const double normalPower = 86.2;
            double powerDelta = fail ? 42.6 : 13.4;
            const double speedDelta = 215;
            var power = Check(powerDelta, recipe.PowerDeltaMin, recipe.PowerDeltaMax);
            var speed = Check(speedDelta, recipe.SpeedDeltaMin, recipe.SpeedDeltaMax);
            var light = fail ? Verdict.Fail : Verdict.Pass;
            var result = new[] { power, speed, light, Check(voltage, recipe.VoltageMin, recipe.VoltageMax) }
                .All(item => item == Verdict.Pass) ? Verdict.Pass : Verdict.Fail;
            test = test with
            {
                Stage = completed ? TestStage.Completed : dust ? TestStage.Dust : TestStage.Normal,
                Elapsed = Math.Min(elapsed, end),
                Voltage = voltage,
                NormalVoltage = completed ? voltage : null,
                NormalCurrent = completed ? Math.Round(normalPower / voltage, 2) : null,
                DustCurrent = completed ? Math.Round((normalPower + powerDelta) / voltage, 2) : null,
                NormalPower = dust ? normalPower : Math.Round(normalPower + Math.Sin(elapsed * 3), 2),
                DustPower = dust ? Math.Round(normalPower + powerDelta, 2) : null,
                PowerDelta = dust ? powerDelta : null,
                NormalSpeed = dust ? 2495 : 2495 + Math.Round(Math.Sin(elapsed * 3) * 15),
                DustSpeed = dust ? 2495 - speedDelta : null,
                SpeedDelta = dust ? speedDelta : null,
                PowerVerdict = completed ? power : Verdict.Pending,
                SpeedVerdict = completed ? speed : Verdict.Pending,
                DirectionVerdict = completed ? Verdict.Pass : Verdict.Pending,
                BrushLightVerdict = completed ? light : Verdict.Pending,
                BrushLightOffVerdict = completed ? Verdict.Pass : null,
                NormalCurrentVerdict = completed ? Verdict.Pass : null,
                NormalPowerVerdict = completed ? Verdict.Pass : null,
                NormalSpeedVerdict = completed ? Verdict.Pass : null,
                DustCurrentVerdict = completed ? Verdict.Pass : null,
                DustPowerVerdict = completed ? (fail ? Verdict.Fail : Verdict.Pass) : null,
                DustSpeedVerdict = completed ? Verdict.Pass : null,
                Result = completed ? result : Verdict.Pending
            };
            if (completed)
                timer.Stop();
        }
        return Task.FromResult(new StationReading(test, feedback));
    }

    public Task ResetAsync()
    {
        if (test.Running)
            throw new InvalidOperationException("测试期间无法执行手动命令。");
        test = new TestSnapshot();
        feedback = 0;
        manualFeedback = 0;
        return Task.CompletedTask;
    }

    public Task SetInspectionSwitchAsync(string key, bool enabled)
    {
        if (test.Running)
            throw new InvalidOperationException("测试期间无法操作检测开关。");
        if (!StationActions.IsInspectionSwitch(key))
            throw new InvalidOperationException("未知的检测开关。");

        int bit = Array.FindIndex(StationActions.Devices, item => item.Key == key);
        ushort mask = (ushort)(1 << bit);
        feedback = enabled ? (ushort)(feedback | mask) : (ushort)(feedback & ~mask);
        return Task.CompletedTask;
    }

    public Task<ushort> ReadManualAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(manualFeedback);
    }

    public Task SetManualAsync(ManualPointId id, bool pressed)
    {
        if (pressed && test.Running)
            throw new InvalidOperationException("测试期间无法执行手动命令。");
        ushort bit = (ushort)(1 << (int)id);
        manualFeedback = pressed ? (ushort)(manualFeedback | bit) : (ushort)(manualFeedback & ~bit);
        return Task.CompletedTask;
    }

    public Task ReleaseManualAsync()
    {
        manualFeedback = 0;
        return Task.CompletedTask;
    }

    public Task ApplyAsync(Recipe updated)
    {
        if (test.Running)
            throw new InvalidOperationException("测试期间无法切换配方。");
        recipe = updated with { };
        return Task.CompletedTask;
    }

    public void Dispose() => timer.Stop();

    private static Verdict Check(double value, double min, double max) =>
        value >= min && value <= max ? Verdict.Pass : Verdict.Fail;
}
