# 系統架構

本文件依目前 repository 的程式碼描述 ProjectManagementWeb BackEnd。圖採 C4 Model Level 3（Component Diagram）的視角，重點是 API 容器內的元件、責任與依賴；Vue SPA 與外部基礎設施只作為互動邊界。

## Level 3 Component Diagram

```mermaid
flowchart LR
    spa[Vue SPA<br/>外部前端應用]
    smtp[Gmail SMTP<br/>外部郵件服務]
    sql[(SQL Server 2022)]

    subgraph api[ProjectManagementWeb.Api]
        program[Program<br/>DI、Middleware、CORS、OpenAPI]
        controllers[Controllers<br/>HTTP 路由、Model Binding、授權入口]
        exceptionHandler[ApiExceptionHandler<br/>Problem Details]
        currentUser[HttpCurrentUser<br/>解析 JWT Claims]
    end

    subgraph application[ProjectManagementWeb.Application]
        contracts[Contracts 與 DTOs<br/>Request、Response、PagedResult]
        ports[Application Interfaces<br/>IAuthService、IUserService、IProjectService<br/>ITaskService、ICommentService、IPreferenceService]
        results[ServiceResult<br/>ServiceError]
    end

    subgraph infrastructure[ProjectManagementWeb.Infrastructure]
        services[Application Services<br/>Auth、User、Project、Task、Comment、Preference]
        support[ServiceSupport<br/>Function 與專案資源授權]
        identity[ASP.NET Core Identity<br/>UserManager、RoleManager]
        token[JWT 與 Refresh Token<br/>JwtTokenIssuer、Bearer Validation]
        email[SmtpEmailGateway]
        dbContext[ApplicationDbContext<br/>EF Core Mapping、Query Filters]
        migrations[EF Core Migrations<br/>Schema 版本來源]
        bootstrap[DatabaseBootstrapper<br/>初始 Admin]
    end

    subgraph domain[ProjectManagementWeb.Domain]
        entities[Entities<br/>Project、TaskItem、Membership、Audit 等]
        policyData[Constants 與 Enums<br/>Roles、Functions、Statuses]
    end

    spa -->|HTTPS JSON、Bearer JWT、CSRF Header| controllers
    program --> controllers
    program --> exceptionHandler
    controllers --> ports
    controllers --> contracts
    controllers --> currentUser
    exceptionHandler --> results
    ports -.由 Infrastructure 實作.-> services
    services --> results
    services --> support
    services --> identity
    services --> token
    services --> email
    services --> dbContext
    support --> currentUser
    support --> dbContext
    identity --> dbContext
    token --> dbContext
    email -->|SMTP over TLS| smtp
    dbContext --> entities
    dbContext --> policyData
    dbContext -->|EF Core| sql
    migrations -->|更新 Schema| sql
    bootstrap --> identity
    bootstrap --> dbContext
```

## 元件職責

| 元件 | 主要職責 | 不負責的事項 |
|---|---|---|
| `Program` | 組裝 DI、Problem Details、CSRF、CORS、OpenAPI、Authentication 與路由管線 | 業務規則與資料存取 |
| Controllers | 接收 HTTP request、套用 `[Authorize]`／Function policy、呼叫 Application interface、轉換 HTTP status | 直接操作 `DbContext` |
| Application contracts | 定義 API DTO、service interface、分頁與統一服務結果 | EF Core、SMTP 或 HTTP 實作 |
| Infrastructure services | 實作帳號、偏好、專案、Task、留言等 use case 與 transaction boundary | HTTP response 格式 |
| `ServiceSupport` | 計算全域 Function 與專案成員／ProjectManager 的資源權限 | 驗證密碼與簽發 token |
| Identity／Token | 帳號驗證、唯一系統角色、JWT 簽發與 token-version、Refresh Token rotation | 專案角色授權 |
| `ApplicationDbContext` | EF Core mapping、關聯、索引、query filter 與 seed data | 執行 HTTP 層驗證 |
| Domain | 業務實體、固定角色／Function 與狀態列舉 | 基礎設施連線與框架組態 |
| `SmtpEmailGateway` | 經 SMTP 寄送 Email 驗證信 | 決定註冊 transaction 是否成功 |

