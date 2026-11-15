using DustLoadScada;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Newland.Fm430.Tests;

public sealed class ScaleTests
{
    [Theory]
    [InlineData(MonitorPointId.Voltage, RegisterDataType.Int32, 0.01)]
    [InlineData(MonitorPointId.Voltage, RegisterDataType.Float32, 1)]
    [InlineData(MonitorPointId.NormalSpeed, RegisterDataType.Int32, 1)]
    [InlineData(MonitorPointId.NormalVoltage, RegisterDataType.Float32, 1)]
    public void MonitorDefaultScalePreservesTheLegacyConversion(MonitorPointId id, RegisterDataType type, double expected)
    {
        Assert.Equal(expected, MonitorPointDefinition.For(id).DefaultScale(type));
    }

    [Fact]
    public void MonitorDecodeAppliesDefaultAndCustomScale()
    {
        var definition = MonitorPointDefinition.For(MonitorPointId.Voltage);
        ushort[] words = [0, 0x3039]; // 12345, high word first

        Assert.Equal(123.45, definition.Decode(RegisterDataType.Int32, words, false));
        Assert.Equal(1234.5, definition.Decode(RegisterDataType.Int32, words, false, 0.1));
        Assert.Equal(12345, definition.Decode(RegisterDataType.Int32, words, false, 1));
    }

    [Fact]
    public void MonitorScaleForFallsBackToTypeDefaultUnlessOverridden()
    {
        var definition = MonitorPointDefinition.For(MonitorPointId.Voltage);
        Assert.Equal(0.01, definition.ScaleFor(new MonitorPoint(6, RegisterDataType.Int32)));
        Assert.Equal(0.5, definition.ScaleFor(new MonitorPoint(6, RegisterDataType.Int32, 0.5)));
        Assert.Equal(1, definition.ScaleFor(new MonitorPoint(6, RegisterDataType.Float32)));
    }

    [Theory]
    [InlineData(RecipePointId.PowerActionSeconds, 0.1)]
    [InlineData(RecipePointId.NormalSeconds, 0.1)]
    [InlineData(RecipePointId.VoltageMin, 1)]
    [InlineData(RecipePointId.PowerCycles, 1)]
    public void RecipeDefaultScaleIsTheInverseOfTheLegacyWriteFactor(RecipePointId id, double expected)
    {
        Assert.Equal(expected, RecipePoint.All[id].DefaultScale);
    }

    [Fact]
    public void RecipeEncodeDefaultPreservesLegacyWriteFactorAndCustomScaleDivides()
    {
        var recipe = new Recipe { PowerActionSeconds = 3 };
        var point = RecipePoint.All[RecipePointId.PowerActionSeconds];

        ushort[] legacy = new ushort[1];
        point.Encode(recipe, legacy, false);
        Assert.Equal(30, legacy[0]);

        ushort[] custom = new ushort[1];
        point.Encode(recipe, custom, false, 0.2);
        Assert.Equal(15, custom[0]);
    }

    [Fact]
    public void RecipeBlockEncodeHonoursPerPointScaleOverrides()
    {
        var recipe = new Recipe { PowerActionSeconds = 3 };
        var scales = new Dictionary<RecipePointId, double> { [RecipePointId.PowerActionSeconds] = 0.2 };
        int offset = RecipePoint.All[RecipePointId.PowerActionSeconds].Offset;

        var block = PlcLayout.Encode(recipe, false, scales);
        Assert.Equal(15, block[offset]);

        var defaultBlock = PlcLayout.Encode(recipe, false);
        Assert.Equal(30, defaultBlock[offset]);
    }

    [Fact]
    public void RecipeWritePlanCarriesScaleOverridesIntoTheWriteBlock()
    {
        var settings = new PlcSettings { RecipeAddress = 100, TelemetryAddress = 0, LowWordFirst = false };
        var scales = new Dictionary<RecipePointId, double> { [RecipePointId.PowerActionSeconds] = 0.2 };
        var plan = new RecipeWritePlan(settings, new Dictionary<RecipePointId, ushort>(), scales);

        var (address, words) = Assert.Single(plan.Encode(new Recipe { PowerActionSeconds = 3 }));
        Assert.Equal((ushort)100, address);
        int offset = RecipePoint.All[RecipePointId.PowerActionSeconds].Offset;
        Assert.Equal(15, words[offset]);
    }

    [Fact]
    public void MonitorAndRecipeScalesRoundTripThroughTheStore()
    {
        string folder = Path.Combine(Path.GetTempPath(), "scale-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new StationStore(folder);
            var monitors = new Dictionary<MonitorPointId, MonitorPoint>
            {
                [MonitorPointId.Voltage] = new(6, RegisterDataType.Int32, 0.5)
            };
            store.SaveMonitorPoints(monitors);
            Assert.Equal(0.5, store.LoadMonitorPoints()[MonitorPointId.Voltage].Scale);

            var scales = new Dictionary<RecipePointId, double> { [RecipePointId.PowerActionSeconds] = 0.2 };
            store.SaveRecipeScales(scales);
            Assert.Equal(0.2, store.LoadRecipeScales()[RecipePointId.PowerActionSeconds]);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(folder, true);
        }
    }
}
