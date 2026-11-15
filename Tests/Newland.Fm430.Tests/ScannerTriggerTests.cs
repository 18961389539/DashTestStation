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
        Assert.Equal(6, result.Words.Length);
        Assert.Equal((ushort)0x4142, result.Words[0]);
    }

    [Theory]
    [InlineData("A", 1)]
    [InlineData("AB", 2)]
    [InlineData("ABC", 2)]
    [InlineData("ABCD", 3)]
    [InlineData("ABMODEL-001", 6)]
    [InlineData("中文ABC", 5)]
    public void BarcodeWritesOnlyTheWordsItNeedsPlusANulTerminator(string barcode, int expectedWords)
    {
        var result = DustLoadScada.ScannerResult.Create(barcode, new DustLoadScada.Recipe());

        Assert.Equal(expectedWords, result.Words.Length);
        Assert.Equal(barcode, DustLoadScada.PlcLayout.ReadString(result.Words));
        Assert.Equal(0, result.Words[^1] & 0xFF);
    }

    [Theory]
    [InlineData(1, DustLoadScada.TestStage.Idle)]
    [InlineData(2, DustLoadScada.TestStage.Normal)]
    [InlineData(3, DustLoadScada.TestStage.Idle)]
    [InlineData(4, DustLoadScada.TestStage.Idle)]
    [InlineData(5, DustLoadScada.TestStage.Fault)]
    [InlineData(6, DustLoadScada.TestStage.Fault)]
    [InlineData(7, DustLoadScada.TestStage.Fault)]
    [InlineData(0, DustLoadScada.TestStage.Fault)]
    public void RunStateCodesMapToStages(ushort code, DustLoadScada.TestStage expected) =>
        Assert.Equal(expected, DustLoadScada.PlcLayout.StageOf(code));

    [Fact]
    public void RunStateIsReadFromItsOwnAddressWithoutVersionCheck()
    {
        var settings = new DustLoadScada.PlcSettings();
        var memory = new ushort[65536];
        memory[settings.TelemetryAddress] = 0;
        memory[500] = 4;
        var overrides = new Dictionary<DustLoadScada.MonitorPointId, DustLoadScada.MonitorPoint>
        {
            [DustLoadScada.MonitorPointId.RunState] = new(500, DustLoadScada.RegisterDataType.Int16)
        };
        var plan = new DustLoadScada.MonitorReadPlan(settings, overrides);
        ushort[] Read(ushort address, ushort count) => memory.AsSpan(address, count).ToArray();

        var (test, _) = plan.Read(Read);

        Assert.Equal((ushort)4, test.RunState);
        Assert.Equal(DustLoadScada.TestStage.Idle, test.Stage);
        Assert.Equal(DustLoadScada.TestStage.Idle, plan.ReadStage(Read));
        memory[500] = 2;
        Assert.Equal(DustLoadScada.TestStage.Normal, plan.ReadStage(Read));
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
        Assert.Equal(80, DustLoadScada.ScannerResult.Create(new string('A', 159), recipe).Words.Length);
        Assert.Equal(80, DustLoadScada.ScannerResult.Create(new string('A', 160), recipe).Words.Length);

        Assert.Throws<InvalidOperationException>(() =>
            DustLoadScada.ScannerResult.Create(new string('A', 161), recipe));
    }

    [Theory]
    [InlineData("D4000", 4000)]
    [InlineData("379", 379)]
    [InlineData("HD0", 0xA080)]
    [InlineData("HM1", 0xC101)]
    [InlineData("HM2", 0xC102)]
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
            new DustLoadScada.ScannerTriggerSettings(BarcodeAddress: 65457).Validate());
        new DustLoadScada.ScannerTriggerSettings(BarcodeAddress: 65456).Validate();
        Assert.Throws<InvalidOperationException>(() =>
            new DustLoadScada.ScannerTriggerSettings(BarcodeAddress: 4000, VerdictAddress: 4079).Validate());
        new DustLoadScada.ScannerTriggerSettings(BarcodeAddress: 4000, VerdictAddress: 4080).Validate();
    }

    [Fact]
    public void VerdictDefaultsToD3AndMayShareRegistersWithMonitorPoints()
    {
        var settings = new DustLoadScada.PlcSettings();
        Assert.Equal((ushort)3, new DustLoadScada.ScannerTriggerSettings().VerdictAddress);

        // D3 落在监控整块 D0–D63 内，但没有监控点占用，默认比对结果 D3 应能正常启动。
        using var station = new DustLoadScada.ModbusStation(settings);

        // 扫码写回与监控属于不同页面，D3 即使被映射成监控点也允许共用。
        var overrides = new Dictionary<DustLoadScada.MonitorPointId, DustLoadScada.MonitorPoint>
        {
            [DustLoadScada.MonitorPointId.RunState] = new(3, DustLoadScada.RegisterDataType.Int16)
        };
        using var shared = new DustLoadScada.ModbusStation(settings, overrides);
    }

    [Theory]
    [InlineData(6)]   // 与实时电压默认地址 D6–D7 完全相同
    [InlineData(7)]   // 与实时电压部分重叠
    [InlineData(26)]  // 落在默认二维码 D26–D41 内
    public void MonitorPointsRejectDuplicatesWithinThePage(ushort address)
    {
        var settings = new DustLoadScada.PlcSettings();
        var overrides = new Dictionary<DustLoadScada.MonitorPointId, DustLoadScada.MonitorPoint>
        {
            [DustLoadScada.MonitorPointId.RunState] = new(address, DustLoadScada.RegisterDataType.Int16)
        };

        Assert.Throws<InvalidOperationException>(() => new DustLoadScada.MonitorReadPlan(settings, overrides));
    }

    [Fact]
    public void RecipePointsMayShareRegistersWithMonitorPoints()
    {
        var settings = new DustLoadScada.PlcSettings { RecipeAddress = null };
        var monitorOverrides = new Dictionary<DustLoadScada.MonitorPointId, DustLoadScada.MonitorPoint>
        {
            [DustLoadScada.MonitorPointId.RunState] = new(0, DustLoadScada.RegisterDataType.Int16)
        };
        _ = new DustLoadScada.MonitorReadPlan(settings, monitorOverrides);

        var recipeOverrides = new Dictionary<DustLoadScada.RecipePointId, ushort>
        {
            [DustLoadScada.RecipePointId.PowerCycles] = 0,
            [DustLoadScada.RecipePointId.GearCycles] = 1
        };
        _ = new DustLoadScada.RecipeWritePlan(settings, recipeOverrides);
    }

    [Fact]
    public void RealtimeCurrentPowerAndDustVoltageReadFromTheirPointTableAddresses()
    {
        var settings = new DustLoadScada.PlcSettings();
        var memory = new ushort[65536];
        void WriteFloat(ushort address, float value)
        {
            uint bits = BitConverter.SingleToUInt32Bits(value);
            memory[address] = (ushort)bits;
            memory[address + 1] = (ushort)(bits >> 16);
        }
        WriteFloat(302, 1.5f);
        WriteFloat(304, 86.25f);
        WriteFloat(402, 24.5f);
        var plan = new DustLoadScada.MonitorReadPlan(settings,
            new Dictionary<DustLoadScada.MonitorPointId, DustLoadScada.MonitorPoint>());
        ushort[] Read(ushort address, ushort count) => memory.AsSpan(address, count).ToArray();

        var (test, _) = plan.Read(Read);

        Assert.Equal(1.5, test.RealtimeCurrent);
        Assert.Equal(86.25, test.RealtimePower);
        Assert.Equal(24.5, test.DustVoltage);
    }
}
