using System.Globalization;

namespace DustLoadScada;

/// <summary>
/// 信捷 XD3 软元件与 Modbus 协议地址互转。X/Y 编号为八进制。
/// 映射来自 EMQX Neuron 信捷驱动文档与 HslCommunication XinJEHelper，接入前应以信捷官方通信手册核对。
/// </summary>
internal static class PlcAddress
{
    private sealed record Area(string Prefix, int Base, int First, int Count, bool Octal)
    {
        public bool ContainsDevice(int number) => number >= First && number < First + Count;
        public bool ContainsAddress(int address) => address >= Base && address < Base + Count;
    }

    public const ushort XBase = 0x5000;
    public const ushort YBase = 0x6000;
    public const ushort HDBase = 0xA080;
    public const ushort HMBase = 0xC100;

    private static readonly Area[] Coils =
    [
        new("M", 0x0000, 0, 8000, false),
        new("X", XBase, 0, 64, true),
        new("X", 0x5100, 0x1000, 640, true),
        new("X", 0x58D0, 0x2000, 128, true),
        new("X", 0x5BF0, 0x3000, 64, true),
        new("Y", YBase, 0, 64, true),
        new("Y", 0x6100, 0x1000, 640, true),
        new("Y", 0x68D0, 0x2000, 128, true),
        new("Y", 0x6BF0, 0x3000, 64, true),
        new("S", 0x7000, 0, 1024, false),
        new("HM", HMBase, 0, 960, false)
    ];

    private static readonly Area[] Registers =
    [
        new("D", 0x0000, 0, 8000, false),
        new("HD", HDBase, 0, 1000, false),
        new("HM", HMBase, 0, 960, false)
    ];

    public static bool TryParseCoil(string text, out ushort address) => TryParse(text, Coils, out address);

    public static bool TryParseRegister(string text, out ushort address) => TryParse(text, Registers, out address);

    public static string FormatCoil(ushort address) => Format(address, Coils);

    public static string FormatRegister(ushort address) => Format(address, Registers);

    public static string FormatRegisterRange(int start, int count) => count <= 1
        ? FormatRegister((ushort)start)
        : $"{FormatRegister((ushort)start)}–{FormatRegister((ushort)(start + count - 1))}";

    /// <summary>X 输入区或 Y 输出区，上位机不得写入。</summary>
    public static bool IsPhysicalIo(ushort address) =>
        Coils.Any(area => area.Prefix is "X" or "Y" && area.ContainsAddress(address));

    private static bool TryParse(string text, Area[] areas, out ushort address)
    {
        address = 0;
        text = text.Trim().ToUpperInvariant();
        if (text.Length == 0)
            return false;
        if (char.IsDigit(text[0]))
            return ushort.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out address);

        foreach (var group in areas.GroupBy(area => area.Prefix).OrderByDescending(group => group.Key.Length))
        {
            if (!text.StartsWith(group.Key, StringComparison.Ordinal))
                continue;
            string digits = text[group.Key.Length..];
            bool octal = group.First().Octal;
            if (digits.Length is 0 or > 6 || !digits.All(digit => octal ? digit is >= '0' and <= '7' : char.IsAsciiDigit(digit)))
                return false;
            int number = Convert.ToInt32(digits, octal ? 8 : 10);
            var area = group.FirstOrDefault(item => item.ContainsDevice(number));
            if (area is null)
                return false;
            address = (ushort)(area.Base + number - area.First);
            return true;
        }
        return false;
    }

    private static string Format(ushort address, Area[] areas)
    {
        var area = areas.FirstOrDefault(item => item.ContainsAddress(address));
        if (area is null)
            return address.ToString(CultureInfo.InvariantCulture);
        int number = area.First + address - area.Base;
        return area.Prefix + (area.Octal ? Convert.ToString(number, 8) : number.ToString(CultureInfo.InvariantCulture));
    }
}
