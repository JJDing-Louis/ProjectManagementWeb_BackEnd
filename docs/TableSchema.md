# 資料庫 Table Schema

本文件依 `ApplicationDbContext`、全部現行 migrations 與 model snapshot 整理。正式 schema 版本來源仍是 `src/ProjectManagementWeb.Infrastructure/Persistence/Migrations/`，本文件用於閱讀與前後端協作，不取代 migration。

## 設計慣例

| 項目 | 規則 |
|---|---|
| 主鍵 | 業務資料主要使用 `uniqueidentifier`；Identity claim 使用 `int` |
| 時間 | 使用 `datetimeoffset`，程式統一以 UTC 處理 |
| 文字 | 使用有長度限制的 `nvarchar`；快照、Email 本文等大內容使用 `nvarchar(max)` |
| 軟刪除 | `Projects`、`TaskItems`、`TaskItemComments` 使用 `DeletedAt` |
| 並行控制 | `Projects`、`TaskItems`、`TaskItemComments` 使用 SQL Server `rowversion` |
| Enum | 以字串儲存，例如 `Pending`、`InProgress` |
| Schema 演進 | 只使用 EF Core migrations，不使用 `EnsureCreated` |

## Identity 與 RBAC

### Accounts

ASP.NET Core Identity 帳號資料。

| 欄位 | 型別 | Null | Key／Constraint | 說明 |
|---|---|---:|---|---|
| `Id` | `uniqueidentifier` | 否 | PK | 帳號識別碼 |
| `UserName` | `nvarchar(256)` | 是 |  | 登入帳號 |
| `NormalizedUserName` | `nvarchar(256)` | 是 | UNIQUE filtered | 正規化登入帳號 |
| `Email` | `nvarchar(256)` | 是 |  | Email |
| `NormalizedEmail` | `nvarchar(256)` | 否 | UNIQUE | 正規化 Email；migration 遇 NULL／重複資料時 fail-fast |
| `EmailConfirmed` | `bit` | 否 |  | Email 是否驗證 |
| `PasswordHash` | `nvarchar(max)` | 是 |  | Identity 密碼雜湊 |
| `SecurityStamp` | `nvarchar(max)` | 是 |  | Identity security stamp |
| `ConcurrencyStamp` | `nvarchar(max)` | 是 |  | Identity concurrency stamp |
| `PhoneNumber` | `nvarchar(max)` | 是 |  | 電話 |
| `PhoneNumberConfirmed` | `bit` | 否 |  | 電話是否驗證 |
| `TwoFactorEnabled` | `bit` | 否 |  | 雙因素旗標 |
| `LockoutEnd` | `datetimeoffset` | 是 |  | 鎖定期限 |
| `LockoutEnabled` | `bit` | 否 |  | 是否啟用鎖定 |
| `AccessFailedCount` | `int` | 否 |  | 登入失敗次數 |
| `Name` | `nvarchar(100)` | 是 |  | 顯示姓名 |
| `Remark` | `nvarchar(500)` | 是 |  | 備註 |
| `IsEnabled` | `bit` | 否 |  | 帳號是否啟用 |
| `TokenVersion` | `int` | 否 |  | JWT 失效版本 |

`PhoneNumber` 的資料庫型別沿用 Identity 的 `nvarchar(max)`，但個人資料 API 以 Application validator 限制為最多 30 字。`IsBootstrapAdmin` 不是資料庫欄位，而是以 `BootstrapAdmin:Account` 與 `NormalizedUserName` 即時計算。

### Roles

| 欄位 | 型別 | Null | Key／Constraint | 說明 |
|---|---|---:|---|---|
| `Id` | `uniqueidentifier` | 否 | PK | 系統角色 ID |
| `Name` | `nvarchar(256)` | 是 |  | `Admin`、`Administrator`、`User`、`Viewer` |
| `NormalizedName` | `nvarchar(256)` | 是 | UNIQUE filtered | Identity 正規化名稱 |
| `ConcurrencyStamp` | `nvarchar(max)` | 是 |  | Identity concurrency stamp |
| `Description` | `nvarchar(max)` | 是 |  | 角色說明；目前未設定最大長度 |

### AccountRoles

