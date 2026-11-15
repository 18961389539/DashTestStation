namespace DustLoadScada;

public enum RecipePointId
{
    VoltageMin, VoltageMax, PowerDeltaMin, PowerDeltaMax, SpeedDeltaMin, SpeedDeltaMax,
    NormalSeconds, DustSeconds, PowerActionSeconds, GearActionSeconds, PowerIntervalSeconds,
    GearIntervalSeconds, PowerCycles, GearCycles, AlarmSeconds, IndicatorOffSeconds,
    NormalCurrentMin, NormalCurrentMax, NormalPowerMin, NormalPowerMax, NormalSpeedMin, NormalSpeedMax,
    DustCurrentMin, DustCurrentMax, DustPowerMin, DustPowerMax, DustSpeedMin, DustSpeedMax
}

internal sealed record RecipePoint(string Label, int Offset, Func<Recipe, double> Value, int Scale = 1, int WordCount = 2, bool IsFloat = false, ushort? Absolute = null)
{
    // 绝对默认地址来自通讯.xlsx 点表（HD 区）；电压上下限与报警时间点表未提供，暂用空闲 HD12/14/16，需 PLC 侧读取。
    public static readonly IReadOnlyDictionary<RecipePointId, RecipePoint> All = new Dictionary<RecipePointId, RecipePoint>
    {
        [RecipePointId.VoltageMin] = new("电压下限", 1, r => r.VoltageMin, IsFloat: true, Absolute: PlcAddress.HDBase + 12),
        [RecipePointId.VoltageMax] = new("电压上限", 3, r => r.VoltageMax, IsFloat: true, Absolute: PlcAddress.HDBase + 14),
        [RecipePointId.PowerDeltaMin] = new("功率差下限", 5, r => r.PowerDeltaMin, IsFloat: true, Absolute: PlcAddress.HDBase + 32),
        [RecipePointId.PowerDeltaMax] = new("功率差上限", 7, r => r.PowerDeltaMax, IsFloat: true, Absolute: PlcAddress.HDBase + 30),
        [RecipePointId.SpeedDeltaMin] = new("转速差下限", 9, r => r.SpeedDeltaMin, IsFloat: true, Absolute: PlcAddress.HDBase + 36),
        [RecipePointId.SpeedDeltaMax] = new("转速差上限", 11, r => r.SpeedDeltaMax, IsFloat: true, Absolute: PlcAddress.HDBase + 34),
        [RecipePointId.NormalSeconds] = new("正常测试时间", 13, r => r.NormalSeconds, 10, Absolute: PlcAddress.HDBase + 100),
        [RecipePointId.DustSeconds] = new("灰尘测试时间", 15, r => r.DustSeconds, 10, Absolute: PlcAddress.HDBase + 102),
        [RecipePointId.PowerActionSeconds] = new("开机动作时长", 17, r => r.PowerActionSeconds, 10, WordCount: 1, Absolute: PlcAddress.HDBase + 2),
        [RecipePointId.GearActionSeconds] = new("档位动作时长", 19, r => r.GearActionSeconds, 10, WordCount: 1, Absolute: PlcAddress.HDBase + 4),
        [RecipePointId.PowerIntervalSeconds] = new("开机间隔时间", 21, r => r.PowerIntervalSeconds, 10, WordCount: 1, Absolute: PlcAddress.HDBase + 3),
        [RecipePointId.GearIntervalSeconds] = new("档位间隔时间", 23, r => r.GearIntervalSeconds, 10, WordCount: 1, Absolute: PlcAddress.HDBase + 5),
        [RecipePointId.PowerCycles] = new("开机次数", 25, r => r.PowerCycles, WordCount: 1, Absolute: PlcAddress.HDBase + 0),
        [RecipePointId.GearCycles] = new("档位次数", 26, r => r.GearCycles, WordCount: 1, Absolute: PlcAddress.HDBase + 1),
        [RecipePointId.AlarmSeconds] = new("报警时间", 27, r => r.AlarmSeconds, 10, Absolute: PlcAddress.HDBase + 16),
        [RecipePointId.IndicatorOffSeconds] = new("跑马灯灭时间设定", 29, r => r.IndicatorOffSeconds, 10, Absolute: PlcAddress.HDBase + 10),
        [RecipePointId.NormalCurrentMax] = new("正常电流上限设定", 49, r => r.NormalCurrentMax, IsFloat: true, Absolute: PlcAddress.HDBase + 200),
        [RecipePointId.NormalCurrentMin] = new("正常电流下限设定", 51, r => r.NormalCurrentMin, IsFloat: true, Absolute: PlcAddress.HDBase + 202),
        [RecipePointId.NormalPowerMax] = new("正常功率上限设定", 53, r => r.NormalPowerMax, IsFloat: true, Absolute: PlcAddress.HDBase + 204),
        [RecipePointId.NormalPowerMin] = new("正常功率下限设定", 55, r => r.NormalPowerMin, IsFloat: true, Absolute: PlcAddress.HDBase + 206),
        [RecipePointId.NormalSpeedMax] = new("正常转速上限设定", 57, r => r.NormalSpeedMax, IsFloat: true, Absolute: PlcAddress.HDBase + 208),
        [RecipePointId.NormalSpeedMin] = new("正常转速下限设定", 59, r => r.NormalSpeedMin, IsFloat: true, Absolute: PlcAddress.HDBase + 210),
        [RecipePointId.DustCurrentMax] = new("灰尘感应电流上限设定", 61, r => r.DustCurrentMax, IsFloat: true, Absolute: PlcAddress.HDBase + 220),
        [RecipePointId.DustCurrentMin] = new("灰尘感应电流下限设定", 63, r => r.DustCurrentMin, IsFloat: true, Absolute: PlcAddress.HDBase + 222),
        [RecipePointId.DustPowerMax] = new("灰尘感应功率上限设定", 65, r => r.DustPowerMax, IsFloat: true, Absolute: PlcAddress.HDBase + 224),
        [RecipePointId.DustPowerMin] = new("灰尘感应功率下限设定", 67, r => r.DustPowerMin, IsFloat: true, Absolute: PlcAddress.HDBase + 226),
        [RecipePointId.DustSpeedMax] = new("灰尘感应转速上限设定", 69, r => r.DustSpeedMax, IsFloat: true, Absolute: PlcAddress.HDBase + 228),
        [RecipePointId.DustSpeedMin] = new("灰尘感应转速下限设定", 71, r => r.DustSpeedMin, IsFloat: true, Absolute: PlcAddress.HDBase + 230)
    };

