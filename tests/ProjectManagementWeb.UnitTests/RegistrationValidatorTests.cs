using FluentAssertions;
using ProjectManagementWeb.Application.Auth;

namespace ProjectManagementWeb.UnitTests;

public sealed class RegistrationValidatorTests
{
    private static IEnumerable<TestCaseData> RegistrationBoundaryCases
    {
        get
        {
            const string validPassword = "Aa1!aaaaaa";
            yield return new TestCaseData(new RegisterRequest(new string('a', 256), validPassword,
                    validPassword, "valid@example.com", "使用者"), null)
                .SetName("帳號_256字_通過");
            yield return new TestCaseData(new RegisterRequest(new string('a', 257), validPassword,
                    validPassword, "valid@example.com", "使用者"), "account")
                .SetName("帳號_257字_拒絕");
            yield return new TestCaseData(new RegisterRequest("a-._@+1", validPassword,
                    validPassword, "valid@example.com", "使用者"), null)
                .SetName("帳號_允許所有合法符號");
            yield return new TestCaseData(new RegisterRequest("valid.user", validPassword,
                    validPassword, "valid@example.com", " "), "name")
                .SetName("顯示名稱_純空白_拒絕");
            yield return new TestCaseData(new RegisterRequest("valid.user", validPassword,
                    validPassword, "valid@example.com", "名"), null)
                .SetName("顯示名稱_1字_通過");
            yield return new TestCaseData(new RegisterRequest("valid.user", validPassword,
                    validPassword, "valid@example.com", new string('名', 100)), null)
                .SetName("顯示名稱_100字_通過");
            yield return new TestCaseData(new RegisterRequest("valid.user", validPassword,
                    validPassword, "valid@example.com", new string('名', 101)), "name")
                .SetName("顯示名稱_101字_拒絕");
            yield return new TestCaseData(new RegisterRequest("valid.user", "Aa1!aaaaa",
                    "Aa1!aaaaa", "valid@example.com", "使用者"), "password")
                .SetName("密碼_9字_拒絕");
            yield return new TestCaseData(new RegisterRequest("valid.user", validPassword,
                    validPassword, "valid@example.com", "使用者"), null)
                .SetName("密碼_10字_通過");
        }
    }

    // 測試案例：TC-ERR-AUTH-003（Backend Unit；與 API、Frontend 對應測試共同覆蓋）
    // 測試結果：Passed（2 tests）
    // 上次測試時間：2026-09-15 15:06:33 +08:00
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

    // 測試案例：TC-E-AUTH-002（Backend Unit 邊界；整體案例仍因 Frontend/API/SQL 未齊而維持 Planned）
    // 測試結果：Passed（9/9 Backend Unit 邊界組合）
    // 上次測試時間：2026-09-15 15:06:33 +08:00
    [TestCaseSource(nameof(RegistrationBoundaryCases))]
    public void 註冊欄位邊界應符合規格(RegisterRequest request, string? expectedErrorField)
    {
        IReadOnlyDictionary<string, string[]> errors = RegistrationValidator.Validate(request);

        if (expectedErrorField is null)
        {
            errors.Should().BeEmpty();
            return;
        }

        errors.Keys.Should().ContainSingle().Which.Should().Be(expectedErrorField);
    }
}
