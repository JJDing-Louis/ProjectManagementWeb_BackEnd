namespace ProjectManagementWeb.Domain.Constants;

public static class SystemFunctions
{
    public const string AccountsRead = "accounts.read";
    public const string AccountsManageRole = "accounts.manage-role";
    public const string AccountsManageStatus = "accounts.manage-status";
    public const string ProjectsRead = "projects.read";
    public const string ProjectsCreate = "projects.create";
    public const string ProjectsManageAll = "projects.manage-all";
    public const string ProjectMembersManageAll = "project-members.manage-all";
    public const string TasksRead = "tasks.read";
    public const string TasksCreate = "tasks.create";
    public const string TasksUpdateAny = "tasks.update-any";
    public const string TasksUpdateAssigned = "tasks.update-assigned";
    public const string TasksDelete = "tasks.delete";
    public const string CommentsRead = "comments.read";
    public const string CommentsCreate = "comments.create";
    public const string CommentsUpdateOwn = "comments.update-own";
    public const string CommentsDeleteOwn = "comments.delete-own";
    public const string PreferencesReadOwn = "preferences.read-own";
    public const string PreferencesUpdateOwn = "preferences.update-own";

    public static readonly IReadOnlyCollection<string> All =
    [
        AccountsRead, AccountsManageRole, AccountsManageStatus,
        ProjectsRead, ProjectsCreate, ProjectsManageAll, ProjectMembersManageAll,
        TasksRead, TasksCreate, TasksUpdateAny, TasksUpdateAssigned, TasksDelete,
        CommentsRead, CommentsCreate, CommentsUpdateOwn, CommentsDeleteOwn,
        PreferencesReadOwn, PreferencesUpdateOwn
    ];
}
