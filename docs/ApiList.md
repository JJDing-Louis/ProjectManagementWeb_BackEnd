# API 清單

本文件依 `src/ProjectManagementWeb.Api/Controllers`、Application contracts 與 Infrastructure services 整理，反映目前程式碼實作。API 基底路徑為 `/api/v1`，JSON enum 以字串傳輸。

## 共通規則

| 項目 | 實作契約 |
|---|---|
| 身分驗證 | 業務 API 使用 `Authorization: Bearer {accessToken}` |
| Access Token | RSA JWT，預設 15 分鐘；包含 `sub`、`jti`、`role`、多筆 `permission`、`token-version` |
| Refresh Token | `PMW-REFRESH` HttpOnly Cookie，Path=`/api/v1/auth`，預設 7 天，每次 refresh 輪替 |
| CSRF | Auth cookie-sensitive POST 必須帶 `X-CSRF-TOKEN`；token 由 `/security/csrf-token` 取得 |
| 分頁 | 預設 `page=1&pageSize=20`，Service 會將 `pageSize` 限制在 1–100 |
| 日期時間 | ISO 8601 `DateTimeOffset`，資料庫使用 `datetimeoffset` |
| 並行控制 | `Project`、`TaskItem`、`TaskItemComment` 的 `rowVersion` 使用 Base64 字串；衝突回傳 409 |
| 業務編號 | Project／Task 由後端依 UTC 日期自動產生 `PRJ-YYYYMMDD######`／`TASK-YYYYMMDD######`；兩類型每日獨立歸零 |
| 錯誤格式 | RFC 7807 Problem Details，另含 `code` 與 `traceId` extension |

## Security 與 Auth

| Method | Route | Auth | CSRF | Request | Success | 說明 |
|---|---|---:|---:|---|---|---|
| GET | `/api/v1/security/csrf-token` | 否 | 否 | 無 | 200 `{ token }` | 建立 antiforgery cookie 並回傳 request token |
| POST | `/api/v1/auth/register` | 否 | 是 | `RegisterRequest` | 201 `{ accountId }` | 建立帳號、Viewer 角色與偏好；寄信失敗不回滾帳號 |
| POST | `/api/v1/auth/login` | 否 | 是 | `LoginRequest` | 200 access token；設定 refresh cookie | Email 未驗證仍可登入，但有效角色固定為 Viewer |
| POST | `/api/v1/auth/refresh` | 否 | 是 | Refresh Cookie | 200 access token；輪替 refresh cookie | 舊 token 重用時撤銷同一 family |
| POST | `/api/v1/auth/logout` | 否 | 是 | Refresh Cookie，可省略 | 204 | 撤銷目前 refresh token 並刪除 cookie |
| POST | `/api/v1/auth/email/confirm` | 否 | 是 | `ConfirmEmailRequest` | 200 `true` | 驗證 Identity Email token |
| POST | `/api/v1/auth/email/resend` | 否 | 是 | `ResendEmailRequest` | 200 `true` | 不洩漏帳號是否存在；符合條件才重新寄送 |
| GET | `/api/v1/auth/me` | 是 | 否 | 無 | 200 `CurrentAccountResponse` | 回傳目前帳號、唯一系統角色及 Functions |

## Users、Roles 與 Preferences

