namespace ProjectManagementWeb.Domain.Constants;

public static class SystemRoles
{
    public const string Admin = "Admin";
    public const string Administrator = "Administrator";
    public const string User = "User";
    public const string Viewer = "Viewer";

    public static readonly IReadOnlyCollection<string> All =
        [Admin, Administrator, User, Viewer];
}