| 欄位 | 型別 | Null | Key／Constraint | 說明 |
|---|---|---:|---|---|
| `UserId` | `uniqueidentifier` | 否 | PK、FK → `Accounts.Id`、UNIQUE | 一個帳號最多一筆角色關聯 |
| `RoleId` | `uniqueidentifier` | 否 | PK、FK → `Roles.Id` | 系統角色 |

刪除 Account 或 Role 時由 Identity mapping cascade 刪除關聯。複合 PK 防止同一配對重複，`UserId` UNIQUE index 額外保證單一系統角色。

### Functions

| 欄位 | 型別 | Null | Key／Constraint | 說明 |
|---|---|---:|---|---|
| `Id` | `uniqueidentifier` | 否 | PK | Function ID |
| `Code` | `nvarchar(100)` | 否 | UNIQUE | 授權 claim code |
| `Name` | `nvarchar(100)` | 否 |  | 顯示名稱；目前 seed 與 Code 相同 |

### RoleFunctions

| 欄位 | 型別 | Null | Key／Constraint | 說明 |
|---|---|---:|---|---|
| `RoleId` | `uniqueidentifier` | 否 | PK、FK → `Roles.Id` | 系統角色 |
| `FunctionId` | `uniqueidentifier` | 否 | PK、FK → `Functions.Id` | Function |

兩端刪除皆 cascade。資料由 migration 固定 seed，目前不提供動態管理 API。

### RefreshTokens

| 欄位 | 型別 | Null | Key／Constraint | 說明 |
|---|---|---:|---|---|
| `Id` | `uniqueidentifier` | 否 | PK | Refresh Token 紀錄 |
| `AccountId` | `uniqueidentifier` | 否 | FK → `Accounts.Id` | 所屬帳號，刪除帳號時 cascade |
| `FamilyId` | `uniqueidentifier` | 否 | INDEX with AccountId | Token family |
| `TokenHash` | `nvarchar(64)` | 否 | UNIQUE | 原始 token 的 SHA-256 hash |
| `ExpiresAt` | `datetimeoffset` | 否 |  | 到期時間 |
| `CreatedAt` | `datetimeoffset` | 否 |  | 建立時間 |
| `RevokedAt` | `datetimeoffset` | 是 |  | 撤銷時間 |
| `ReplacedByTokenId` | `uniqueidentifier` | 是 | self-FK → `RefreshTokens.Id` | 輪替後的新 token；Delete NoAction |

### UserPreferences

| 欄位 | 型別 | Null | Key／Constraint | 說明 |
|---|---|---:|---|---|
| `AccountId` | `uniqueidentifier` | 否 | PK、FK → `Accounts.Id` | 每帳號最多一筆偏好，刪除帳號時 cascade |
| `SkipBatchConfirmation` | `bit` | 否 |  | 是否略過批次操作確認 |

### Identity 支援資料表

| Table | 主鍵 | 主要 Foreign Key／用途 |
|---|---|---|
| `AccountClaims` | `Id` (`int`) | `UserId` → `Accounts.Id`；帳號 claims |
| `AccountLogins` | (`LoginProvider`, `ProviderKey`) | `UserId` → `Accounts.Id`；外部登入 |
| `AccountTokens` | (`UserId`, `LoginProvider`, `Name`) | `UserId` → `Accounts.Id`；Identity token |
| `RoleClaims` | `Id` (`int`) | `RoleId` → `Roles.Id`；角色 claims |

上述關聯皆沿用 Identity 的 cascade delete。系統實際 Function 授權以 `RoleFunctions` 為主。

## 專案與成員

### Projects

