using DustLoadScada;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Newland.Fm430.Tests;

public sealed class RecipeTypeTests
{
    [Theory]
    [InlineData(RecipePointId.VoltageMin, RegisterDataType.Float32)]
    [InlineData(RecipePointId.PowerCycles, RegisterDataType.UInt16)]
    [InlineData(RecipePointId.NormalSeconds, RegisterDataType.Int32)]
    [InlineData(RecipePointId.SpeedDeltaMin, RegisterDataType.Float32)]
    [InlineData(RecipePointId.SpeedDeltaMax, RegisterDataType.Float32)]
    public void RecipeDefaultTypeMatchesTheLegacyEncoding(RecipePointId id, RegisterDataType expected)
    {
        Assert.Equal(expected, RecipePoint.All[id].DefaultType);
    }

    [Theory]
    [InlineData(RegisterDataType.Int16, 1)]
    [InlineData(RegisterDataType.UInt16, 1)]
    [InlineData(RegisterDataType.Int32, 2)]
    [InlineData(RegisterDataType.UInt32, 2)]
    [InlineData(RegisterDataType.Float32, 2)]
    public void WordsForMatchesTheRegisterWidth(RegisterDataType type, int expected)
    {
        Assert.Equal(expected, RecipePoint.WordsFor(type));
    }

    [Fact]
    public void EncodeHonoursATypeOverrideAndShrinksToOneWord()
    {
        var recipe = new Recipe { NormalSeconds = 3 };
        var point = RecipePoint.All[RecipePointId.NormalSeconds];

        ushort[] words = new ushort[2];
        point.Encode(recipe, words, false, scale: 1, type: RegisterDataType.Int16);
        Assert.Equal((ushort)3, words[0]);
        Assert.Equal((ushort)0, words[1]);
    }

    [Fact]
    public void EncodeFloat32IgnoresScale()
    {
        var recipe = new Recipe { NormalSeconds = 3 };
        var point = RecipePoint.All[RecipePointId.NormalSeconds];

        ushort[] words = new ushort[2];
        point.Encode(recipe, words, false, scale: 0.5, type: RegisterDataType.Float32);
        uint bits = ((uint)words[0] << 16) | words[1];
        Assert.Equal(3.0f, BitConverter.UInt32BitsToSingle(bits));
    }

    [Fact]
    public void WritePlanCarriesTypeOverridesIntoTheWriteBlock()
    {
        var settings = new PlcSettings { RecipeAddress = null, TelemetryAddress = 0, LowWordFirst = false };
        var overrides = new Dictionary<RecipePointId, ushort> { [RecipePointId.PowerCycles] = 500 };
        var types = new Dictionary<RecipePointId, RegisterDataType> { [RecipePointId.PowerCycles] = RegisterDataType.Int32 };
        var plan = new RecipeWritePlan(settings, overrides, types: types);

        var (address, words) = Assert.Single(plan.Encode(new Recipe { PowerCycles = 7 }), write => write.Address == 500);
        Assert.Equal((ushort)500, address);
        Assert.Equal(2, words.Length);
        Assert.Equal(7, (words[0] << 16) | words[1]);
    }

    [Fact]
    public void WritePlanRejectsAnEnlargedTypeThatOverlapsTheNextPoint()
    {
        var settings = new PlcSettings { RecipeAddress = null, TelemetryAddress = 0, LowWordFirst = false };
        var overrides = new Dictionary<RecipePointId, ushort>
        {
            [RecipePointId.PowerCycles] = 500,
            [RecipePointId.GearCycles] = 501
        };
        var types = new Dictionary<RecipePointId, RegisterDataType> { [RecipePointId.PowerCycles] = RegisterDataType.Int32 };

        Assert.Throws<InvalidOperationException>(() => new RecipeWritePlan(settings, overrides, types: types));
    }

