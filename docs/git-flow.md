# GitFlow 開發與發版規範（Backend）

本文件適用於 `ProjectManagementWeb_BackEnd`。本專案是 .NET 10／ASP.NET Core API，使用 SQL Server、EF Core migrations 與 Docker；前端及規格是另外兩個 Git repository，各自建立分支、PR 與 tag。業務規則與測試要求仍以 `AGENTS.md` 及相關規格為準。

## Branch Strategy

| 分支        | 用途                       | 建立來源                            | 合併目標                   |
| ----------- | -------------------------- | ----------------------------------- | -------------------------- |
| `main`      | 已核准的 Production 程式碼 | Release／Hotfix PR                  | 不在此直接開發             |
| `develop`   | 下一版本整合               | Feature／Bugfix／Release／Hotfix PR | Release 的建立來源         |
| `feature/*` | 新功能或一般重構           | `develop`                           | `develop`                  |
| `bugfix/*`  | 尚未發版版本的一般缺陷     | `develop`                           | `develop`                  |
| `release/*` | Release Candidate          | `develop`                           | `main`，再回合併 `develop` |
| `hotfix/*`  | Production 緊急修正        | `main`                              | `main`，再回合併 `develop` |

`main`、`develop` 只接受 PR，不直接 push 或 force push。`feature`／`bugfix` 合併至 `develop`，`release`／`hotfix` 合併至 `main`，都必須使用 PR；`release`／`hotfix` 回合併 `develop` 也使用 PR。PR 合併後才更新目標分支，不改寫既有 history，也不以刪除舊分支作為導入條件。

導入狀態（2026-09-27）：Backend 的 `develop` 已存在，但 `main` 含有尚未回到 `develop` 的後續提交。下一版 Release 前，維護者應透過經審查的同步 PR，把需要保留的 Production 變更帶回 `develop`；不可對 `develop` 直接 push，AI Agent 亦不可自行合併 `main`。建立依賴 Production 修正的功能分支前，也應先完成同步。請每次以實際 Git 狀態重新確認。

## Branch Naming

| 類型              | 格式                                | 範例                        |
| ----------------- | ----------------------------------- | --------------------------- |
| 新功能／一般重構  | `feature/<ticket-id>-<description>` | `feature/123-user-login`    |
| 一般 Bug          | `bugfix/<ticket-id>-<description>`  | `bugfix/456-project-filter` |
| Release           | `release/<version>`                 | `release/1.2.0`             |
| Production Hotfix | `hotfix/<version>-<description>`    | `hotfix/1.2.1-login-error`  |

`ticket-id` 使用議題編號；尚無議題系統時，使用團隊可追溯的工作代碼（例如日期 `20260927`），並在 PR 連結需求。`description` 使用小寫英文字母、數字與連字號。一般重構歸在 `feature/*`；若是修復可重現的缺陷，使用 `bugfix/*`。變更正式環境既有行為且需立即發版時，才使用 `hotfix/*`。

## Commit Convention

採用 Conventional Commits：`<type>(<scope>): <description>`。`type` 限 `feat`、`fix`、`refactor`、`docs`、`test`、`chore`、`build`、`ci`、`perf`、`style`、`revert`；`scope` 使用穩定模組名，例如 `auth`、`api`、`projects`、`tasks`、`db`、`docs`。描述與 commit body 使用繁體中文，動詞指出實際變更。

```text
feat(auth): 新增 JWT 登入功能
fix(tasks): 修正批次更新時的版本衝突處理
docs(git): 補充發版與回復流程
```

Breaking Change 在 commit body 註明 `BREAKING CHANGE: <繁體中文說明>`，並於 PR 說明 API／資料遷移。舊提交維持原狀；規範從新提交開始適用。預設 Squash Merge 時，PR 的最終 squash commit 也必須符合此格式。

## Pull Request

開 PR 時使用 [PR 範本](../.github/PULL_REQUEST_TEMPLATE.md)，填寫 Summary、Changes、Test、Risk、Rollback；測試分別列出 Unit、Integration、Manual 的實際結果或未執行原因，並說明 API、資料庫、前端及部署影響。審查者確認規格、相容性、敏感資訊、測試證據與回復方案後再合併。

- `feature/*`／`bugfix/*` → `develop`：預設 Squash Merge，產生一個 Conventional Commit。
- `release/*`／`hotfix/*` → `main`：使用 Merge Commit 保留完整提交與分支脈絡；回合併 `develop` 也保留完整提交。若 GitHub 設定無法選擇 Merge Commit，先調整 repository 設定，再合併。
- 其他來源或目標的 PR 需在說明中交代用途；跨 repository 的相依變更各自送 PR，連結彼此並確認相容版本。

## Release Flow

