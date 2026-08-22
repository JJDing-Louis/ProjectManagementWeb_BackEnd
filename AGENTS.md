# ProjectManagementWeb 後端開發規範

## 1. 適用範圍

本文件適用於 `ProjectManagementWeb_BackEnd` 目錄及其所有子目錄。

本目錄目前尚未初始化後端專案。後續初始化、開發、程式碼審查與測試皆以 **ASP.NET Core Web API** 為技術方向。除非需求另有明確決策，不得改用其他 Web framework，也不得把範例程式、空白 scaffold、尚未建置的程式碼視為完成實作。

## 2. 需求與文件優先順序

實作前必須先閱讀與功能相關的規格。需求判斷依下列順序進行：

1. 使用者在目前任務中明確提出的需求。
2. `../ProjectManagementWeb_Spec/UserStory.md` 的角色、權限與驗收條件。
3. `../ProjectManagementWeb_Spec/Flowchart/` 的業務流程與交易邊界。
4. `../ProjectManagementWeb_Spec/C4/` 的元件責任與相依方向。
5. `../ProjectManagementWeb_Spec/StaticData.md` 的既有代碼與名稱。
6. `../ProjectManagementWeb_Spec/Schema.md` 的資料概念與設計草稿。
7. `../ProjectManagementWeb_Spec/UIMock/` 的畫面操作概念。

文件互相衝突、驗收條件不足或 API 契約未定義時，不得自行發明業務規則。應先列出衝突、合理假設、可行方案與影響範圍，取得確認後才實作。

目前已知 `UserStory.md` 與 `Flowchart/註冊、Email 驗證與登入.md` 對 Email 驗證後的角色有不同描述。此規則在確認前不得寫死於註冊、驗證或授權流程。

`Schema.md` 是設計草稿，不是已核准的實體資料庫契約。建立 migration 或資料表前，必須先依本文件的資料庫規範完成審查並取得確認。

## 3. 技術基線

- ASP.NET Core Web API，採用 C#。
- API presentation 預設採 Controller，不將 Minimal API 與 Controller 混用。
- 使用 SDK-style project，啟用 nullable reference types 與 implicit usings。
- `.csproj` 必須明確指定 `TargetFramework`。初始化時應選擇仍受官方支援的 .NET 版本並記錄決策；不得在未確認相容性、套件與部署環境前自行升級 major version。
- API 文件使用 ASP.NET Core OpenAPI 能力；若需加入額外套件，先說明必要性與維護風險。
- 資料庫方向依 C4 規格為 SQL Server；ORM、micro-ORM 或原生 ADO.NET 尚未確認，不得先假定使用 Entity Framework Core、Dapper 或其他資料存取方案。
- 測試以 xUnit 為預設方向；mock、assertion、container 等額外套件需依實際測試需求選用。

新增 NuGet package 前，必須說明用途、維護狀態、安全性、授權、替代方案與對架構的影響。避免加入功能重疊的套件，也不得只為少量簡單邏輯引入大型 framework。

## 4. 建議 Solution 與目錄結構

初始化後，原則上採用下列責任邊界。若 MVP 規模不需要拆成多個 project，可以先維持較少 project，但相依方向與職責不得混雜。

```text
ProjectManagementWeb_BackEnd/
├── src/
│   ├── ProjectManagementWeb.Api/              # HTTP、Controller、Middleware、DI
│   ├── ProjectManagementWeb.Application/      # Use case、DTO、介面與交易協調
│   ├── ProjectManagementWeb.Domain/           # Entity、Value Object、核心業務規則
│   └── ProjectManagementWeb.Infrastructure/   # DB、Email、外部服務與介面實作
├── tests/
│   ├── ProjectManagementWeb.UnitTests/
│   └── ProjectManagementWeb.IntegrationTests/
├── ProjectManagementWeb.sln
└── AGENTS.md
```

相依方向應為：

```text
Api -> Application -> Domain
Infrastructure -> Application / Domain
Api -> Infrastructure（僅限啟動時註冊實作）
```

- Domain 不得依賴 ASP.NET Core、資料庫、SMTP 或其他 Infrastructure 細節。
- Application 定義 use case 與所需介面，不直接處理 HTTP request／response。
- Api 負責 HTTP 邊界、輸入轉換、身分資訊與結果映射，不承擔業務規則。
- Infrastructure 實作資料存取、交易、Email 與外部服務 adapter。
- 不得建立循環相依。
- 類別、介面、record、enum 與抽象類別原則上各自放在獨立檔案；只在高度內聚且極小的 private nested type 有明確理由時例外。

## 5. ASP.NET Core 與 Controller 規範

