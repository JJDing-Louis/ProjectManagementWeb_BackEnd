# ProjectManagementWeb BackEnd

ProjectManagementWeb 的 ASP.NET Core Web API 後端 MVP。採前後端分離、分層架構、SQL Server 與 EF Core migrations，提供帳號驗證、系統／專案角色、專案、Task、留言、偏好與稽核功能。

## 文件導覽

| 文件 | 內容 |
|---|---|
| [API 清單](docs/ApiList.md) | 35 個 `/api/v1` endpoint、授權範圍、request 與 status code |
| [系統架構](docs/Architecture.md) | C4 Level 3 Component Diagram、主要請求流程與 Compose 部署關係 |
| [資料表 Schema](docs/TableSchema.md) | 20 張資料表的欄位、PK、FK、index、刪除行為與現行風險 |
| [E-R Diagram](docs/E-R_Diagram.md) | Identity／RBAC、專案／Task 與系統紀錄關聯圖 |
| [前端／API 契約](docs/FrontendContract.md) | Vue／TypeScript 型別、JWT、CSRF、refresh、rowversion 與錯誤處理 |

## 技術與架構

- .NET 10、ASP.NET Core Controller-based Web API
- ASP.NET Core Identity（Guid 主鍵）
- EF Core 10、SQL Server 2022、migration 版本控制
- RSA JWT Access Token（15 分鐘）與 opaque Refresh Token（7 天、HttpOnly Cookie、rotation）
- OpenAPI 3、Swagger UI
- NUnit 4、Moq、Bogus、FluentAssertions 7.2.2
- Docker multi-stage build 與 Docker Compose

Solution 依賴方向：

```text
Api -> Application -> Domain
Api -> Infrastructure -> Application / Domain
```

目前具體 use case 實作位於 Infrastructure services，透過 Application interfaces 提供給 Controller；Controller 不直接操作 EF Core。

## 專案結構

```text
ProjectManagementWeb_BackEnd/
├── src/
│   ├── ProjectManagementWeb.Api/              # Controllers、HTTP pipeline、授權入口
│   ├── ProjectManagementWeb.Application/      # Contracts、DTO、service interfaces
│   ├── ProjectManagementWeb.Domain/           # Entities、roles、functions、enums
│   └── ProjectManagementWeb.Infrastructure/   # EF Core、Identity、JWT、SMTP、services
├── tests/
│   ├── ProjectManagementWeb.UnitTests/
│   └── ProjectManagementWeb.IntegrationTests/
├── docs/                                      # API、架構、Schema、ER 與前端契約
├── Dockerfile
└── compose.yaml
```

API 使用傳統 `Program.Main` 進入點，不使用 Top-level statements；組態方法集中在 `Program` 類別中。

## 權限模型

- 每個帳號最多一個系統角色，資料庫以 `AccountRoles.UserId` UNIQUE index 強制保證。
- 固定系統角色：`Admin`、`Administrator`、`User`、`Viewer`。
- 註冊帳號固定為 `Viewer`；Email 未驗證仍可登入，但 token 只包含 Viewer 的 Function。
- 每個帳號在同一專案只有一筆 `ProjectMember`，可透過 `ProjectMemberRoles` 擁有多個專案角色。
- `ProjectManager` 採資源型授權；`Viewer` 即使被誤配為 ProjectManager 也不能寫入。
- 異動系統角色或停用帳號會讓既有 JWT 的 token version 失效，並撤銷全部 Refresh Token。

## 本機執行

需要 .NET SDK 10.0.201 與 SQL Server。連線字串、JWT 私鑰、SMTP 帳密應使用環境變數或 User Secrets，不要寫入設定檔：

```bash
export ConnectionStrings__DefaultConnection='Server=localhost,1433;Database=ProjectManagementWeb;User Id=pmw_app;Password=...;Encrypt=True;TrustServerCertificate=True'
export Jwt__PrivateKeyPem="$(cat /path/to/private-key.pem)"
dotnet tool restore
dotnet ef database update --project src/ProjectManagementWeb.Infrastructure --startup-project src/ProjectManagementWeb.Api
dotnet run --project src/ProjectManagementWeb.Api
```

Development 預設提供：

- Health：`http://localhost:5080/health`（實際 port 依 launch profile）
- OpenAPI：`/openapi/v1.json`
- Swagger UI：`/swagger`

Production 預設不公開 OpenAPI；若確有需要，設定 `OpenApi__Enabled=true`。

## Docker Compose

先複製 `.env.example` 為 `.env` 並替換所有密碼與私鑰，再執行：

```bash
docker compose config
docker compose up --build
```

啟動順序是 SQL Server health check、建立 migration／API 專用登入、套用 EF migration、啟動 API。SA 只用於初始化；migration 使用 `pmw_migrator`，API 使用只有資料讀寫權限的 `pmw_app`。

首次部署可透過 `BOOTSTRAP_ADMIN_ACCOUNT`、`BOOTSTRAP_ADMIN_EMAIL`、`BOOTSTRAP_ADMIN_PASSWORD` 建立第一位已驗證 Admin；帳號存在後即不再修改。完成初始化後應從部署環境移除 Bootstrap 密碼。

Apple Silicon 使用 `platform: linux/amd64` 執行 SQL Server 2022 Linux image，仰賴 Docker 的 x64 模擬；Microsoft 不正式支援此模擬環境，因此正式環境應使用受支援的 x64 Linux 主機或受管理 SQL Server。

## CSRF 與 Auth 呼叫順序

SPA 先呼叫 `GET /api/v1/security/csrf-token`，保存回傳的 request token；呼叫 register、login、refresh、logout、Email confirm 或 resend 時，將 token 放入 `X-CSRF-TOKEN` header。Access Token 由前端保存在記憶體，業務 API 透過 `Authorization: Bearer {token}` 呼叫。

Refresh Token 原文只存在 `PMW-REFRESH` HttpOnly Cookie；資料庫只保存 SHA-256 hash。Refresh 每次輪替，已使用或撤銷的 token 再次出現時，整個 token family 都會撤銷。

## Schema 與 migration

正式 Schema 來源位於 `src/ProjectManagementWeb.Infrastructure/Persistence/Migrations/`。新增 migration：

```bash
dotnet ef migrations add MigrationName --project src/ProjectManagementWeb.Infrastructure --startup-project src/ProjectManagementWeb.Api --output-dir Persistence/Migrations
```

禁止使用 `EnsureCreated`。Project、Task、可修改留言使用 SQL Server `rowversion`，API 以 Base64 傳遞；版本衝突回傳 HTTP 409。

目前 migration 共有 20 張資料表。值得注意的是，Identity 執行期要求 Email 唯一，但現行 migration 尚未對 `Accounts.NormalizedEmail` 建立 UNIQUE constraint；若要從資料庫層完整保證，需另建 migration。詳細限制請參閱 [TableSchema.md](docs/TableSchema.md)。

## 驗證

```bash
dotnet format --verify-no-changes
dotnet build
dotnet test
dotnet list package --vulnerable --include-transitive
docker compose config
```

IntegrationTests 的輕量 API surface 測試使用 `WebApplicationFactory`。完整資料庫流程由 Compose 實際啟動 SQL Server、套用 migration 後進行 smoke test；不可改用 EF InMemory 來宣稱 SQL Server relational behavior 已驗證。

## 本階段不包含

Vue 前端、Hangfire 到期提醒、RoleFunction 動態管理、雲端部署與密碼重設不在本 MVP 範圍。