## 主要執行流程

### Bearer 業務請求

```mermaid
sequenceDiagram
    participant SPA as Vue SPA
    participant API as ASP.NET Core Pipeline
    participant JWT as JWT Validation
    participant Controller as Controller
    participant Service as Application Service
    participant DB as SQL Server

    SPA->>API: Authorization Bearer accessToken
    API->>JWT: 驗證 RSA 簽章、期限、帳號狀態、token-version
    JWT->>DB: 查詢帳號與 TokenVersion
    DB-->>JWT: 帳號狀態
    JWT-->>API: ClaimsPrincipal
    API->>Controller: 已驗證 request
    Controller->>Service: DTO 與目前帳號
    Service->>DB: EF Core query 或 transaction
    DB-->>Service: Entity 或更新結果
    Service-->>Controller: ServiceResult
    Controller-->>SPA: JSON 或 RFC 7807 Problem Details
```

### Cookie-sensitive Auth 請求

```mermaid
sequenceDiagram
    participant SPA as Vue SPA
    participant Security as SecurityController
    participant Auth as AuthController
    participant Service as AuthService
    participant DB as SQL Server

    SPA->>Security: GET /api/v1/security/csrf-token
    Security-->>SPA: PMW-CSRF Cookie 與 request token
    SPA->>Auth: POST Auth API + X-CSRF-TOKEN
    Auth->>Auth: ValidateAntiForgeryToken
    Auth->>Service: 登入、註冊、Refresh 或 Logout
    Service->>DB: Identity 與 RefreshTokens
    DB-->>Service: 處理結果
    Service-->>Auth: Access Token 與 Refresh Token 結果
    Auth-->>SPA: JSON + PMW-REFRESH HttpOnly Cookie
```

## 依賴方向與現況說明

```text
Api -> Application -> Domain
Api -> Infrastructure -> Application / Domain
Infrastructure -> Application / Domain
```

- `Application` 只保存 contracts 與 abstractions；目前 use case 的具體類別位於 `Infrastructure/Services`。這是現行實作，不等同於將 use case 完全置於 Application layer 的嚴格 Clean Architecture。
- Controller 不直接依賴 EF Core；具體 service 經 DI 以 Application interface 注入。
- SQL Server schema 只由已提交的 EF Core migrations 演進，不使用 `EnsureCreated`。
- `Project`、`TaskItem`、`TaskItemComment` 使用 `rowversion` 進行 optimistic concurrency control。
- Project、Task 與 Comment model 皆以 `DeletedAt` 搭配 EF query filter；目前公開刪除 API 只涵蓋 Task 與 Comment，Project 尚未提供刪除 endpoint。稽核、歷史與既有留言資料不隨 Task 軟刪除而消失。

## 部署元件與啟動順序

```mermaid
flowchart LR
    sqlContainer[SQL Server Container]
    init[database-init<br/>建立最低權限登入]
    migration[migration runner<br/>dotnet ef database update]
    apiContainer[API Container]
    volume[(SQL Data Volume)]
    keys[(Data Protection Keys Volume)]

    sqlContainer --> volume
    sqlContainer -->|Health Check 通過| init
    init -->|登入與權限建立完成| migration
    migration -->|Migration 成功| apiContainer
    apiContainer --> keys
    apiContainer --> sqlContainer
```

Compose 將 migration 與 API 分離，migration 帳號擁有 schema 變更權限，API 帳號只保留執行期所需的資料存取權限。Apple Silicon 本機環境以 `linux/amd64` 模擬執行 SQL Server 2022 image，此組合不應被視為正式生產環境的支援基準。