- Controller 保持精簡：接收輸入、呼叫 Application use case、映射 HTTP 結果。
- Controller 不直接操作 `DbConnection`、`DbContext`、SMTP client 或 repository implementation。
- Controller action 使用 async API，並接受、傳遞 `CancellationToken`。
- 不使用 `.Result`、`.Wait()` 或其他 sync-over-async 寫法。
- DI 使用 constructor injection；禁止 Service Locator 與任意呼叫 `IServiceProvider.GetService` 取得相依物件。
- Service lifetime 必須符合依賴物件生命週期，不得讓 singleton 捕捉 scoped service。
- Middleware 只處理跨領域的 request pipeline concern，例如例外映射、correlation ID 或安全 header，不在 middleware 實作特定業務流程。
- Filter、middleware、model binder 與 action behavior 的責任不得重複。

Controller 與 route 命名以業務資源為主，例如 Accounts、Users、Projects、ProjectMembers、TaskItems 與 Comments。除非規格已確認，不得用 RPC-style route 暫代正式 REST API 契約。

## 6. Application 與 Domain 設計

- 一個 use case 聚焦一個明確的應用操作，負責協調驗證、授權、Domain 規則、repository 與 transaction。
- Domain entity 保護自身不變條件，不公開可讓任意呼叫者破壞狀態的 setter。
- 業務規則應放在適當的 Domain 或 Application 層，不得散落在 Controller、SQL 與 UI 專用 DTO 中。
- 優先使用組合而非繼承；只有存在真實且穩定的 is-a 關係時才使用繼承。
- 介面應小而明確，不建立包含所有資料操作的通用大型 repository 或 service。
- 不建立僅轉呼叫下一層、沒有邊界價值的形式化包裝層。
- 時間、亂數、目前使用者與外部服務等不穩定依賴需透過可替換邊界隔離，以利測試。
- 系統時間儲存與比較策略必須統一。尚未確認前，不得混用 UTC、伺服器本地時間與未帶 offset 的時間值。

## 7. API 與資料契約

- API request／response DTO、Application command／query 與 persistence model 必須分離，不直接把資料表 entity 暴露為 API contract。
- DTO 欄位名稱、nullability、長度、格式與 enum／代碼值必須有清楚契約。
- 輸入驗證在 HTTP 邊界提供快速回饋，Domain／Application 仍須保護真正的業務不變條件。
- 錯誤回應使用一致的 RFC 7807 Problem Details 格式，不回傳 exception stack trace、SQL、token 或內部實作細節。
- HTTP status code 應符合語意，至少區分輸入錯誤、未驗證、權限不足、資源不存在、版本衝突與伺服器錯誤。
- 列表 API 的分頁、搜尋、排序與篩選需有明確上限與允許欄位，禁止將未驗證的排序欄位直接拼接至 SQL。
- 所有 SQL 必須參數化；禁止字串拼接使用者輸入。
- 日期時間傳輸採 ISO 8601，並依已確認的時區策略處理。
- OpenAPI 文件需反映實際 status code、驗證規則、授權需求與 DTO，不得與實作分離維護成兩套互相漂移的契約。
- 對可能重送的建立、批次更新或外部服務操作，應評估 idempotency，不得假定 request 只會到達一次。

公開 API 若需破壞性變更，必須先列出前端與其他 consumer 的影響，確認 versioning 或遷移策略後才修改。

## 8. 身分驗證與授權

系統角色名稱以規格為準：`Admin`、`Administrator`、`User`、`Viewer`。不得自行改名、合併或新增角色。

- 驗證回答「使用者是誰」，授權回答「此使用者能否對此資源執行此操作」，兩者不得混為一談。
- 每個讀取與修改 use case 都必須由後端檢查系統角色、專案成員關係、資源範圍與操作能力；不得依賴前端隱藏按鈕。
- 授權規則優先集中為 policy、authorization service 或 use-case guard，禁止在各 Controller 複製角色字串判斷。
- `Viewer` 只能讀取有權限存取的資料，不得新增、修改、刪除、留言、切換 Task 狀態或調整角色。
- 密碼只允許使用 ASP.NET Core Identity 或經審查的密碼雜湊機制保存，禁止明文、可逆加密或自行設計密碼雜湊演算法。
- Token、cookie、session 與 refresh／revocation 策略尚未確認前，不得自行假定 JWT 或將長效憑證寫入不安全儲存位置。
- Email 驗證 token 必須具備安全隨機性、用途限制、有效期限與重送／失效規則，不得在 log 中記錄 token 原文。
- 若採 cookie authentication，必須評估 CSRF；若採 bearer token，必須評估 token 洩漏、過期、撤銷與 replay 風險。
- 管理員異動角色、停用帳號及其他敏感操作必須留下可追溯稽核紀錄。

## 9. 資料庫設計與資料存取

建立或修改 Schema 前，先識別 Entity、Attribute、Relationship、資料生命週期、主要查詢情境與交易邊界。設計優先順序為：

