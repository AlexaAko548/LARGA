using LARGA.SharedCore;

namespace LARGA.Tests.Auth;

/// <summary>Assistant Manager access in the mobile app (StaffRoles) and the SOS auto-answer
/// allowlist that now includes Assistant Managers (ManagerPhoneAllowlist).</summary>
public class StaffRolesTests
{
    [Theory]
    [InlineData("Driver", "//driver-dashboard")]
    [InlineData(" driver ", "//driver-dashboard")]
    [InlineData("Manager", "//manager-dashboard")]
    [InlineData("Assistant Manager", "//manager-dashboard")]
    [InlineData("assistant manager", "//manager-dashboard")]
    [InlineData("Mechanic", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void HomeRoute_ByRole(string? role, string? expected) =>
        Assert.Equal(expected, StaffRoles.HomeRoute(role));

    [Theory]
    [InlineData("Manager", "Manager", true)]
    [InlineData("Assistant Manager", "Manager", true)]
    [InlineData("Driver", "Driver", true)]
    [InlineData("Assistant Manager", "Driver", false)]
    [InlineData("Manager", "Driver", false)]
    [InlineData("Driver", "Manager", false)]
    public void LoginTile_AssistantManagerUsesTheManagerTile(string role, string tile, bool allowed) =>
        Assert.Equal(allowed, StaffRoles.MatchesLoginTile(role, tile));

    [Fact]
    public void Allowlist_IncludesManagersAndAssistantManagers_Only()
    {
        List<string> numbers = ManagerPhoneAllowlist.Build(new (string?, string?)[]
        {
            ("Manager", "09171234567"),
            ("Assistant Manager", "0918 765 4321"),
            ("assistant manager", "+639181112222"),
            ("Driver", "09190000000"),
            ("Manager", "0917-123-4567"), // same number again
            ("Manager", "not a number"),
            ("Assistant Manager", null),
        });

        Assert.Equal(3, numbers.Count);
        Assert.Equal(numbers.OrderBy(n => n, StringComparer.Ordinal), numbers);
        Assert.DoesNotContain(InputValidator.NormalizePhilippineMobile("09190000000"), numbers);
        Assert.Contains(InputValidator.NormalizePhilippineMobile("09187654321"), numbers);
    }
}
