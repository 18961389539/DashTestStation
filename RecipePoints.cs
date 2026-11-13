namespace DustLoadScada;

public enum RecipePointId
{
    VoltageMin, VoltageMax, PowerDeltaMin, PowerDeltaMax, SpeedDeltaMin, SpeedDeltaMax,
    NormalSeconds, DustSeconds, PowerActionSeconds, GearActionSeconds, PowerIntervalSeconds,
    GearIntervalSeconds, PowerCycles, GearCycles, AlarmSeconds, IndicatorOffSeconds
}

internal sealed record RecipePoint(string Label, int Offset, Func<Recipe, double> Value, int Scale = 1, int WordCount = 2, bool IsFloat = false)
{
    public static readonly IReadOnlyDictionary<RecipePointId, RecipePoint> All = new Dictionary<RecipePointId, RecipePoint>
    {
        [RecipePointId.VoltageMin] = new("电压下限", 1, r => r.VoltageMin, IsFloat: true),
        [RecipePointId.VoltageMax] = new("电压上限", 3, r => r.VoltageMax, IsFloat: true),
        [RecipePointId.PowerDeltaMin] = new("功率差下限", 5, r => r.PowerDeltaMin, IsFloat: true),
        [RecipePointId.PowerDeltaMax] = new("功率差上限", 7, r => r.PowerDeltaMax, IsFloat: true),
        [RecipePointId.SpeedDeltaMin] = new("转速差下限", 9, r => r.SpeedDeltaMin),
        [RecipePointId.SpeedDeltaMax] = new("转速差上限", 11, r => r.SpeedDeltaMax),
        [RecipePointId.NormalSeconds] = new("正常测试时间", 13, r => r.NormalSeconds),
        [RecipePointId.DustSeconds] = new("灰尘测试时间", 15, r => r.DustSeconds),
        [RecipePointId.PowerActionSeconds] = new("开机动作时长", 17, r => r.PowerActionSeconds, 10),
        [RecipePointId.GearActionSeconds] = new("档位动作时长", 19, r => r.GearActionSeconds, 10),
        [RecipePointId.PowerIntervalSeconds] = new("开机间隔时间", 21, r => r.PowerIntervalSeconds, 10),
        [RecipePointId.GearIntervalSeconds] = new("档位间隔时间", 23, r => r.GearIntervalSeconds, 10),
        [RecipePointId.PowerCycles] = new("开机次数", 25, r => r.PowerCycles, WordCount: 1),
        [RecipePointId.GearCycles] = new("档位次数", 26, r => r.GearCycles, WordCount: 1),
        [RecipePointId.AlarmSeconds] = new("报警时间", 27, r => r.AlarmSeconds, 10),
        [RecipePointId.IndicatorOffSeconds] = new("跑马灯灭时间设定", 29, r => r.IndicatorOffSeconds, 10)
    };

    public ushort? DefaultAddress(PlcSettings settings) => settings.RecipeAddress is ushort start
        ? checked((ushort)(start + Offset)) : null;

    public void Encode(Recipe recipe, Span<ushort> words, bool lowWordFirst)
    {
        if (IsFloat)
        {
            float single = (float)(Value(recipe) * Scale);
            if (!float.IsFinite(single))
                throw new InvalidOperationException($"{Label}超出 Float32 范围。");
            uint floatBits = BitConverter.SingleToUInt32Bits(single);
            words[lowWordFirst ? 1 : 0] = (ushort)(floatBits >> 16);
            words[lowWordFirst ? 0 : 1] = (ushort)floatBits;
            return;
        }
        double value = Math.Round(Value(recipe) * Scale, MidpointRounding.AwayFromZero);
        if (WordCount == 1)
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
    MonitorReadPlan monitoring)
{
    private readonly IReadOnlyDictionary<RecipePointId, ushort> addresses = Validate(settings, overrides, monitoring);
    public bool Configured => settings.RecipeAddress.HasValue || addresses.Count == RecipePoint.All.Count;

    private static Dictionary<RecipePointId, ushort> Validate(PlcSettings settings,
        IReadOnlyDictionary<RecipePointId, ushort> overrides, MonitorReadPlan monitoring)
    {
        var result = new Dictionary<RecipePointId, ushort>();
        var used = new HashSet<int>();
        if (settings.RecipeAddress is ushort start)
        {
            used.Add(start);
            for (int offset = 31; offset < PlcLayout.RecipeWords; offset++)
                used.Add(start + offset);
            foreach (var (id, point) in RecipePoint.All)
            {
                ushort original = point.DefaultAddress(settings)!.Value;
                if (!overrides.TryGetValue(id, out ushort address) || address == original)
                    for (int word = original; word < original + point.WordCount; word++)
                        used.Add(word);
            }
        }
        foreach (var (id, point) in RecipePoint.All)
        {
            if (!overrides.TryGetValue(id, out ushort address) || address == point.DefaultAddress(settings))
                continue;
            int end = address + point.WordCount;
            if (end > 65536)
                throw new InvalidOperationException($"{point.Label}的地址超出寄存器范围。");
            if (monitoring.OverlapsHoldingRegisters(address, end))
                throw new InvalidOperationException($"{point.Label}的地址与监控保持寄存器重叠。");
            for (int word = address; word < end; word++)
                if (!used.Add(word))
                    throw new InvalidOperationException($"{point.Label}的地址与其他配方参数或保留寄存器重叠。");
            result.Add(id, address);
        }
        return result;
    }

    public List<(ushort Address, ushort[] Words)> Encode(Recipe recipe)
    {
        if (settings.RecipeAddress is ushort start && addresses.Count == 0)
            return [(start, PlcLayout.Encode(recipe, settings.LowWordFirst))];

        var registers = new SortedDictionary<int, ushort>();
        if (settings.RecipeAddress is ushort baseAddress)
        {
            var block = PlcLayout.Encode(recipe, settings.LowWordFirst);
            for (int i = 0; i < block.Length; i++)
                registers.Add(baseAddress + i, block[i]);
            foreach (var id in addresses.Keys)
            {
                var point = RecipePoint.All[id];
                for (int i = 0; i < point.WordCount; i++)
                    registers.Remove(baseAddress + point.Offset + i);
            }
        }

        Span<ushort> words = stackalloc ushort[2];
        foreach (var (id, address) in addresses)
        {
            var point = RecipePoint.All[id];
            point.Encode(recipe, words, settings.LowWordFirst);
            for (int i = 0; i < point.WordCount; i++)
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
