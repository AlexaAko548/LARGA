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
    public void Key_SameOrNumberDifferentStation_DifferentClaim()
    {
        string? petron = FuelOrNumberRules.Key("Petron - EDSA", "0012345");
        string? shell = FuelOrNumberRules.Key("Shell Pilipinas", "0012345");
        Assert.NotEqual(petron, shell);
    }

    [Fact]
    public void Key_SameStationAndOrNumberWrittenDifferently_SameClaim()
    {
        string? a = FuelOrNumberRules.Key("Petron - EDSA", "OR-0012345");
        string? b = FuelOrNumberRules.Key(" petron edsa ", "or 0012 345");
        Assert.Equal(a, b);
    }

    [Fact]
    public void Key_BlankOrNumber_NotChecked() =>
        Assert.Null(FuelOrNumberRules.Key("Petron", ""));

    [Fact]
    public void Key_BlankStation_StillChecked() =>
        Assert.NotNull(FuelOrNumberRules.Key(null, "0012345"));

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
