using System.ComponentModel.DataAnnotations;

namespace ProjectManagementWeb.Application.Users;

public static class OwnProfileValidator
{
    private static readonly PhoneAttribute PhoneValidator = new();

    public static IReadOnlyDictionary<string, string[]> Validate(UpdateOwnProfileRequest request)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        string name = request.Name?.Trim() ?? string.Empty;
        string? phoneNumber = NormalizePhoneNumber(request.PhoneNumber);

        if (name.Length == 0)
        {
            errors["name"] = ["請輸入顯示名稱。"];
        }
        else if (name.Length > OwnProfileRules.NameMaxLength)
        {
            errors["name"] = [$"顯示名稱不可超過 {OwnProfileRules.NameMaxLength} 個字元。"];
        }

        if (phoneNumber is not null && phoneNumber.Length > OwnProfileRules.PhoneNumberMaxLength)
        {
            errors["phoneNumber"] = [$"電話號碼不可超過 {OwnProfileRules.PhoneNumberMaxLength} 個字元。"];
        }
        else if (phoneNumber is not null && !PhoneValidator.IsValid(phoneNumber))
        {
            errors["phoneNumber"] = ["請輸入有效的電話號碼格式。"];
        }

        return errors;
    }

    public static string? NormalizePhoneNumber(string? phoneNumber) =>
        string.IsNullOrWhiteSpace(phoneNumber) ? null : phoneNumber.Trim();
}