| Method | Route | Function／範圍 | Request | Success | 說明 |
|---|---|---|---|---|---|
| GET | `/api/v1/users` | `accounts.read` | `UserQuery` | 200 `PagedResult<UserResponse>` | 搜尋帳號、Email、姓名；可依角色篩選 |
| GET | `/api/v1/users/{id}` | `accounts.read` 或本人 | 無 | 200 `UserResponse` | 其他帳號無權限時回傳 403 |
| PUT | `/api/v1/users/{id}/role` | `accounts.manage-role` | `UpdateRoleRequest` | 200 `UserResponse` | Serializable transaction 內取代角色、撤銷 tokens；保護最後一位 Admin |
| PATCH | `/api/v1/users/{id}/status` | `accounts.manage-status` | `UpdateUserStatusRequest` | 200 `UserResponse` | 啟停帳號、遞增 token version、撤銷 tokens |
| PUT | `/api/v1/users/{id}/administration` | `accounts.manage-role` 與 `accounts.manage-status` | `UpdateAdministrationRequest` | 200 `UserResponse` | Serializable transaction 原子更新角色與狀態；token version 只遞增一次並撤銷 tokens |
| GET | `/api/v1/users/me/preferences` | `preferences.read-own` | 無 | 200 `PreferenceResponse` | 讀取自己的批次確認偏好 |
| PUT | `/api/v1/users/me/preferences` | `preferences.update-own` | `UpdatePreferenceRequest` | 200 `PreferenceResponse` | 更新自己的批次確認偏好 |
| GET | `/api/v1/roles` | 任一已登入帳號 | 無 | 200 `RoleResponse[]` | 回傳固定系統角色及各自 Functions |

## Projects 與 Members

| Method | Route | Function／資源範圍 | Request | Success | 說明 |
|---|---|---|---|---|---|
| GET | `/api/v1/projects` | `projects.read`；非全域管理者只看所屬專案 | `ProjectQuery` | 200 `PagedResult<ProjectResponse>` | 可依關鍵字、狀態、頁碼篩選 |
| POST | `/api/v1/projects` | `projects.create` | `CreateProjectRequest` | 201 `ProjectResponse` | Code 由後端產生；Owner 必須是有效帳號並自動取得 ProjectManager |
| GET | `/api/v1/projects/{id}` | 全域管理或專案成員 | 無 | 200 `ProjectResponse` | 軟刪除專案由 EF query filter 排除 |
| PUT | `/api/v1/projects/{id}` | 非 Viewer，且為全域管理或該專案 ProjectManager | `UpdateProjectRequest` | 200 `ProjectResponse` | Owner 必須是成員；驗證 `rowVersion` |
| GET | `/api/v1/projects/roles` | 任一已登入帳號 | 無 | 200 `ProjectRoleResponse[]` | 回傳固定專案角色 |
| GET | `/api/v1/projects/{id}/members` | 全域管理或專案成員 | 無 | 200 `ProjectMemberResponse[]` | 每位成員包含多筆專案角色 |
| GET | `/api/v1/projects/{id}/member-candidates` | 非 Viewer，且為全域管理或 ProjectManager | `search`、`page`、`pageSize` | 200 `PagedResult<ProjectMemberCandidateResponse>` | 僅回傳帳號 ID、帳號及姓名，並排除既有成員 |
| POST | `/api/v1/projects/{id}/members` | 非 Viewer，且為全域管理或 ProjectManager | `SaveProjectMemberRequest` | 200 `ProjectMemberResponse` | `projectRoleIds` 至少一筆且必須全部有效 |
| PUT | `/api/v1/projects/{id}/members/{accountId}` | 非 Viewer，且為全域管理或 ProjectManager | `UpdateProjectMemberRequest` | 200 `ProjectMemberResponse` | 以陣列取代該成員全部專案角色 |
| DELETE | `/api/v1/projects/{id}/members/{accountId}` | 非 Viewer，且為全域管理或 ProjectManager | 無 | 204 | Owner 必須先移交；有未完成 Task 必須先重新指派 |

## Task Items