| 欄位 | 型別 | Null | Key／Constraint | 說明 |
|---|---|---:|---|---|
| `Id` | `uniqueidentifier` | 否 | PK | 內部 ID |
| `Code` | `nvarchar(50)` | 否 | UNIQUE filtered | 未刪除資料的顯示編號唯一 |
| `Name` | `nvarchar(200)` | 否 |  | 專案名稱 |
| `Description` | `nvarchar(4000)` | 是 |  | 說明 |
| `OwnerAccountId` | `uniqueidentifier` | 否 | FK → `Accounts.Id`、INDEX with Status | Owner，刪除採 Restrict |
| `TimeZoneId` | `nvarchar(100)` | 否 |  | Project 的合法 IANA timezone ID；新建時必填 |
| `Status` | `nvarchar(30)` | 否 |  | `Pending`、`Active`、`Completed`、`Archived` |
| `VersionNumber` | `int` | 否 | DEFAULT 1 | 使用者可讀的專案版本；每次更新專案基本資料時遞增 |
| `CreatedAt` | `datetimeoffset` | 否 |  | 建立時間 |
| `UpdatedAt` | `datetimeoffset` | 否 |  | 更新時間 |
| `DeletedAt` | `datetimeoffset` | 是 | query filter | 軟刪除時間 |
| `DeletedByAccountId` | `uniqueidentifier` | 是 | FK → `Accounts.Id` | 軟刪除操作者；Delete NoAction |
| `RowVersion` | `rowversion` | 否 | concurrency token | API 以 Base64 傳輸 |

### ProjectMembers

| 欄位 | 型別 | Null | Key／Constraint | 說明 |
|---|---|---:|---|---|
| `ProjectId` | `uniqueidentifier` | 否 | PK、FK → `Projects.Id` | 專案；專案實體刪除時 cascade |
| `AccountId` | `uniqueidentifier` | 否 | PK、FK → `Accounts.Id` | 帳號；刪除採 Restrict |
| `JoinedAt` | `datetimeoffset` | 否 |  | 加入時間 |

複合 PK 同時保證同一帳號在同一專案只有一筆 member。

### ProjectRoles

| 欄位 | 型別 | Null | Key／Constraint | 說明 |
|---|---|---:|---|---|
| `Id` | `uniqueidentifier` | 否 | PK | 專案角色 ID |
| `Code` | `nvarchar(100)` | 否 | UNIQUE | `ProjectManager`、`FrontendDeveloper`、`BackendDeveloper`、`SystemAnalyst`、`Member` |
| `Name` | `nvarchar(100)` | 否 |  | 顯示名稱；目前 seed 與 Code 相同 |

### ProjectMemberRoles

| 欄位 | 型別 | Null | Key／Constraint | 說明 |
|---|---|---:|---|---|
| `ProjectId` | `uniqueidentifier` | 否 | PK、複合 FK → `ProjectMembers` | 專案 |
| `AccountId` | `uniqueidentifier` | 否 | PK、複合 FK → `ProjectMembers` | 成員 |
| `ProjectRoleId` | `uniqueidentifier` | 否 | PK、FK → `ProjectRoles.Id` | 專案角色，刪除採 Restrict |

三欄複合 PK 允許同一成員擁有多個不同專案角色，並防止同一角色重複。

## Task 與留言

### TaskItems

| 欄位 | 型別 | Null | Key／Constraint | 說明 |
|---|---|---:|---|---|
| `Id` | `uniqueidentifier` | 否 | PK | 內部 ID |
| `Code` | `nvarchar(50)` | 否 | UNIQUE filtered | 未刪除資料的顯示編號唯一；目前為全系統範圍 |
| `ProjectId` | `uniqueidentifier` | 否 | FK → `Projects.Id`、INDEX with Status/Deadline | 所屬專案，刪除採 Restrict |
| `Title` | `nvarchar(300)` | 否 |  | 標題 |
| `Description` | `nvarchar(max)` | 是 | EF logical max 8000 | 說明；SQL Server 對大於 4000 的 Unicode 長度映射為 max |
| `CreatedByAccountId` | `uniqueidentifier` | 否 | FK → `Accounts.Id` | 建立者，刪除採 Restrict |
| `AssignedAccountId` | `uniqueidentifier` | 否 | FK → `Accounts.Id`、INDEX | 指派成員，刪除採 Restrict |
| `StartAt` | `datetimeoffset` | 否 |  | 開始時間 |
| `Deadline` | `datetimeoffset` | 否 |  | 截止時間 |
| `Status` | `nvarchar(30)` | 否 |  | `Pending`、`InProgress`、`Blocked`、`Completed` |
| `CreatedAt` | `datetimeoffset` | 否 |  | 建立時間 |
| `UpdatedAt` | `datetimeoffset` | 否 |  | 更新時間 |
| `DeletedAt` | `datetimeoffset` | 是 | query filter | 軟刪除時間 |
| `RowVersion` | `rowversion` | 否 | concurrency token | API 以 Base64 傳輸 |

