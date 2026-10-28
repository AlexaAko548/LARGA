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
}
