using System.Globalization;
using System.Text;

namespace DustLoadScada;

internal sealed record ScannerTriggerSettings(
    ushort? TriggerAddress = PlcAddress.YBase + 9,
    ushort BarcodeAddress = 4000,
    ushort VerdictAddress = 3)
{
    internal const int MaxBarcodeWords = 80;

    internal void Validate()
    {
        if (BarcodeAddress + MaxBarcodeWords > 65536)
            throw new InvalidOperationException("二维码起始地址须为 D0–D65456，需连续占用 80 个寄存器。");
        if (VerdictAddress >= BarcodeAddress && VerdictAddress < BarcodeAddress + MaxBarcodeWords)
            throw new InvalidOperationException("比对结果地址不能与二维码的 80 个寄存器重叠。");
    }

    internal static bool TryParseRegister(string text, out ushort address) =>
        PlcAddress.TryParseRegister(text, out address);
}

internal sealed record ScannerResult(string Barcode, string Model, ushort[] Words, ushort VerdictCode)
{
    internal string Comparison => $"{(VerdictCode == 1 ? "匹配" : "不匹配")}（{VerdictCode}）· {Model}";

    internal static ScannerResult Create(string barcode, Recipe recipe)
    {
        if (string.IsNullOrEmpty(barcode))
            throw new InvalidOperationException("扫码结果为空，不能写入 PLC。");
        if (recipe.BarcodeStartIndex is < 1 or > 512 || recipe.BarcodeCompareLength is < 0 or > 512
            || string.IsNullOrEmpty(recipe.Model))
            throw new InvalidOperationException("当前配方的扫码比对参数无效。");

        // 字数按实际字节数动态决定，末尾多留一个零字节作结束符；恰好 160 字节时占满预留块。
        int byteCount = Encoding.UTF8.GetByteCount(barcode);
        var words = new ushort[Math.Min(ScannerTriggerSettings.MaxBarcodeWords, byteCount / 2 + 1)];
        PlcLayout.WriteString(words, 0, words.Length, barcode, "二维码");
        int start = recipe.BarcodeStartIndex - 1;
        int length = recipe.BarcodeCompareLength == 0 ? recipe.Model.Length : recipe.BarcodeCompareLength;
        bool matches = length == recipe.Model.Length && start <= barcode.Length - length
            && barcode.AsSpan(start, length).SequenceEqual(recipe.Model.AsSpan());
        return new ScannerResult(barcode, recipe.Model, words, matches ? (ushort)1 : (ushort)2);
    }
}
