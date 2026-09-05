# E-R Diagram

本文件依目前 EF Core model 與 migrations 繪製。為維持可讀性，將 21 張資料表拆成 Identity／RBAC、專案／Task、系統紀錄三張圖。完整欄位定義請參閱 [TableSchema.md](TableSchema.md)。

## Identity 與 RBAC

```mermaid
erDiagram
    ACCOUNTS {
        uniqueidentifier Id PK
        nvarchar UserName
        nvarchar Email
        bit EmailConfirmed
        bit IsEnabled
        int TokenVersion
    }
    ROLES {
        uniqueidentifier Id PK
        nvarchar Name
        nvarchar Description
    }
    ACCOUNT_ROLES {
        uniqueidentifier UserId PK,FK,UK
        uniqueidentifier RoleId PK,FK
    }
    FUNCTIONS {
        uniqueidentifier Id PK
        nvarchar Code UK
        nvarchar Name
    }
    ROLE_FUNCTIONS {
        uniqueidentifier RoleId PK,FK
        uniqueidentifier FunctionId PK,FK
    }
    REFRESH_TOKENS {
        uniqueidentifier Id PK
        uniqueidentifier AccountId FK
        uniqueidentifier FamilyId
        nvarchar TokenHash UK
        datetimeoffset ExpiresAt
    }
    USER_PREFERENCES {
        uniqueidentifier AccountId PK,FK
        bit SkipBatchConfirmation
    }
    ACCOUNT_CLAIMS {
        int Id PK
        uniqueidentifier UserId FK
    }
    ACCOUNT_LOGINS {
        nvarchar LoginProvider PK
        nvarchar ProviderKey PK
        uniqueidentifier UserId FK
    }
    ACCOUNT_TOKENS {
        uniqueidentifier UserId PK,FK
        nvarchar LoginProvider PK
        nvarchar Name PK
    }
    ROLE_CLAIMS {
        int Id PK
        uniqueidentifier RoleId FK
    }

    ACCOUNTS ||--o{ ACCOUNT_ROLES : has
    ROLES ||--o{ ACCOUNT_ROLES : assigns
    ROLES ||--o{ ROLE_FUNCTIONS : grants
    FUNCTIONS ||--o{ ROLE_FUNCTIONS : maps
    ACCOUNTS ||--o{ REFRESH_TOKENS : owns
    ACCOUNTS ||--o| USER_PREFERENCES : configures
    ACCOUNTS ||--o{ ACCOUNT_CLAIMS : has
    ACCOUNTS ||--o{ ACCOUNT_LOGINS : uses
    ACCOUNTS ||--o{ ACCOUNT_TOKENS : stores
    ROLES ||--o{ ROLE_CLAIMS : has
```

`ACCOUNT_ROLES.UserId` 除了是複合 PK 的一部分，另有 UNIQUE index，因此一個 Account 最多只有一筆系統角色關聯。`ROLE_FUNCTIONS` 則讓一個 Role 對應多個 Function。

## 專案、成員、Task 與留言

