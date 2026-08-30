using FluentAssertions;
using ProjectManagementWeb.Application.Auth;

namespace ProjectManagementWeb.UnitTests;

public sealed class RegistrationValidatorTests
{
    [Test]
    public void 欄位格式錯誤時應回傳所有對應欄位訊息()
    {
        var request = new RegisterRequest(
            "invalid account",
            "weak",
            "different",
            "invalid-email",
            " ");

        IReadOnlyDictionary<string, string[]> errors = RegistrationValidator.Validate(request);

        errors.Keys.Should().BeEquivalentTo("account", "name", "email", "password", "confirmPassword");
        errors["account"].Should().Contain("帳號只能使用英文字母、數字及 - . _ @ +。");
        errors["name"].Should().Contain("請輸入顯示名稱。");
        errors["email"].Should().Contain("請輸入有效的 Email 格式。");
        errors["password"].Should().Contain("密碼至少需要 10 個字元。");
        errors["confirmPassword"].Should().Contain("兩次輸入的密碼不一致。");
    }

    [Test]
    public void 欄位皆符合規則時不應回傳錯誤()
    {
        var request = new RegisterRequest(
            "new.user",
            "ValidPass1!",
            "ValidPass1!",
            "new.user@example.com",
            "新使用者");

        RegistrationValidator.Validate(request).Should().BeEmpty();
    }
}
