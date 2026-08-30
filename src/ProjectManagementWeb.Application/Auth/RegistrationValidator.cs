using System.ComponentModel.DataAnnotations;

namespace ProjectManagementWeb.Application.Auth;

public static class RegistrationValidator
{
    public static IReadOnlyDictionary<string, string[]> Validate(RegisterRequest request)
    {
        var errors = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        ValidateAccount(request.Account, errors);
        ValidateName(request.Name, errors);
        ValidateEmail(request.Email, errors);
        ValidatePassword(request.Password, errors);
        ValidateConfirmPassword(request.Password, request.ConfirmPassword, errors);

        return errors.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.ToArray(),
            StringComparer.OrdinalIgnoreCase);
    }

    private static void ValidateAccount(string? account, IDictionary<string, List<string>> errors)
    {
        string value = account?.Trim() ?? string.Empty;
        if (value.Length == 0)
        {
            Add(errors, "account", "請輸入帳號。");
            return;
        }
        if (value.Length > RegistrationRules.AccountMaxLength)
        {
            Add(errors, "account", $"帳號不可超過 {RegistrationRules.AccountMaxLength} 個字元。");
        }
        if (value.Any(character => !RegistrationRules.AllowedAccountCharacters.Contains(character)))
        {
            Add(errors, "account", "帳號只能使用英文字母、數字及 - . _ @ +。");
        }
    }

    private static void ValidateName(string? name, IDictionary<string, List<string>> errors)
    {
        string value = name?.Trim() ?? string.Empty;
        if (value.Length == 0)
        {
            Add(errors, "name", "請輸入顯示名稱。");
        }
        else if (value.Length > RegistrationRules.NameMaxLength)
        {
            Add(errors, "name", $"顯示名稱不可超過 {RegistrationRules.NameMaxLength} 個字元。");
        }
    }

    private static void ValidateEmail(string? email, IDictionary<string, List<string>> errors)
    {
        string value = email?.Trim() ?? string.Empty;
        if (value.Length == 0)
        {
            Add(errors, "email", "請輸入 Email。");
            return;
        }
        if (value.Length > RegistrationRules.EmailMaxLength || !new EmailAddressAttribute().IsValid(value))
        {
            Add(errors, "email", "請輸入有效的 Email 格式。");
        }
    }

    private static void ValidatePassword(string? password, IDictionary<string, List<string>> errors)
    {
        if (string.IsNullOrEmpty(password))
        {
            Add(errors, "password", "請輸入密碼。");
            return;
        }
        if (password.Length < RegistrationRules.PasswordMinLength)
        {
            Add(errors, "password", $"密碼至少需要 {RegistrationRules.PasswordMinLength} 個字元。");
        }
        if (!password.Any(char.IsUpper))
        {
            Add(errors, "password", "密碼至少需要一個大寫英文字母。");
        }
        if (!password.Any(char.IsLower))
        {
            Add(errors, "password", "密碼至少需要一個小寫英文字母。");
        }
        if (!password.Any(char.IsDigit))
        {
            Add(errors, "password", "密碼至少需要一個數字。");
        }
        if (!password.Any(character => !char.IsLetterOrDigit(character)))
        {
            Add(errors, "password", "密碼至少需要一個特殊字元。");
        }
    }

    private static void ValidateConfirmPassword(
        string? password,
        string? confirmPassword,
        IDictionary<string, List<string>> errors)
    {
        if (string.IsNullOrEmpty(confirmPassword))
        {
            Add(errors, "confirmPassword", "請再次輸入密碼。");
        }
        else if (!string.Equals(password, confirmPassword, StringComparison.Ordinal))
        {
            Add(errors, "confirmPassword", "兩次輸入的密碼不一致。");
        }
    }

    private static void Add(IDictionary<string, List<string>> errors, string field, string message)
    {
        if (!errors.TryGetValue(field, out List<string>? messages))
        {
            messages = [];
            errors[field] = messages;
        }
        messages.Add(message);
    }
}
