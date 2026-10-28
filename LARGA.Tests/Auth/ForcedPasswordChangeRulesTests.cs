using LARGA.SharedCore;

namespace LARGA.Tests.Auth;

/// <summary>users/{uid}.mustChangePassword: the forced change-password form (ForcedPasswordChangeRules).</summary>
public class ForcedPasswordChangeRulesTests
{
    [Theory]
    [InlineData("", "newPass1", "newPass1", "Please fill in all fields.")]
    [InlineData("temp123", "newPass1", "newPass2", "New password and confirmation must match.")]
    [InlineData("temp123", "temp123", "temp123", "Choose a new password - it can't be the temporary one your manager gave you.")]
    public void Validate_RefusesIncompleteOrUnchangedPasswords(string current, string next, string confirm, string expected) =>
        Assert.Equal(expected, ForcedPasswordChangeRules.Validate(current, next, confirm));

    [Fact]
    public void Validate_UsesTheUsualPasswordRule()
    {
        Assert.Equal(InputValidator.ValidatePassword("a b c d"), ForcedPasswordChangeRules.Validate("temp123", "a b c d", "a b c d"));
        Assert.NotNull(ForcedPasswordChangeRules.Validate("temp123", "a b c d", "a b c d"));
    }

    [Fact]
    public void Validate_AcceptsANewValidPassword() =>
        Assert.Null(ForcedPasswordChangeRules.Validate("temp123", "myOwnPass9", "myOwnPass9"));

    private static readonly DateTime TempSet = new(2026, 9, 1, 2, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void ChangedSinceTemporary_LaterPasswordChange_ClearsTheFlag() =>
        Assert.True(ForcedPasswordChangeRules.ChangedSinceTemporary(TempSet.AddDays(3), TempSet));

    [Theory]
    [InlineData(0)]   // Firebase's own timestamp for the manager's write
    [InlineData(1)]   // within the margin
    [InlineData(-60)] // before the temporary password
    public void ChangedSinceTemporary_NoLaterChange_KeepsTheFlag(int minutesAfter) =>
        Assert.False(ForcedPasswordChangeRules.ChangedSinceTemporary(TempSet.AddMinutes(minutesAfter), TempSet));

    [Fact]
    public void ChangedSinceTemporary_UnknownTimes_KeepsTheFlag()
    {
        Assert.False(ForcedPasswordChangeRules.ChangedSinceTemporary(null, TempSet));
        Assert.False(ForcedPasswordChangeRules.ChangedSinceTemporary(TempSet.AddDays(3), null));
    }
}