    [Theory]
    [InlineData(RecipePointId.PowerCycles, 0)]
    [InlineData(RecipePointId.GearCycles, 1)]
    [InlineData(RecipePointId.PowerActionSeconds, 2)]
    [InlineData(RecipePointId.PowerIntervalSeconds, 3)]
    [InlineData(RecipePointId.GearActionSeconds, 4)]
    [InlineData(RecipePointId.GearIntervalSeconds, 5)]
    [InlineData(RecipePointId.IndicatorOffSeconds, 10)]
    [InlineData(RecipePointId.PowerDeltaMax, 30)]
    [InlineData(RecipePointId.PowerDeltaMin, 32)]
    [InlineData(RecipePointId.SpeedDeltaMax, 34)]
    [InlineData(RecipePointId.SpeedDeltaMin, 36)]
    [InlineData(RecipePointId.NormalSeconds, 100)]
    [InlineData(RecipePointId.DustSeconds, 102)]
    [InlineData(RecipePointId.VoltageMin, 12)]
    [InlineData(RecipePointId.VoltageMax, 14)]
    [InlineData(RecipePointId.AlarmSeconds, 16)]
    [InlineData(RecipePointId.NormalCurrentMax, 200)]
    [InlineData(RecipePointId.NormalCurrentMin, 202)]
    [InlineData(RecipePointId.NormalPowerMax, 204)]
    [InlineData(RecipePointId.NormalPowerMin, 206)]
    [InlineData(RecipePointId.NormalSpeedMax, 208)]
    [InlineData(RecipePointId.NormalSpeedMin, 210)]
    [InlineData(RecipePointId.DustCurrentMax, 220)]
    [InlineData(RecipePointId.DustCurrentMin, 222)]
    [InlineData(RecipePointId.DustPowerMax, 224)]
    [InlineData(RecipePointId.DustPowerMin, 226)]
    [InlineData(RecipePointId.DustSpeedMax, 228)]
    [InlineData(RecipePointId.DustSpeedMin, 230)]
    public void RecipeDefaultsMatchTheConfirmedPlcPointTable(RecipePointId id, int hdNumber)
    {
        var settings = new PlcSettings { RecipeAddress = null };
        Assert.Equal((ushort)(0xA080 + hdNumber), RecipePoint.All[id].DefaultAddress(settings));
    }

    [Fact]
    public void AllRecipePointsHaveDefaultAddressesSoDownloadIsConfigured()
    {
        var settings = new PlcSettings { RecipeAddress = null, TelemetryAddress = 0 };
        var plan = new RecipeWritePlan(settings, new Dictionary<RecipePointId, ushort>());
        Assert.True(plan.Configured);
    }

    [Fact]
    public void RecipeBlockEncodesTheNewLimitParameters()
    {
        var recipe = new Recipe { NormalCurrentMax = 5.5f, DustSpeedMin = 100 };
        var block = PlcLayout.Encode(recipe, lowWordFirst: true);

        Assert.Equal(PlcLayout.RecipeWords, block.Length);
        uint bits = ((uint)block[50] << 16) | block[49];
        Assert.Equal(5.5f, BitConverter.UInt32BitsToSingle(bits));
        uint dustBits = ((uint)block[72] << 16) | block[71];
        Assert.Equal(100f, BitConverter.UInt32BitsToSingle(dustBits));
    }

    [Fact]
    public void RecipeTypesRoundTripThroughTheStore()
    {
        string folder = Path.Combine(Path.GetTempPath(), "recipe-type-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new StationStore(folder);
            var types = new Dictionary<RecipePointId, RegisterDataType>
            {
                [RecipePointId.PowerCycles] = RegisterDataType.Int32,
                [RecipePointId.NormalSeconds] = RegisterDataType.Float32
            };
            store.SaveRecipeTypes(types);
            var loaded = store.LoadRecipeTypes();
            Assert.Equal(RegisterDataType.Int32, loaded[RecipePointId.PowerCycles]);
            Assert.Equal(RegisterDataType.Float32, loaded[RecipePointId.NormalSeconds]);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(folder, true);
        }
    }
}