### TaskItemComments

| 欄位 | 型別 | Null | Key／Constraint | 說明 |
|---|---|---:|---|---|
| `Id` | `uniqueidentifier` | 否 | PK | 留言 ID |
| `TaskItemId` | `uniqueidentifier` | 否 | FK → `TaskItems.Id`、INDEX with CreatedAt | 所屬 Task，刪除採 Restrict |
| `AuthorAccountId` | `uniqueidentifier` | 否 | FK → `Accounts.Id` | 作者，刪除採 Restrict |
| `Content` | `nvarchar(2000)` | 否 |  | trim 後 1–2000 字 |
| `CreatedAt` | `datetimeoffset` | 否 |  | 建立時間 |
| `UpdatedAt` | `datetimeoffset` | 否 |  | 更新時間 |
| `DeletedAt` | `datetimeoffset` | 是 | query filter | 軟刪除時間 |
| `RowVersion` | `rowversion` | 否 | concurrency token | API 以 Base64 傳輸 |

### TaskItemHistories

| 欄位 | 型別 | Null | Key／Constraint | 說明 |
|---|---|---:|---|---|
| `Id` | `uniqueidentifier` | 否 | PK | 歷史 ID |
| `TaskItemId` | `uniqueidentifier` | 否 | FK → `TaskItems.Id`、INDEX with CreatedAt | Task，刪除採 Restrict |
| `ActorAccountId` | `uniqueidentifier` | 否 | FK → `Accounts.Id` | 操作者，刪除採 Restrict |
| `Action` | `nvarchar(30)` | 否 |  | 操作名稱 |
| `Snapshot` | `nvarchar(max)` | 否 |  | JSON 快照 |
| `CreatedAt` | `datetimeoffset` | 否 |  | 建立時間 |

## 系統紀錄

### BusinessCodeCounters

Project 與 Task 的 UTC 每日業務編號計數器。產生編號與建立實體使用同一個 Serializable transaction，並以 SQL Server `UPDLOCK`、`HOLDLOCK` 保護同日計數器。

| 欄位 | 型別 | Null | Key／Constraint | 說明 |
|---|---|---:|---|---|
| `CodeType` | `nvarchar(20)` | 否 | PK、CHECK | 僅允許 `Project`、`Task` |
| `BusinessDate` | `date` | 否 | PK | `TimeProvider.GetUtcNow()` 對應的 UTC 日期 |
| `LastValue` | `int` | 否 | CHECK 1–999999 | 當日最後已使用流水號 |

格式為 `PRJ-YYYYMMDD######` 與 `TASK-YYYYMMDD######`。兩類型每日各自從 `000001` 起算；超過 `999999` 時 API 回傳 `daily_code_limit_exceeded`，交易失敗時計數器與實體一併 rollback。

### AuditLogs

| 欄位 | 型別 | Null | Key／Constraint | 說明 |
|---|---|---:|---|---|
| `Id` | `uniqueidentifier` | 否 | PK | 稽核 ID |
| `ActorAccountId` | `uniqueidentifier` | 是 | FK → `Accounts.Id` | 可為系統操作；刪除 Account 時 NoAction |
| `Action` | `nvarchar(100)` | 否 |  | 操作名稱 |
| `EntityType` | `nvarchar(100)` | 否 | INDEX with EntityId/CreatedAt | 目標類型 |
| `EntityId` | `nvarchar(100)` | 否 | INDEX with EntityType/CreatedAt | 目標 ID |
| `BeforeData` | `nvarchar(max)` | 是 |  | 變更前 JSON |
| `AfterData` | `nvarchar(max)` | 是 |  | 變更後 JSON |
| `CreatedAt` | `datetimeoffset` | 否 |  | 建立時間 |

### EmailMessages