1. 資料正確性。
2. 業務需求。
3. 至少符合 1NF，並以 2NF 為主要目標。
4. Schema 可理解性與可維護性。
5. 查詢與程式開發複雜度。
6. 查詢效能。

既有或新 Schema 必須檢查：

- Primary Key、Foreign Key、UNIQUE constraint 與 nullability 是否合理。
- 是否存在 partial dependency、多值欄位或不合理的重複資料。
- 欄位型別、長度、精度、時區與命名是否符合資料語意。
- Index 是否對應實際查詢與排序情境，且未造成不必要的寫入成本。
- 軟刪除資料是否會被一般查詢正確排除，以及唯一性規則如何處理已刪除資料。
- 是否需要 optimistic concurrency token，版本衝突如何映射為 API 回應。
- 是否存在 race condition、遺失更新或先查後寫的競態問題。
- Transaction boundary 是否完整且範圍適中。
- Cascade Delete 是否可能誤刪留言、歷史、稽核或其他需保留資料。

審查後先列出建議修改與資料遷移影響，待確認後才建立 migration 或修改 Schema。不得為追求 3NF 而做無實務價值的過度拆表，但資料重複若會造成一致性問題，必須主動指出。

Repository 應描述業務需要的資料操作，不把 ORM 或 SQL 細節洩漏到 Application／Domain。查詢必須避免 N+1、無上限讀取及不必要的 `SELECT *`；需要大量資料時使用分頁或適合的批次策略。

## 10. 交易、並行與稽核

- 批次修改 Task 狀態必須先驗證所有項目的權限、指派關係、狀態轉換及版本，再於單一交易中全部更新；任何一筆失敗即全部 rollback。
- 「檢查最後一位 Admin 後再異動角色」等跨資料規則必須在同一交易與適當 isolation／locking 策略中處理，避免並行請求同時通過檢查。
- 修改 Project、Task、角色與其他可能被多人同時編輯的資料時，需評估 optimistic concurrency，衝突不得靜默覆蓋。
- 軟刪除 Task 時保留相關留言與稽核紀錄，不得使用未經評估的 cascade delete。
- 稽核資料至少能辨識操作者、操作類型、目標、時間與必要的前後狀態；不得只相信 request 傳入的操作者 ID。
- Transaction 由 use case 邊界協調，不得讓 Controller 分段提交同一個業務操作。
- 不在資料庫 transaction 內執行耗時 SMTP 或其他不可回復的網路呼叫。需要資料與外部副作用一致性時，應評估 outbox／queue 等方案。

## 11. Email 與背景工作

- Email 透過 Application 定義的介面與 Infrastructure adapter 隔離，不得讓 Domain 或 Controller 直接依賴 Gmail SMTP。
- SMTP host、port、帳密、寄件人與連線安全設定全部來自 configuration／secret provider，不得硬編碼。
- 寄信必須設定 timeout，並依失敗類型設計有限次 retry；不得無限重試。
- 到期提醒的掃描與單封寄送依流程規格分開處理，並記錄可追蹤的寄送狀態。
- 背景工作不得假設只會執行一次。重啟、平行 worker 與重試情境下仍須避免或降低重複寄送。
- log 不得包含完整驗證連結、token、密碼或不必要的 Email 個資。

## 12. Configuration、Secrets 與環境

- 使用 ASP.NET Core configuration provider 與 strongly typed options 管理設定；啟動時驗證必要設定。
- `appsettings.json` 僅放可公開的預設值。連線字串、SMTP 密碼、token signing key 與其他秘密不得提交至 Git。
- 本機秘密使用 User Secrets 或專案已確認的安全方案；正式環境使用部署平台的 secret manager／環境設定。
- Development、Test、Staging、Production 的差異由設定提供，不以 `if` 加硬編碼機器名稱。
- CORS 採最小允許來源、method 與 header；正式環境不得使用任意 origin 搭配 credentials。
- 啟動時不得自動執行具破壞性的正式資料庫重建或 seed。

## 13. 例外處理、Logging 與穩定性

- 不吞掉例外。已知業務錯誤映射為穩定的錯誤代碼與 Problem Details；未知例外由全域 handler 記錄並回傳通用錯誤。
- Log 使用 structured logging 與具語意的欄位，不以字串串接重要資料。
- 每個 request 應可透過 trace／correlation ID 追蹤，但不得把敏感資料當識別碼。
- 禁止記錄密碼、access token、refresh token、Email verification token、完整 connection string 或其他秘密。
- 外部 I/O 需傳遞 cancellation、設定合理 timeout，並評估有限 retry；非冪等操作不得盲目重試。
- Health check 應區分應用程式存活與必要相依服務可用性，避免因非核心服務短暫失敗造成錯誤重啟風暴。
- API 應考慮 request body、分頁大小、留言長度與批次數量上限，避免資源耗盡。

