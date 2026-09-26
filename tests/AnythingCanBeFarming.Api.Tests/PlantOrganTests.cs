using AnythingCanBeFarming.Api.Identification;
using AnythingCanBeFarming.Api.PlantNet;

namespace AnythingCanBeFarming.Api.Tests;

public sealed class PlantOrganTests
{
    [Fact]
    public void Wire_values_match_the_organs_the_client_accepts()
    {
        var wire = Enum.GetValues<PlantOrgan>().Select(PlantOrgans.ToWire).ToHashSet();
        Assert.Equal(Enum.GetValues<PlantOrgan>().Length, wire.Count);
        Assert.True(wire.SetEquals(PlantNetClient.ValidOrgans),
            $"Enum: {string.Join(",", wire.Order())}; client: {string.Join(",", PlantNetClient.ValidOrgans.Order())}");
        Assert.True(PlantOrgans.WireValues.ToHashSet().SetEquals(wire));
    }

    [Fact]
    public void Wire_values_round_trip()
    {
        foreach (var organ in Enum.GetValues<PlantOrgan>())
        {
            Assert.True(PlantOrgans.TryParse(PlantOrgans.ToWire(organ), out var parsed));
            Assert.Equal(organ, parsed);
        }
        Assert.Equal("habit", PlantOrgans.ToWire(PlantOrgan.Habit));
    }

    [Theory]
    [InlineData("Leaf")]
    [InlineData("LEAF")]
    [InlineData(" leaf")]
    [InlineData("leaf ")]
    [InlineData("1")]
    [InlineData("0")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("unknown")]
    public void TryParse_accepts_only_exact_lowercase_wire_values(string? value) =>
        Assert.False(PlantOrgans.TryParse(value, out _));

    [Fact]
    public void ToWire_rejects_undefined_values() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => PlantOrgans.ToWire((PlantOrgan)99));
}