| 欄位 | 型別 | Null | Key／Constraint | 說明 |
|---|---|---:|---|---|
| `Id` | `uniqueidentifier` | 否 | PK | 郵件紀錄 ID |
| `Recipient` | `nvarchar(320)` | 否 |  | 收件人 |
| `Subject` | `nvarchar(500)` | 否 |  | 主旨 |
| `Body` | `nvarchar(max)` | 否 |  | 內容 |
| `Status` | `nvarchar(30)` | 否 | INDEX with CreatedAt | `Pending`、`Sent`、`Failed` |
| `AttemptCount` | `int` | 否 |  | 寄送嘗試次數 |
| `LastError` | `nvarchar(2000)` | 是 |  | 最後失敗原因 |
| `CreatedAt` | `datetimeoffset` | 否 |  | 建立時間 |
| `SentAt` | `datetimeoffset` | 是 |  | 寄送成功時間 |

`EmailMessages` 目前以 Recipient 保存收件資訊，沒有對 `Accounts` 建立 Foreign Key。

### EmailVerificationTokens

| 欄位 | 型別 | Null | Key／Constraint | 說明 |
|---|---|---:|---|---|
| `Id` | `uniqueidentifier` | 否 | PK | Token lifecycle ID |
| `AccountId` | `uniqueidentifier` | 否 | FK → `Accounts.Id`、filtered UNIQUE | 刪除 Account 時 cascade；同帳號最多一個已啟用且未使用／失效的 Token |
| `EmailMessageId` | `uniqueidentifier` | 否 | FK → `EmailMessages.Id`、UNIQUE | 與寄送紀錄一對一，刪除郵件紀錄時 cascade |
| `TokenHash` | `nvarchar(64)` | 否 | UNIQUE | 32-byte opaque token 的 SHA-256 hash，不保存原文 |
| `CreatedAt` | `datetimeoffset` | 否 |  | 建立時間 |
| `ExpiresAt` | `datetimeoffset` | 否 | INDEX | 建立後 3 分鐘到期 |
| `ActivatedAt` | `datetimeoffset` | 是 | filtered UNIQUE 條件 | 郵件成功送出後才啟用 |
| `UsedAt` | `datetimeoffset` | 是 | filtered UNIQUE 條件 | 成功確認時間，只能使用一次 |
| `InvalidatedAt` | `datetimeoffset` | 是 | filtered UNIQUE 條件 | 新 Token 啟用時使舊 Token 失效 |

### EmailVerificationResendAttempts

| 欄位 | 型別 | Null | Key／Constraint | 說明 |
|---|---|---:|---|---|
| `Id` | `uniqueidentifier` | 否 | PK | 重寄嘗試 ID |
| `AccountId` | `uniqueidentifier` | 是 | FK → `Accounts.Id` | 帳號不存在時仍記錄嘗試但不洩漏存在性；刪除 Account 時 cascade |
| `ClientAddressHash` | `nvarchar(64)` | 否 | INDEX with RequestedAt | 來源 IP 的 SHA-256 hash，不保存 IP 原文 |
| `RequestedAt` | `datetimeoffset` | 否 | 複合 INDEX | 請求時間 |
| `Outcome` | `nvarchar(30)` | 否 | INDEX with AccountId/RequestedAt | `Allowed`、`NotEligible`、`CooldownLimited`、`AccountLimited`、`IpLimited` |

資料支援同帳號 60 秒冷卻，以及帳號／來源 IP 各自在滾動 60 分鐘內最多 5 次的限制。只有來源 IP 超限對外回傳 429；其他不可寄送狀態仍回傳通用成功語意，避免帳號枚舉。

### LoginFailureAttempts

| 欄位 | 型別 | Null | Key／Constraint | 說明 |
|---|---|---:|---|---|
| `Id` | `uniqueidentifier` | 否 | PK | 登入安全紀錄 ID |
| `AccountKeyHash` | `nvarchar(64)` | 否 | INDEX with Outcome/OccurredAt | 正規化帳號鍵的 SHA-256 hash，不保存帳號原文 |
| `ClientAddressHash` | `nvarchar(64)` | 否 | INDEX with Outcome/OccurredAt | 來源 IP 的 SHA-256 hash，不保存 IP 原文 |
| `OccurredAt` | `datetimeoffset` | 否 | 複合 INDEX | 發生時間 |
| `Outcome` | `nvarchar(30)` | 否 | 複合 INDEX | `InvalidCredentials` 或 `RateLimited` |