```mermaid
erDiagram
    ACCOUNTS {
        uniqueidentifier Id PK
        nvarchar UserName
    }
    PROJECTS {
        uniqueidentifier Id PK
        nvarchar Code UK
        uniqueidentifier OwnerAccountId FK
        nvarchar Status
        int VersionNumber
        rowversion RowVersion
        datetimeoffset DeletedAt
    }
    PROJECT_MEMBERS {
        uniqueidentifier ProjectId PK,FK
        uniqueidentifier AccountId PK,FK
        datetimeoffset JoinedAt
    }
    PROJECT_ROLES {
        uniqueidentifier Id PK
        nvarchar Code UK
        nvarchar Name
    }
    PROJECT_MEMBER_ROLES {
        uniqueidentifier ProjectId PK,FK
        uniqueidentifier AccountId PK,FK
        uniqueidentifier ProjectRoleId PK,FK
    }
    TASK_ITEMS {
        uniqueidentifier Id PK
        nvarchar Code UK
        uniqueidentifier ProjectId FK
        uniqueidentifier CreatedByAccountId FK
        uniqueidentifier AssignedAccountId FK
        nvarchar Status
        rowversion RowVersion
        datetimeoffset DeletedAt
    }
    TASK_ITEM_COMMENTS {
        uniqueidentifier Id PK
        uniqueidentifier TaskItemId FK
        uniqueidentifier AuthorAccountId FK
        nvarchar Content
        rowversion RowVersion
        datetimeoffset DeletedAt
    }
    TASK_ITEM_HISTORIES {
        uniqueidentifier Id PK
        uniqueidentifier TaskItemId FK
        uniqueidentifier ActorAccountId FK
        nvarchar Action
        nvarchar Snapshot
    }

    ACCOUNTS ||--o{ PROJECTS : owns
    PROJECTS ||--o{ PROJECT_MEMBERS : contains
    ACCOUNTS ||--o{ PROJECT_MEMBERS : joins
    PROJECT_MEMBERS ||--o{ PROJECT_MEMBER_ROLES : receives
    PROJECT_ROLES ||--o{ PROJECT_MEMBER_ROLES : classifies
    PROJECTS ||--o{ TASK_ITEMS : contains
    ACCOUNTS ||--o{ TASK_ITEMS : creates
    ACCOUNTS ||--o{ TASK_ITEMS : assigned_to
    TASK_ITEMS ||--o{ TASK_ITEM_COMMENTS : has
    ACCOUNTS ||--o{ TASK_ITEM_COMMENTS : writes
    TASK_ITEMS ||--o{ TASK_ITEM_HISTORIES : records
    ACCOUNTS ||--o{ TASK_ITEM_HISTORIES : performs
```

`PROJECT_MEMBERS` 的 `(ProjectId, AccountId)` 複合 PK 保證一個帳號在同一專案只有一筆 membership；`PROJECT_MEMBER_ROLES` 的三欄複合 PK 允許該 membership 擁有多個不同 Project Role。

圖中的 `Code UK` 對 Projects 與 TaskItems 實際是帶有 `[DeletedAt] IS NULL` 的 filtered unique index。軟刪除資料不參與唯一性判斷。

## 系統紀錄

```mermaid
erDiagram
    ACCOUNTS {
        uniqueidentifier Id PK
        nvarchar UserName
    }
    AUDIT_LOGS {
        uniqueidentifier Id PK
        uniqueidentifier ActorAccountId FK
        nvarchar Action
        nvarchar EntityType
        nvarchar EntityId
        datetimeoffset CreatedAt
    }
    EMAIL_MESSAGES {
        uniqueidentifier Id PK
        nvarchar Recipient
        nvarchar Subject
        nvarchar Status
        datetimeoffset CreatedAt
        datetimeoffset SentAt
    }
    BUSINESS_CODE_COUNTERS {
        nvarchar CodeType PK
        date BusinessDate PK
        int LastValue
    }

    ACCOUNTS o|--o{ AUDIT_LOGS : performs
```

- `AuditLogs.ActorAccountId` 可為 null，以支援系統操作；FK 採 `NoAction`，避免刪除帳號時破壞稽核資料。
- `EmailMessages` 目前是獨立寄送紀錄，只保存 Recipient，沒有對 `Accounts` 建立 FK。
- `BusinessCodeCounters` 是無外鍵的 Project／Task UTC 每日計數器，複合主鍵為 `(CodeType, BusinessDate)`。
- `RefreshTokens.ReplacedByTokenId` 是應用層維護的輪替指標，目前不是資料庫 FK，因此未畫成實體關聯。

## 刪除與生命週期

| Aggregate | 行為 |
|---|---|
| Account | Identity 支援表、AccountRole、RefreshToken、Preference cascade；專案、Task、留言、歷史與稽核關聯會限制實體刪除 |
| Project | Entity 與 query filter 已支援軟刪除，但目前沒有公開刪除 API；若實體刪除，ProjectMembers cascade，但 TaskItems Restrict |
| TaskItem | 應用層使用軟刪除；Comments 與 Histories 均 Restrict，不連帶刪除 |
| TaskItemComment | 應用層使用軟刪除，保留資料供稽核 |