1. 確認 `develop` 已包含預計發版內容及必要的 Production 同步變更，從 `develop` 建立 `release/x.y.z`。
2. Release 分支只接受 Bug Fix、Version Update、Release Note、Config 調整；新功能回到下一版 `develop`。執行 Build、Unit、Integration、Manual QA，並核對 SQL migration 與部署順序。
3. 以 `release/x.y.z` → `main` PR 審查並使用 Merge Commit。合併後確認 `main` 的目標 commit。
4. 在該 `main` release commit 建立 annotated tag `vX.Y.Z`，確認 tag 指向該 commit；發布 GitHub Release 與 Production 部署由已核准的發版流程執行。禁止對未合併 commit 打正式 tag。
5. 使用 `release/x.y.z` → `develop` PR 回合併 release 修正；若分支後有衝突，在 PR 中處理並重新測試。回合併完成才結束發版。

```text
develop → release/x.y.z → QA／Test → main PR → vX.Y.Z tag
        → Production Deployment → develop 回合併 PR
```

## Hotfix Flow

1. 從 `main` 建立 `hotfix/x.y.z-description`，只包含解決 Production 事件所需的修正與測試。
2. 執行受影響範圍的 Build、Unit、Integration、Manual Test，寫明事件影響與回復方法。
3. 以 Hotfix PR 合併到 `main`，保留完整提交；在合併 commit 建立對應 `vX.Y.Z` tag，完成核准後的 Production 部署。
4. 以 Hotfix PR 回合併 `develop` 並處理衝突，確保修正進入下一版。

```text
main → hotfix/x.y.z-description → Test → main PR → vX.Y.Z tag
     → Production Deployment → develop 回合併 PR
```

## Versioning

採用 Semantic Versioning `MAJOR.MINOR.PATCH`：不相容的 API／資料契約變更增加 MAJOR；向後相容的新功能增加 MINOR；向後相容的修正增加 PATCH。版本依 Backend repository 的可部署 API 獨立計算；Frontend、Spec 不因 Backend 發版而自動加版。跨庫同時發版時，PR 與 Release Note 列出各庫 tag 與相容組合。

現有提交尚無正式 `v*` tag；第一次採用的版本需由 Release PR 根據相容性及既有對外版本確認，不根據資料夾名稱或歷史提交自行宣稱為 `1.0.0`。

## Tagging

正式 tag 格式為 `v<version>`，例如 `v1.2.0`。只在 Release／Hotfix 合併到 `main` 後建立，必須指向 `main` 上該次發版的 commit，且同一版本在同一 repository 只使用一次。tag 已公開後不得移動或覆寫；部署與回溯記錄保存 repository、tag、commit SHA、映像 digest、migration 與時間。若發版失敗，以新的修正版本或經核准的既有穩定版本回復，不重用舊 tag。

## CI/CD Mapping

程式碼託管於 GitHub，因此目標 CI 平台為 GitHub Actions。目前 repository **沒有 GitHub Actions workflow，也沒有已設定的分支保護或自動部署**；下表是待實作的觸發契約，不代表現有檢查已在執行。

| 觸發條件                   | 目標檢查／部署                                                                                         |
| -------------------------- | ------------------------------------------------------------------------------------------------------ |
| `feature/*`／`bugfix/*` PR | `dotnet format --verify-no-changes`、`dotnet build`、Unit Test；有 SQL 測試環境時執行 Integration Test |
| `develop` PR／合併         | Build、Unit、Integration Test；核准後部署 DEV                                                          |
| `release/*` PR／更新       | Build、Unit、Integration、必要的手動 QA；核准後部署 STAGING                                            |
| `main` PR／合併            | Build、Unit、Integration；合併後保留可追溯部署產物，等待正式 tag 與 Production 核准                    |
| `v*` tag                   | 驗證 tag 指向 `main` release commit，發布 GitHub Release，依核准流程將同一產物部署 PRODUCTION          |

Integration Test 使用隔離 SQL Server，不連 Production DB。資料庫 migration 與 API 部署分開執行，先驗證可回復性與相容性；`/health` 只證明 API 存活，不能單獨證明 `pmw_app` 可執行業務查詢。部署目的地、GitHub Environment、Secret、權限與人工核准尚待建立，文件不授權自動部署。

GitHub repository 管理者需另行啟用 `main`／`develop` 的 PR 必要條件、審查與必要狀態檢查，限制直接 push 與 force push；先建立實際 workflow 再把檢查設為 Required。文件本身不會替代 GitHub 的保護設定。

## AI Agent 作業程序

1. 修改前檢查 repository、`git status --short --branch`、`main`／`develop` 位置與相關規格；工作樹有既存變更時先辨識其歸屬。
2. 判斷為 `feature`（包含一般重構）、`bugfix` 或 `hotfix`，從規定基底建立符合命名的工作分支；若 `develop` 尚未同步必要 Production 變更，先提出同步 PR，不直接修改保護分支。
3. 修改後執行相關 Build、Test、`git diff --check`，檢查 `git diff`、敏感資訊與檔案範圍；未執行的驗證明確標示。
4. 產生繁體中文 Conventional Commit，提供含測試結果、影響與回復方式的 PR 說明。所有合併與發版交由核准流程處理。

AI Agent 未經使用者明確要求，不執行 force push、history rewrite、刪除遠端分支、合併 `main` 或 Production 發版。
