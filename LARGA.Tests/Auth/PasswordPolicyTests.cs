using LARGA.SharedCore;

namespace LARGA.Tests.Auth;

/// <summary>The password policy (InputValidator.PasswordRules) for new passwords, and the lighter
/// length-only check at sign-in.</summary>
public class PasswordPolicyTests
{
    [Theory]
    [InlineData("abc12", "Password must be at least 6 characters.")]
    [InlineData("abc 123", "Password can't contain spaces.")]
    [InlineData("123456", "Password must contain at least one letter.")]
    [InlineData("abcdef", "Password must contain at least one number.")]
    public void ValidatePassword_RefusesPasswordsOutsideThePolicy(string password, string expected) =>
        Assert.Equal(expected, InputValidator.ValidatePassword(password));

    [Fact]
    public void ValidatePassword_RefusesTooLong() =>
        Assert.NotNull(InputValidator.ValidatePassword(new string('a', 64) + "1"));

    [Fact]
    public void ValidatePassword_AcceptsLettersAndNumbers() =>
        Assert.Null(InputValidator.ValidatePassword("larga2026"));

    [Fact]
    public void PasswordRules_AllMetExactlyWhenValid()
    {
        foreach (string password in new[] { "larga2026", "abcdef", "123456", "ab 12cd", "a1" })
        {
            bool allMet = InputValidator.PasswordRules.All(r => r.IsMet(password));
            Assert.Equal(InputValidator.ValidatePassword(password) is null, allMet);
        }
    }

    [Theory]
    [InlineData("", "Enter your password.")]
    [InlineData("abc", "Password must be at least 6 characters.")]
    public void ValidateLoginPassword_ChecksLengthOnly(string password, string expected) =>
        Assert.Equal(expected, InputValidator.ValidateLoginPassword(password));

    [Fact]
    public void ValidateLoginPassword_StillAcceptsAnOlderPasswordWithoutANumber() =>
        Assert.Null(InputValidator.ValidateLoginPassword("password"));
}
