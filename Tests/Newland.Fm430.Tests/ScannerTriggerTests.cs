using Xunit;

namespace Newland.Fm430.Tests;

public sealed class ScannerTriggerTests
{
    [Fact]
    public void ScanComparesTheConfiguredOneBasedModelSliceAndEncodesBarcode()
    {
        var recipe = new DustLoadScada.Recipe
        {
            Model = "MODEL",
            BarcodeStartIndex = 3,
            BarcodeCompareLength = 5
        };

        var result = DustLoadScada.ScannerResult.Create("ABMODEL-001", recipe);

        Assert.Equal((ushort)1, result.VerdictCode);
        Assert.Equal("ABMODEL-001", DustLoadScada.PlcLayout.ReadString(result.Words));
        Assert.Equal(32, result.Words.Length);
        Assert.Equal((ushort)0x4142, result.Words[0]);
    }

    [Theory]
    [InlineData("ABmodel-001", 3, 5)]
    [InlineData("ABMO-001", 3, 5)]
    [InlineData("ABMODEL-001", 3, 4)]
    [InlineData("MODEL-001", 10, 5)]
    public void ScanReportsNonmatchingBarcode(string barcode, int startIndex, int length)
    {
        var recipe = new DustLoadScada.Recipe
        {
            Model = "MODEL",
            BarcodeStartIndex = startIndex,
            BarcodeCompareLength = length
        };

        Assert.Equal((ushort)2, DustLoadScada.ScannerResult.Create(barcode, recipe).VerdictCode);
    }

    [Fact]
    public void ZeroCompareLengthUsesProductModelLength()
    {
        var recipe = new DustLoadScada.Recipe { Model = "MODEL", BarcodeStartIndex = 3 };

        Assert.Equal((ushort)1, DustLoadScada.ScannerResult.Create("ABMODEL-001", recipe).VerdictCode);
    }

    [Fact]
    public void BarcodeMustFitTheConfiguredRegisterBlockWithoutTruncation()
    {
        var recipe = new DustLoadScada.Recipe();
        DustLoadScada.ScannerResult.Create(new string('A', 64), recipe);

        Assert.Throws<InvalidOperationException>(() =>
            DustLoadScada.ScannerResult.Create(new string('A', 65), recipe));
    }

    [Theory]
    [InlineData("D4000", 4000)]
    [InlineData("379", 379)]
    [InlineData("HD0", 0xA080)]
    public void RegisterAddressAcceptsDeviceAndDecimalSyntax(string text, ushort expected)
    {
        Assert.True(DustLoadScada.ScannerTriggerSettings.TryParseRegister(text, out ushort address));
        Assert.Equal(expected, address);
    }

    [Fact]
    public void DefaultTriggerAddressIsXinjeY11()
    {
        var settings = new DustLoadScada.ScannerTriggerSettings();

        Assert.Equal((ushort)(0x6000 + 9), settings.TriggerAddress);
        Assert.Equal("Y11", DustLoadScada.PlcAddress.FormatCoil(settings.TriggerAddress!.Value));
    }

    [Theory]
    [InlineData("M50", 50)]
    [InlineData("X0", 0x5000)]
    [InlineData("x77", 0x503F)]
    [InlineData("Y10", 0x6008)]
    [InlineData("Y10000", 0x6100)]
    [InlineData("S0", 0x7000)]
    [InlineData("HM0", 0xC100)]
    [InlineData("24576", 24576)]
    public void Xd3CoilAddressesMapToModbusAndBack(string text, ushort expected)
    {
        Assert.True(DustLoadScada.PlcAddress.TryParseCoil(text, out ushort address));
        Assert.Equal(expected, address);
        Assert.True(DustLoadScada.PlcAddress.TryParseCoil(DustLoadScada.PlcAddress.FormatCoil(address), out ushort roundTrip));
        Assert.Equal(expected, roundTrip);
    }

    [Theory]
    [InlineData("Y8")]
    [InlineData("Y100")]
    [InlineData("M8000")]
    [InlineData("HM960")]
    [InlineData("D1")]
    public void Xd3RejectsInvalidCoilDevices(string text)
    {
        Assert.False(DustLoadScada.PlcAddress.TryParseCoil(text, out _));
    }

    [Fact]
    public void Xd3PhysicalIoIsNotWritable()
    {
        Assert.True(DustLoadScada.PlcAddress.IsPhysicalIo(0x5000));
        Assert.True(DustLoadScada.PlcAddress.IsPhysicalIo(0x6000));
        Assert.False(DustLoadScada.PlcAddress.IsPhysicalIo(50));
        Assert.False(DustLoadScada.PlcAddress.IsPhysicalIo(0xFC00));
    }

    [Fact]
    public void ScannerRegisterSettingsRejectOutOfRangeAndOverlappingAddresses()
    {
        Assert.Throws<InvalidOperationException>(() =>
            new DustLoadScada.ScannerTriggerSettings(BarcodeAddress: 65505).Validate());
        Assert.Throws<InvalidOperationException>(() =>
            new DustLoadScada.ScannerTriggerSettings(BarcodeAddress: 4000, VerdictAddress: 4031).Validate());
    }
}
