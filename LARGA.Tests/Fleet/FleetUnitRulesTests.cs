using LARGA.SharedCore;

namespace LARGA.Tests.Fleet;

/// <summary>Taxi unit and driver deletion rules (ManagerWeb's Fleet Units page and driver profile).</summary>
public class FleetUnitRulesTests
{
    [Theory]
    [InlineData("GAB 4004", true)]
    [InlineData("gab-4004", true)]
    [InlineData("123 ABC", true)]
    [InlineData("", false)]
    [InlineData("ABCD", false)] // no number
    [InlineData("1234", false)] // no letter
    [InlineData("AB1", false)] // too short
    [InlineData("GAB 4004!", false)]
    public void Plate_needs_letters_and_numbers(string plate, bool valid) =>
        Assert.Equal(valid, FleetUnitRules.ValidatePlate(plate) is null);

    [Fact]
    public void Plates_match_however_they_are_typed()
    {
        Assert.Equal("GAB 4004", FleetUnitRules.NormalizePlate("  gab   4004 "));
        Assert.Equal(FleetUnitRules.PlateKey("GAB 4004"), FleetUnitRules.PlateKey("gab-4004"));
    }

    [Theory]
    [InlineData(1979, false)]
    [InlineData(1980, true)]
    [InlineData(2027, true)]
    [InlineData(2028, false)]
    public void Year_is_between_1980_and_next_year(int year, bool valid) =>
        Assert.Equal(valid, FleetUnitRules.ValidateYear(year, new DateTime(2026, 11, 26)) is null);

    [Fact]
    public void Mileage_is_required_and_not_negative()
    {
        Assert.NotNull(FleetUnitRules.ValidateMileage(null));
        Assert.NotNull(FleetUnitRules.ValidateMileage(-1));
        Assert.Null(FleetUnitRules.ValidateMileage(0));
    }

    [Fact]
    public void Next_id_follows_the_highest_one_in_use()
    {
        Assert.Equal("TAXI_001", FleetUnitRules.NextTaxiId(Array.Empty<string>()));
        Assert.Equal("TAXI_008", FleetUnitRules.NextTaxiId(new[] { "TAXI_002", "TAXI_007", "other" }));
    }

    [Theory]
    [InlineData("GAB 1001", true)]
    [InlineData("  gab-1001 ", true)]
    [InlineData("GAB1001", true)]
    [InlineData("GAB 100", false)]
    [InlineData("GAB 10011", false)]
    [InlineData("TAXI_001", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Removal_is_confirmed_by_the_plate(string? typed, bool expected) =>
        Assert.Equal(expected, FleetUnitRules.ConfirmsRemoval(typed, "GAB 1001", "TAXI_001"));

    [Theory]
    [InlineData("")]
    [InlineData(" - ")]
    [InlineData(null)]
    public void Without_a_plate_removal_is_confirmed_by_the_unit_id(string? plate)
    {
        Assert.Equal("TAXI_001", FleetUnitRules.RemovalConfirmationText(plate, "TAXI_001"));
        Assert.True(FleetUnitRules.ConfirmsRemoval("taxi_001", plate, "TAXI_001"));
        Assert.False(FleetUnitRules.ConfirmsRemoval("", plate, "TAXI_001"));
    }

    [Fact]
    public void Free_unit_has_no_blockers() =>
        Assert.Empty(FleetUnitRules.RemovalBlockers(new UnitRemovalFacts([], false, 0, false, [])));

    [Fact]
    public void Everything_current_blocks_removing_a_unit()
    {
        var facts = new UnitRemovalFacts(["Juan Dela Cruz"], true, 2, true, [new DateTime(2026, 12, 1)]);
        Assert.Equal(5, FleetUnitRules.RemovalBlockers(facts).Count);
    }

    [Fact]
    public void Settled_off_duty_driver_has_no_blockers() =>
        Assert.Empty(DriverRemovalRules.Blockers(false, false, 0, 0m));

    [Fact]
    public void Shift_clock_in_sos_and_debt_each_block_deleting_a_driver() =>
        Assert.Equal(4, DriverRemovalRules.Blockers(true, true, 1, 0.5m).Count);

    [Fact]
    public void Former_driver_is_a_name_on_records_not_a_role_that_signs_in()
    {
        Assert.False(StaffRoles.IsDriver(StaffRoles.FormerDriver));
        Assert.False(StaffRoles.IsStaff(StaffRoles.FormerDriver));
        Assert.Null(StaffRoles.HomeRoute(StaffRoles.FormerDriver));
        Assert.True(StaffRoles.IsDriverOrFormer(StaffRoles.FormerDriver));
    }
}
