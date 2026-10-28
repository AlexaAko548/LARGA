using System.Collections.Generic;
using System.Linq;

namespace LARGA.SharedCore;

/// <summary>
/// system_configs/global.managerPhoneNumbers - the numbers a driver's phone auto-answers after an
/// automated SOS (LAR-86/87): every Manager's and Assistant Manager's profile number, normalized
/// (InputValidator.NormalizePhilippineMobile), without repeats, sorted so an unchanged list compares equal.
/// </summary>
public static class ManagerPhoneAllowlist
{
    public static List<string> Build(IEnumerable<(string? Role, string? PhoneNumber)> users) =>
        users
            .Where(u => StaffRoles.IsStaff(u.Role))
            .Select(u => InputValidator.NormalizePhilippineMobile(u.PhoneNumber))
            .OfType<string>()
            .Distinct()
            .OrderBy(n => n, System.StringComparer.Ordinal)
            .ToList();
}