| Method | Route | Function／資源範圍 | Request | Success | 說明 |
|---|---|---|---|---|---|
| GET | `/api/v1/projects/{projectId}/task-items` | `tasks.read` 且可讀專案 | `TaskQuery` | 200 `PagedResult<TaskResponse>` | 支援搜尋、狀態、指派者、只看自己、排序、分頁 |
| POST | `/api/v1/projects/{projectId}/task-items` | `tasks.create` 且可讀專案 | `CreateTaskRequest` | 201 `TaskResponse` | Code 由後端產生；指派對象必須是專案成員 |
| GET | `/api/v1/projects/{projectId}/task-items/{taskId}` | `tasks.read` 且可讀專案 | 無 | 200 `TaskResponse` | Task 必須屬於 route 指定專案 |
| PUT | `/api/v1/projects/{projectId}/task-items/{taskId}` | `tasks.update-any` 且可讀專案 | `UpdateTaskRequest` | 200 `TaskResponse` | 完整修改；驗證指派者、期限與 `rowVersion` |
| PATCH | `/api/v1/projects/{projectId}/task-items/{taskId}/status-and-deadline` | `tasks.update-assigned`、本人被指派且可讀專案 | `UpdateAssignedTaskRequest` | 200 `TaskResponse` | 一般使用者只修改自己的狀態與期限 |
| PATCH | `/api/v1/projects/{projectId}/task-items/batch-status` | `tasks.update-any`，或每筆皆為本人且具 `tasks.update-assigned` | `BatchUpdateTaskStatusRequest` | 200 `BatchUpdateResponse` | 先驗證全部資料，再於單一 transaction 全部更新 |
| DELETE | `/api/v1/projects/{projectId}/task-items/{taskId}?rowVersion=...` | `tasks.delete` 且可讀專案 | query `rowVersion` | 204 | 軟刪除，保留留言、歷史與稽核資料 |

Task 列表允許的 `sortBy` 為 `createdAt`（預設）、`deadline`、`status`、`code`；`sortDirection=asc` 才使用升冪，其他值以降冪處理。

## Comments

| Method | Route | Function／資源範圍 | Request | Success | 說明 |
|---|---|---|---|---|---|
| GET | `/api/v1/projects/{projectId}/task-items/{taskId}/comments` | `comments.read` 且可讀 Task | 無 | 200 `CommentResponse[]` | 依建立時間升冪 |
| POST | `/api/v1/projects/{projectId}/task-items/{taskId}/comments` | `comments.create` 且可讀 Task | `CreateCommentRequest` | 200 `CommentResponse` | 留言 trim 後需為 1–2000 字 |
| PUT | `/api/v1/projects/{projectId}/task-items/{taskId}/comments/{commentId}` | `comments.update-own`、留言作者且可讀 Task | `UpdateCommentRequest` | 200 `CommentResponse` | 驗證 `rowVersion` |
| DELETE | `/api/v1/projects/{projectId}/task-items/{taskId}/comments/{commentId}?rowVersion=...` | `comments.delete-own`、留言作者且可讀 Task | query `rowVersion` | 204 | 軟刪除留言；驗證 `rowVersion` |

## 平台端點

| Method | Route | 環境 | 說明 |
|---|---|---|---|
| GET | `/health` | 全部 | 應用程式健康狀態 |
| GET | `/openapi/v1.json` | Development、Testing，或 `OpenApi:Enabled=true` | ASP.NET Core OpenAPI 文件 |
| GET | `/swagger/v1/swagger.json` | 同上 | Swashbuckle OpenAPI 文件 |
| GET | `/swagger` | 同上 | Swagger UI |

## 常見狀態碼

| Status | 用途 |
|---:|---|
| 200 | 查詢或更新成功 |
| 201 | 註冊、建立專案、建立 Task 成功 |
| 204 | logout 或刪除成功 |
| 400 | Model binding、CSRF、格式或一般驗證失敗 |
| 401 | 缺少／無效 JWT、登入失敗、refresh token 失效 |
| 403 | 已登入但 Function 或資源範圍不足 |
| 404 | 帳號、專案、成員、Task 或留言不存在 |
| 409 | UNIQUE、最後一位 Admin、Owner／Task 阻擋、rowversion 衝突，或每日編號超過上限（`daily_code_limit_exceeded`） |
| 422 | Email 未驗證角色提升、Owner／指派者／專案角色／期限等業務驗證失敗 |
| 500 | 未預期例外，或 Identity 角色異動失敗 |

完整 request／response 欄位與前端實作規則請參閱 [FrontendContract.md](FrontendContract.md)。