    public ushort? DefaultAddress(PlcSettings settings) => settings.RecipeAddress is ushort start
        ? checked((ushort)(start + Offset)) : Absolute;

    // 工程值 = 原始值 × scale；代码里的 Scale 是「原始值 = 工程值 × Scale」，故默认 scale 为其倒数。
    public double DefaultScale => 1.0 / Scale;

    public RegisterDataType DefaultType => IsFloat ? RegisterDataType.Float32
        : WordCount == 1 ? RegisterDataType.UInt16 : RegisterDataType.Int32;

    public static int WordsFor(RegisterDataType type) =>
        type is RegisterDataType.Int16 or RegisterDataType.UInt16 ? 1 : 2;

    public void Encode(Recipe recipe, Span<ushort> words, bool lowWordFirst, double? scale = null, RegisterDataType? type = null)
    {
        var dataType = type ?? DefaultType;
        if (dataType == RegisterDataType.Float32)
        {
            float single = (float)Value(recipe);
            if (!float.IsFinite(single))
                throw new InvalidOperationException($"{Label}超出 Float32 范围。");
            uint floatBits = BitConverter.SingleToUInt32Bits(single);
            words[lowWordFirst ? 1 : 0] = (ushort)(floatBits >> 16);
            words[lowWordFirst ? 0 : 1] = (ushort)floatBits;
            return;
        }
        double divisor = scale ?? DefaultScale;
        double value = Math.Round(Value(recipe) / divisor, MidpointRounding.AwayFromZero);
        if (WordsFor(dataType) == 1)
        {
            words[0] = checked((ushort)value);
            return;
        }
        uint bits = unchecked((uint)checked((int)value));
        words[lowWordFirst ? 1 : 0] = (ushort)(bits >> 16);
        words[lowWordFirst ? 0 : 1] = (ushort)bits;
    }
}