登入限制以 Serializable transaction 計算 15 分鐘滾動視窗；帳號或來源 IP 累積 5 次無效登入後回傳 429。狀態存於 SQL Server，可供多個 API instance 共用。

### ProjectReminderRuns

| 欄位 | 型別 | Null | Key／Constraint | 說明 |
|---|---|---:|---|---|
| `Id` | `uniqueidentifier` | 否 | PK | 掃描執行 ID |
| `ProjectId` | `uniqueidentifier` | 否 | FK → `Projects.Id` | Project；Delete NoAction |
| `ReminderDate` | `date` | 否 | UNIQUE with ProjectId | Project 當地提醒日期 |
| `StartedAt`／`CompletedAt` | `datetimeoffset` | 後者是 |  | 執行起訖時間 |
| `CreatedCount`／`DuplicateCount`／`SkippedCount` | `int` | 否 |  | 掃描摘要 |

### TaskReminders

| 欄位 | 型別 | Null | Key／Constraint | 說明 |
|---|---|---:|---|---|
| `Id` | `uniqueidentifier` | 否 | PK | 提醒 ID，同時作 provider idempotency key |
| `ProjectId` | `uniqueidentifier` | 否 | FK → `Projects.Id` | Project；Delete NoAction |
| `TaskItemId` | `uniqueidentifier` | 否 | FK → `TaskItems.Id`、UNIQUE composite | Task；Delete NoAction |
| `RecipientAccountId` | `uniqueidentifier` | 否 | FK → `Accounts.Id`、UNIQUE composite | 收件人；Delete NoAction |
| `ReminderDate` | `date` | 否 | UNIQUE composite | 與 TaskItemId、RecipientAccountId 組成唯一鍵 |
| `Status` | `nvarchar(30)` | 否 | INDEX with NextAttemptAt | `Pending`、`Processing`、`Retry`、`Sent`、`Failed`、`Cancelled` |
| `AttemptCount`／`RetryCount` | `int` | 否 |  | 初次與重試計數 |
| `NextAttemptAt`／`CreatedAt` | `datetimeoffset` | 否 |  | 下一次可執行時間與建立時間 |
| `ClaimedAt`／`ClaimToken` | `datetimeoffset`／`uniqueidentifier` | 是 |  | 5 分鐘原子 claim lease |
| `SentAt`／`AlertedAt` | `datetimeoffset` | 是 |  | 成功與最終失敗告警時間 |
| `ProviderResponseId` | `nvarchar(500)` | 是 |  | Provider 成功回應 ID |
| `LastError` | `nvarchar(2000)` | 是 |  | 安全化失敗類型 |
| `CancellationReason` | `nvarchar(200)` | 是 |  | 寄送前重查取消原因 |

Hangfire SQL schema 由 migrator 權限帳號初始化；API runtime 設定 `PrepareSchemaIfNecessary=false`，只需既有資料的讀寫權，不取得 DDL 權限。

## 關鍵完整性與風險

- `AccountRoles.UserId` UNIQUE：資料庫層保證每帳號最多一個系統角色。
- `ProjectMembers(ProjectId, AccountId)` PK：每帳號在同一專案只有一筆 membership。
- `ProjectMemberRoles(ProjectId, AccountId, ProjectRoleId)` PK：同一 membership 可有多個專案角色。
- Project／Task Code 由 `BusinessCodeCounters` 產生；既有 filtered unique index 仍只套用未軟刪除資料。產生器不會主動重用軟刪除 Code。
- Task Code 現行唯一索引不包含 `ProjectId`，因此 Code 是全系統唯一，而非專案內唯一。
- `RefreshTokens.ReplacedByTokenId` self-FK 使用 NoAction，避免清理時連鎖刪除 token 歷史。
- `Accounts.NormalizedEmail` 使用 NOT NULL／UNIQUE，並行重複註冊由資料庫保底。
- `ProjectReminderRuns(ProjectId, ReminderDate)` 與 `TaskReminders(TaskItemId, RecipientAccountId, ReminderDate)` UNIQUE，搭配 SQL 原子 claim 防止多 scheduler／worker 重複處理。
- 多數核心 FK 使用 Restrict／NoAction，以避免誤刪歷史資料；若日後加入實體刪除流程，必須先設計明確 transaction 與清理順序。
