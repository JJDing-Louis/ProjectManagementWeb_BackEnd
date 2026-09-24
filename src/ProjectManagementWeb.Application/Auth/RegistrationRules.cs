namespace ProjectManagementWeb.Application.Auth;

public static class RegistrationRules
{
    public const int AccountMaxLength = 256;
    public const int EmailMaxLength = 256;
    public const int NameMaxLength = 100;
    public const int PasswordMinLength = 10;
    public const string AllowedAccountCharacters =
        "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-._@+";
}