## 14. C# 程式碼規範

- 命名遵循 .NET conventions：type、method、property 使用 PascalCase；local variable、parameter 使用 camelCase；interface 使用 `I` 前綴。
- 命名需描述業務意圖，避免 `Manager`、`Helper`、`Common`、`Data` 等無法表達責任的模糊名稱。
- 方法保持短小並維持單一責任；過深巢狀優先使用 guard clause 或拆分具語意的方法。
- 不以 null-forgiving operator (`!`) 或不必要的型別轉換掩蓋 nullability 問題。
- 只讀資料優先使用不可變型別或 read-only collection，避免跨層共享可變狀態。
- 非同步方法使用 `Async` 後綴；不為了符合命名而建立沒有非同步工作的假 async 方法。
- `CancellationToken` 應傳到資料庫、HTTP、SMTP 與其他可取消 I/O 的最底層。
- 公開 API 與複雜業務邏輯需要清楚文件；註解說明原因、限制與取捨，不重述程式碼表面行為。
- 遵循 repository 內的 `.editorconfig` 與 formatter；若尚未建立，初始化時一併建立最小且一致的格式規則。
- 禁止保留未使用程式碼、被註解掉的大段舊實作或沒有追蹤依據的 TODO。

## 15. 測試規範

測試重點依風險分層：

- Domain：不變條件、狀態轉換、邊界值與無效操作的單元測試。
- Application：授權、驗證、交易協調、版本衝突與失敗路徑測試。
- Infrastructure：SQL mapping、constraint、transaction、concurrency、repository 與 Email adapter 的整合測試。
- Api：routing、model binding、authentication、authorization、Problem Details 與 status code 測試。
- 關鍵流程：註冊與驗證、登入、專案資料範圍、批次狀態更新、角色異動、留言及軟刪除的端對端或適當整合測試。

不得只測 happy path。至少涵蓋：

- null、空字串、長度上限、無效代碼與不存在的關聯資料。
- 未登入、`Viewer`、權限不足與跨專案存取。
- 重複帳號／Email／成員與唯一性競態。
- 批次資料中任一筆驗證失敗時全部不更新。
- 同一資源被兩個 request 同時修改的版本衝突。
- DB、SMTP、timeout、cancellation 與其他外部服務失敗。

修正 bug 時，優先先補上能重現問題的測試，再修正實作。測試不得依賴執行順序、共用可變資料或正式外部服務。

## 16. 開發與驗證流程

開始修改前：

1. 閱讀本文件及相關 User Story、Flowchart、C4、StaticData 與 Schema。
2. 盤點既有 solution、project、公開 API、測試、套件與相依方向。
3. 說明需求理解、必要假設、影響範圍與最小修改策略。
4. 若涉及 Schema，先提出審查結果與建議，取得確認後才修改。

完成修改後，依 solution 實際設定執行驗證。專案初始化後，原則上至少執行：

```bash
dotnet format --verify-no-changes
dotnet build
dotnet test
```

若專案啟用套件弱點檢查，亦應執行：

```bash
dotnet list package --vulnerable --include-transitive
```

涉及資料庫時，還需以隔離的測試資料庫驗證 migration、constraint、transaction、rollback 與並行情境。涉及 API 時，需啟動應用程式並驗證 OpenAPI、正常與錯誤回應、授權及 log。若無法執行某項驗證，交付時必須明確列出原因，不得宣稱全部通過。

## 17. 變更與交付原則

- 優先做最小必要修改，維持既有公開 API、資料契約與相依方向。
- 不進行與需求無關的大型重構、框架替換或過度設計。
- 不提交 `bin/`、`obj/`、測試結果、coverage、秘密、個資或本機專用設定。
- 不以 TODO、假資料、空 method、未實作 exception 或只通過編譯的 scaffold 宣稱功能完成。
- Git commit message 使用繁體中文，一個 commit 聚焦一個可說明的變更。
- 交付時列出修改檔案、行為差異、影響範圍、風險、已執行驗證與未驗證項目。
- 修改 API、授權或 Schema 時，必須明確提醒前端、資料遷移與相容性影響。

## 18. 架構底線

- Controller 不直接承擔 SQL、交易、授權規則、Email 與複雜 DTO mapping 等多重責任。
- Domain 與 Application 不依賴 ASP.NET Core 或 Infrastructure implementation。
- 授權必須在 Backend API 的每個 use case 落實，前端顯示控制不是安全邊界。
- 跨多筆資料的業務操作必須有清楚 transaction boundary，不得造成部分成功。
- 外部服務失敗不得破壞已確認的資料一致性；retry、timeout 與補償策略需符合操作的冪等性。
- 未確認的需求、Schema 與技術選型必須標示為待確認，不得以實作結果反向創造規格。
