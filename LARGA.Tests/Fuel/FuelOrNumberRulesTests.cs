using LARGA.SharedCore;

namespace LARGA.Tests.Fuel;

/// <summary>Proposal §4.2.4.5: OR number duplicate check (FuelOrNumberRules).</summary>
public class FuelOrNumberRulesTests
{
    [Theory]
    [InlineData("OR-0012345", "OR0012345")]
    [InlineData(" or 0012 345 ", "OR0012345")]
    [InlineData("12/34", "12_34")]
    public void Normalize_SameNumberWrittenDifferently_SameClaim(string input, string expected) =>
        Assert.Equal(expected, FuelOrNumberRules.Normalize(input));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" - ")]
    public void Normalize_Blank_NotChecked(string? input) =>
        Assert.Null(FuelOrNumberRules.Normalize(input));

    [Fact]
    public void CombineReasons_StationAndDuplicate() =>
        Assert.Equal("Station is not Petron or Shell; OR number already submitted",
            FuelOrNumberRules.CombineReasons(new[] { FuelStations.NotAllowedReason, FuelOrNumberRules.DuplicateReason }));

    [Fact]
    public void CombineReasons_SkipsBlanksAndRepeats()
    {
        Assert.Equal(FuelOrNumberRules.DuplicateReason,
            FuelOrNumberRules.CombineReasons(new[] { null, "", FuelOrNumberRules.DuplicateReason, FuelOrNumberRules.DuplicateReason }));
        Assert.Null(FuelOrNumberRules.CombineReasons(new string?[] { null, " " }));
    }
}