internal sealed class RecipeWritePlan(PlcSettings settings, IReadOnlyDictionary<RecipePointId, ushort> overrides,
    IReadOnlyDictionary<RecipePointId, double>? scales = null,
    IReadOnlyDictionary<RecipePointId, RegisterDataType>? types = null)
{
    private readonly IReadOnlyDictionary<RecipePointId, ushort> addresses = Validate(settings, overrides, types);
    private readonly IReadOnlyDictionary<RecipePointId, double> pointScales = scales ?? new Dictionary<RecipePointId, double>();
    private readonly IReadOnlyDictionary<RecipePointId, RegisterDataType> pointTypes = types ?? new Dictionary<RecipePointId, RegisterDataType>();
    public bool Configured => addresses.Count == RecipePoint.All.Count;

    private RegisterDataType TypeFor(RecipePointId id) => pointTypes.TryGetValue(id, out var type)
        ? type : RecipePoint.All[id].DefaultType;

    private int WordsFor(RecipePointId id) => RecipePoint.WordsFor(TypeFor(id));

    private double ScaleFor(RecipePointId id) => TypeFor(id) == RegisterDataType.Float32 ? 1
        : pointScales.TryGetValue(id, out double scale) ? scale : RecipePoint.All[id].DefaultScale;

    private static Dictionary<RecipePointId, ushort> Validate(PlcSettings settings,
        IReadOnlyDictionary<RecipePointId, ushort> overrides,
        IReadOnlyDictionary<RecipePointId, RegisterDataType>? types)
    {
        int Words(RecipePointId id) => RecipePoint.WordsFor(types is not null && types.TryGetValue(id, out var type)
            ? type : RecipePoint.All[id].DefaultType);
        var result = new Dictionary<RecipePointId, ushort>();
        var used = new HashSet<int>();
        if (settings.RecipeAddress is ushort start)
            for (int word = start; word < start + PlcLayout.RecipeWords; word++)
                used.Add(word);
        foreach (var (id, point) in RecipePoint.All)
        {
            ushort? effective = overrides.TryGetValue(id, out ushort configured) ? configured : point.DefaultAddress(settings);
            if (effective is not ushort address)
                continue;
            int end = address + Words(id);
            if (end > 65536)
                throw new InvalidOperationException($"{point.Label}的地址超出寄存器范围。");
            bool inBlock = settings.RecipeAddress is ushort blockStart
                && address >= blockStart && end <= blockStart + PlcLayout.RecipeWords;
            // 同页查重：配方参数之间不得共用寄存器；跨页面（监控/扫码写回等）允许重复。
            if (!inBlock)
                for (int word = address; word < end; word++)
                    if (!used.Add(word))
                        throw new InvalidOperationException($"{point.Label}的地址与其他配方参数或保留寄存器重叠。");
            result.Add(id, address);
        }
        return result;
    }

    public List<(ushort Address, ushort[] Words)> Encode(Recipe recipe)
    {
        var registers = new SortedDictionary<int, ushort>();
        if (settings.RecipeAddress is ushort baseAddress)
        {
            var block = PlcLayout.Encode(recipe, settings.LowWordFirst, pointScales, pointTypes);
            for (int i = 0; i < block.Length; i++)
                registers.Add(baseAddress + i, block[i]);
            foreach (var (id, point) in RecipePoint.All)
            {
                if (!addresses.TryGetValue(id, out ushort address)
                    || (address >= baseAddress && address + WordsFor(id) <= baseAddress + PlcLayout.RecipeWords))
                    continue;
                for (int i = 0; i < WordsFor(id); i++)
                    registers.Remove(baseAddress + point.Offset + i);
            }
        }

        Span<ushort> words = stackalloc ushort[2];
        foreach (var (id, address) in addresses)
        {
            if (settings.RecipeAddress is ushort start && address >= start && address + WordsFor(id) <= start + PlcLayout.RecipeWords)
                continue;
            var point = RecipePoint.All[id];
            point.Encode(recipe, words, settings.LowWordFirst, ScaleFor(id), TypeFor(id));
            for (int i = 0; i < WordsFor(id); i++)
                registers.Add(address + i, words[i]);
        }

        var writes = new List<(ushort Address, ushort[] Words)>();
        var batch = new List<ushort>();
        int first = 0;
        foreach (var (address, word) in registers)
        {
            if (batch.Count != 0 && address != first + batch.Count)
            {
                writes.Add(((ushort)first, batch.ToArray()));
                batch.Clear();
            }
            if (batch.Count == 0)
                first = address;
            batch.Add(word);
        }
        if (batch.Count != 0)
            writes.Add(((ushort)first, batch.ToArray()));
        return writes;
    }
}
