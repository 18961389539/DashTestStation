using System.Globalization;

namespace DustLoadScada;

public enum ManualPointId
{
    Power, Head, Lower, Slide, Start, Mode, Dust, Brush, Encoder, Scan, Mark, Photo, CameraRecipe
}

public sealed record ManualPoint(ushort WriteAddress, ushort FeedbackAddress);

internal static class ManualAddress
{
    public static string Format(ushort address, bool output) => output
        ? address >= ManualPoints.OutputBase ? $"Y{Convert.ToString(address - ManualPoints.OutputBase, 8)}" : address.ToString(CultureInfo.InvariantCulture)
        : address < 8000 ? $"M{address}" : address.ToString(CultureInfo.InvariantCulture);

    public static bool TryParse(string text, bool output, out ushort address)
    {
        text = text.Trim();
        if (text.StartsWith(output ? "Y" : "M", StringComparison.OrdinalIgnoreCase))
        {
            string digits = text[1..];
            if (!output)
                return ushort.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out address) && address < 8000;
            if (digits.Length is >= 1 and <= 4 && digits.All(digit => digit is >= '0' and <= '7')
                && Convert.ToInt32(digits, 8) <= 0x3FF)
            {
                address = (ushort)(ManualPoints.OutputBase + Convert.ToInt32(digits, 8));
                return true;
            }
            address = 0;
            return false;
        }
        return ushort.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out address);
    }
}

internal static class ManualPoints
{
    public const ushort OutputBase = 0xFC00;
    public static readonly string[] Labels =
    [
        "通电", "压头气缸", "上压气缸", "通电平移", "开关气缸", "模式气缸",
        "灰尘模拟气缸", "地刷通电气缸", "编码器气缸", "扫码启动", "打点气缸", "相机拍照", "相机方案切换"
    ];

    public static ManualPoint Default(ManualPointId id) =>
        new((ushort)(50 + (int)id), (ushort)(OutputBase + (int)id));

    public static Dictionary<ManualPointId, ManualPoint> Resolve(
        IReadOnlyDictionary<ManualPointId, ManualPoint>? overrides, IEnumerable<ushort> otherCommands)
    {
        var points = Enum.GetValues<ManualPointId>().ToDictionary(id => id,
            id => overrides?.GetValueOrDefault(id) ?? Default(id));
        var writes = points.Values.Select(point => point.WriteAddress).ToArray();
        var feedback = points.Values.Select(point => point.FeedbackAddress).ToArray();
        if (writes.Any(address => address >= OutputBase))
            throw new InvalidOperationException("手动写入地址不能位于汇川 Y 输出区。");
        if (writes.Distinct().Count() != writes.Length || writes.Intersect(otherCommands).Any())
            throw new InvalidOperationException("手动写入地址重复，或与其他命令线圈重叠。");
        if (writes.Intersect(feedback).Any() || otherCommands.Intersect(feedback).Any())
            throw new InvalidOperationException("命令线圈地址与只读反馈地址重叠。");
        return points;
    }
}
