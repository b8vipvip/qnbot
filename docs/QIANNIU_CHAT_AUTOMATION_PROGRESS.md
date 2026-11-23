# Qianniu Chat Automation Progress / 千牛聊天自动化进度

Last updated / 最后更新：2026-11-23

本文件记录当前生产基线、源码已完成项、尚需真实 Windows/千牛验收的项目，以及本轮新增的只读诊断工具。所有状态区分“源码实现”“CI/构建通过”“正式发布”“真实客户端验收”，不能互相替代。

## 1. Current production baseline / 当前生产基线

- Repository：`b8vipvip/qnbot`
- Default branch：`master`
- Current source baseline：以 GitHub `master` 当前 HEAD 为准；不要使用早期交接文档中的 2026-09 SHA。
- Latest stable client：bot-v1.1.3117
- Release target：ac17e76dae53d3318c208139efd46f8e7249b987
- x64 package SHA-256：3c82b949a0d6896ad0b4445a0a36d7f2aab9f3c209f4269c4285327967855cf7
- master 后续源码修复不会自动等同于已发布客户端；以 Release target SHA 为准判断客户端包含哪些代码。

## 2. Completed source capabilities / 已实现的源码能力

### 2.1 Message ingestion and session identity
- 生产消息接收仍以注入脚本、WebSocket 41010、CDP/QN 事件为主。
- 买家文本、图片、订单等事件进入统一处理链；非买家会话隔离、精确消息去重和会话身份校验已实现。
- 多店运行时协调器以 ShopKey、物理 CDP SessionId、TargetId 和聚焦 WebView 证据仲裁活动店铺；不允许仅凭卖家昵称授权跨店操作。
- CDP 关闭会话会释放对应 client、等待请求和店铺偏好；重复页面按店铺身份保留必要的可恢复 standby。

### 2.2 Generation and reliable send
- 买家 burst coalescing、generation cancellation 和绝对生命周期 watchdog 已实现。
- generation 从 `BuyerActionAccepted` 起登记；55 秒绝对期限覆盖 Observed/Coalescing/Processing/Generating/Ready/Sending/Waiting 整个生命周期，只有终态才移除。
- CDP execute gate 排队上限约 1.5 秒；active buyer 确认有 9 秒总 wall-clock deadline。
- 正常文本 AI 预算约 50 秒；accepted generation 的绝对年龄门禁覆盖到 Ready/Sending/Waiting。
- CDP execute gate 有界等待；发送前验证 seller/buyer/current conversation 和 ShopKey。
- Bot-owned exact draft、可信提交证据、卖家 echo / delivery verification 和防盲目重复发送已进入生产链路。
- 平台提醒、人工接管、stale generation 和不确定提交保持 fail-closed。

### 2.3 Knowledge V2 and conversation learning
- 会话静默 5 分钟后进入完整历史复盘；远端历史分页、时间线和 Bot 卡片使用同一 ShopKey 范围。
- 完整历史缺失、消息数不一致、时间戳未知或知识语料版本漂移时 fail-closed。
- Knowledge V2 全库按批次对照，记录消息/语料覆盖率、批次检查点、模型/协议、版本指纹及候选证据。
- 上游错误按 ShopKey 熔断并支持断点续跑。
- **Knowledge V2 真人证据 FAQ 自动新增已启用**：AI 生成标准问法、答案和别名；必须绑定同店铺买家原始消息与非 Bot 人工回复的千牛原生消息 ID，通过完整历史、全量语料稳定指纹、内容安全、去重、原子事务、回滚记录和治理审计后，直接发布到正式库，不需要人工确认或灰度。
- 高置信真人纠正按知识 ID、原答案、消息证据和版本指纹执行自动修订；没有完整证据的通用会话建议和回复风格仍保持 dry-run。
- 高风险店铺规则、账户、付款、隐私和履约事实仍由独立业务流程处理，继续 fail-closed。

### 2.4 Multi-shop and catalog
- #426：CDP 身份切换、旧队列/迟到帧隔离。
- #429：ShopKey 私有配置、证据账本和流程草稿校验。
- #430：新店中性默认、旧店策略兼容和缓存生命周期。
- #431：Knowledge V2 熔断、完整语料断点和并发门资源管理。
- 商品目录刷新已使用稳定 ShopKey、权威 CDP 会话和响应后的 SessionId 复核；后台刷新不切换当前千牛工作台。

### 2.5 GPTAuto / release
- Reconcile 能按实际发布工作流发现 `Publish Bot auto-update release`。
- 手动发布按精确 source SHA 选取成功的 Windows x64 构建。
- 发布运行通过 workflow ID 和源 SHA display_title 关联；动态名称不再被静态工作流名称条件误过滤。
- 这些是发布流程代码门禁；仍需一次新的 Reconcile 任务真实派发并获得终态证据。

## 3. Remaining runtime acceptance / 尚需真实运行验收

### P0 — Message channel and mirror
关联原 Issue：原 Issue #307

- 最新正式版运行时，确认买家消息可收到正确 buyer + 正文。
- 检查 Bot Web 历史消息首次同步后持续显示，切换买家不串屏。
- 断线重连、千牛重启、多个窗口/账号切换后不丢失、不重复。
- 诊断 `AliRender --render_id=bench_im`、CDP target、`window.imsdk` / `invoke` / `on`、本地 41010 listener。
- 历史 #307 的千牛版本/设置线索不是当前版本验收证据。

### P1 — Multi-shop and lifecycle
- #426：店铺 A→B→A、同昵称不同店、物理 WebSocket 切换、旧 session 迟到帧。
- #429：同昵称多店、v1→v2 禁用态迁移、私有配置备份/恢复、外部进程修改后及时生效、证据撤回记录。
- #430：新店中性默认、升级保留旧配置、危险动作默认关闭。
- #341：多标签/多窗口循环后重复 standby 通道、订阅数量、内存/句柄/线程趋势；固定回复同义编译失败不能改变用户原文或触发错误发送。
- 以上必须以真实客户端和长时间运行结果验收，不能只看 CI。

### P1 — Knowledge V2 full learning
关联原 Issue：原 Issue #339 和 #431。

- 150+ 条真实测试会话完整历史；全部知识条目逐批覆盖，不能只分析检索命中片段。
- 重启后复用有效检查点；知识库修订漂移后必须重新分析。
- 模拟/观察 502、503、scheduler invariant 错误；确认 ShopKey 独立熔断、冷却、半开探测和重启恢复。
- 证明人工证据 ID 属于当前完整时间线；弱证据/高风险候选拒绝。
- 自动写入保持关闭，直到原子事务、备份、回滚、审批主体和灰度验收全部完成。

### P1 — General reception workflow Agent
关联原 Issue：原 Issue #346。

- Reception Agent 已改为 ShopKey 隔离的 v2 active catalog（PR #442 已合并）；通过结构校验后由 Agent 直接发布，无需管理员审批或强制灰度。
- 发布事务具备目录 revision CAS、单调 flow_version、不可变历史快照、自动回滚和旧 draft v1 文件迁移。
- 流程动作限制为等待、补充证据、转人工、只读查询和受现有发送门控约束的回复意图；目录不能授予付款、充值、退款、账号修改或实际履约权限。
- 完整通用状态机执行器尚未接入全部消息/OCR/订单/账号证据源；active catalog 可发布不等于所有流程转移已在真实千牛环境执行验收。

### P1 — GPTAuto Reconcile end-to-end
- 在修复后的 master 上真实 dispatch 一次 Reconcile。
- 验证源 SHA、Windows 构建关联、发布工作流动态 display_title、Completion Lease 最终状态。
- Observer 成功或 Executor workflow 启动都不能单独视为任务完成。

### P1 — Product catalog runtime
- 真实在线店铺验证完整目录/部分目录合并、手动刷新、断线重连恢复和多店切换不串店。
- 确认后台刷新不会切换用户当前千牛工作台。

## 4. New read-only Windows diagnostics / 新增只读 Windows 诊断工具

脚本：`scripts/Invoke-QnBotAcceptanceDiagnostics.ps1`

在仓库根目录运行：

```powershell
powershell.exe -NoProfile -File scripts\Invoke-QnBotAcceptanceDiagnostics.ps1
```

默认输出到：

`%LOCALAPPDATA%\QianniuAiBot\diagnostics\`

报告包括：
- Windows / PowerShell / .NET Framework 版本；
- Bot、AliWorkbench、AliRender、wwcmd 进程数及资源指标；
- 是否找到 `--render_id=bench_im` 渲染进程，并区分进程命令行可见/不可见；
- Bot 产品版本优先从安装包根目录 `release-info.json` 读取，程序集 `1.1.0.0` 仅作为兼容回退；同时输出 Release tag 与源 SHA；
- 本地 41010 WebSocket listener、loopback 可达性及进程归属；
- 未确认 bench_im 身份时，只列出千牛进程拥有的 loopback 监听候选端口，不连接探测；
- 能够唯一关联时，通过 loopback CDP 只读检查页面 `document.readyState`、`typeof window.imsdk`、`invoke`、`on` 和 loginID/QN 是否存在；
- JSON 与 Markdown 两种报告。

隐私与安全边界：
- 不读取或导出聊天正文、客户昵称、订单、Cookie、Token、原始日志、截图、完整命令行或页面 URL。
- 命令行仅在内存中用于识别 renderer 和调试端口，报告只保存布尔值/端口号。
- 只连接 `127.0.0.1`，CDP 只执行属性类型/布尔值读取，不发送消息、不修改设置、不停止进程、不上传文件。
- 如果 CDP 目标无法唯一关联，工具跳过探测并明确报告，不猜测页面身份；命令行不可见会显示为 indeterminate，而不是误报为未发现。
- 该工具不能证明真实消息已送达；真实买家消息、切店、重连和知识学习仍需现场验收。

已有可选长时采样脚本：
- `scripts/Collect-QnBotMemory.ps1`：只读采样系统/进程内存、CPU、句柄和线程；同时按采样周期读取 Bot 的伪名化 `runtime-snapshot.json`，记录店铺 owner 会话变化、重复/未映射会话、standby 能力和 Bot 进程重启。
- Knowledge V2 云同步服务写出 `knowledge-v2-sync-snapshot.json`，仅含 ShopRef、云端 revision、记录数、截断指纹、收敛状态和最近同步状态；采样脚本会校验它与当前 Bot 进程身份一致后再记录。
- Knowledge V2 全量学习服务写出 `knowledge-v2-learning-snapshot.json`，仅含会话/店铺伪名、历史消息数量、语料记录数、已审查记录数、批次完成数、覆盖率、断点恢复次数和指纹前缀，不包含聊天正文、买家昵称、卖家昵称或知识内容。
- 150+ 消息验收：检查 `KnowledgeV2LatestHistoryMessagesInWindow >= 150`、`KnowledgeV2LatestCorpusComplete=True`、`KnowledgeV2LatestRecordsReviewed = KnowledgeV2LatestCorpusRecords`、`KnowledgeV2LatestBatchesCompleted = KnowledgeV2LatestBatchCount`。重启恢复验收还需看到 `KnowledgeV2LatestResumeCount` 增长，并在最终完成后重新满足上述完整性条件。
- A/B 切店与重连采样示例：`powershell.exe -NoProfile -File scripts\\Collect-QnBotMemory.ps1 -Minutes 60 -IntervalSeconds 15 -ScenarioTag shop-switch`。运行期间手动执行 A→B→A 切换和断线重连；CSV 的 `RuntimeShopOwners`、`RuntimeOwnerChangesSincePreviousSample`、`RuntimeDuplicateSessions`、`RuntimeUnmappedConnectedSessions` 可用于检查身份稳定性。
- 跨设备 Knowledge V2 对比：在两台设备分别运行采样，确认同一店铺的 CloudRevision、LocalFingerprintPrefix、CloudFingerprintPrefix 一致且 Converged=True。ShopRef 是进程本地伪名，重启后会轮换，不用于跨设备匹配。
- `scripts/Collect-QnBotGcTrace.ps1`：管理员权限下有界 CLR GC ETW；ETL 可能含同机其他 .NET 程序事件，分享前需自行审查。

### 4.1 2026-11-20 Windows diagnostic follow-up

- Stable client baseline: `bot-v1.1.2481`, release target `35f5ed0138eba56e3f341915b7e2a800c102007d`. 
- The uploaded runtime sample had one connected authoritative session, no duplicate standby, and Bot-owned loopback WebSocket 41010. It did not identify a bench_im renderer or unambiguous CDP endpoint.
- Corrected the runtime snapshot counter so `RecoverableStandbySessions` counts actual duplicate standby sessions that also advertise recoverable capability; `StandbyCapabilitySessions` separately counts all capable sessions, including the owner.
- Follow-up diagnostics now report AliRender command-line visibility, tolerate quoted renderer/debug-port arguments, list Qianniu-owned loopback listener candidates without probing them when bench_im identity is unknown, and read product version/tag/source SHA from the package-root `release-info.json` instead of presenting the assembly compatibility version as the product version.
- The diagnostic follow-up source/tests were validated on exact SHA and included in `bot-v1.1.2470`; they are not included in `bot-v1.1.2460`. `bot-v1.1.2481` retains those diagnostics and adds the later multi-shop identity and alias-cache fixes.

### 4.2 2026-11-20 continued repair

- GPTAuto Reconcile recovery: Executor no longer treats an old `gptauto-reconcile-dispatched` comment as terminal proof. It skips a recovery dispatch only when the matching task has a durable `Completion Lease is RELEASED`, `Terminal state: DONE`, and exact `Merge SHA` receipt. Otherwise it re-dispatches and updates the existing handoff comment.
- At the time of this 2026-11-20 update, PR #434 task `GA-ddf0aa319783` (merge SHA `483b07f03eeeeef5e9339b286d2c179378489ccc`, `release_required=false`) remained ACTIVE after Reconcile run `44677995005` failed during terminal capture because it searched only for canonical PR workflow `CI`. The task was later recovered to terminal DONE with Completion Lease RELEASED on 2026-11-21; see section 4.3.
- Knowledge V2 write gate: base session suggestions and reply-style profile updates are now both blocked unless the full Knowledge V2 corpus analysis completes with a stable source revision. Incomplete coverage/version drift cannot write any learned content. `EnableAutomaticWrites` remains hard-disabled.
- Virtual-goods delivery recovery: read-only verification now classifies seller/ShopKey identity failures and exceptions as `ConfirmationUncertain`, so repeated read-only failures reach the existing five-attempt manual-review path instead of retrying forever as generic Deferred. The consumed confirmation ledger remains a no-repeat barrier; this does not authorize another confirmation click.
- Validated product-source SHA: `8fc1c21440f351ce336a58f2751ef16afae4cd97`. API Full Gate passed: Fast Gate `1682 passed, 16 skipped`, API package tests `141 passed`, Windows Static Gate passed, and Windows x64 build `44752223276` completed successfully. Documentation-only handoff updates followed this source SHA.
- Release `bot-v1.1.2470` was published for exact product-source SHA `8fc1c21440f351ce336a58f2751ef16afae4cd97`; x64 package SHA-256: `30ac46a1791f986e05bc836adcd67a7d74ef9db6d88d22705aa66539bc419830`. These fixes are included in 2470, but local installation and real Qianniu runtime acceptance remain unverified.

### 4.3 2026-11-21 continued repair

- **Buyer identity alias cache**: replaced TTL-only cleanup with a bounded cache policy. Cleanup starts at 5,000 entries and evicts least-recently-used mappings toward 4,000; a single interlocked cleanup runs at a time, updates share a record lock, and key/value removal is conditional. This prevents fresh aliases from growing without bound and avoids scanning the whole dictionary on every message once the cache is back under the threshold.
- **Multi-shop message identity**: buyer inbound messages and seller echoes now validate the seller endpoint and login nickname; when stable seller TargetId is available, conflicting endpoint/login TargetIds are rejected even if the visible nickname matches.
- **Timeline shop scope**: `ConversationContextStore.RefreshAndRecord` now rejects writes when the message seller cannot be resolved to the ambient ShopKey, and skips ambiguous seller identity instead of creating a synthetic unresolved timeline entry.
- **GPTAuto historical Reconcile**: fixed the Executor so an old dispatch marker is not treated as terminal proof. Added task-aware Reconcile run titles and a temporary exact-evidence recovery workflow. It re-dispatched PR #434 task `GA-ddf0aa319783` only after current master API/Windows gates, exact PR-head API/Windows gates, exact merge-SHA CI, and an ACTIVE lease were verified. Reconcile run `44858934200` completed successfully; Completion Lease is RELEASED, Terminal state is DONE, Merge SHA is `483b07f03eeeeef5e9339b286d2c179378489ccc`. The one-time recovery workflow was removed after the task closed; generic Executor recovery logic remains.
- **Exact-SHA validation**: release source SHA `35f5ed0138eba56e3f341915b7e2a800c102007d`; Fast Gate `1686 passed, 16 skipped`; API package tests `141 passed`; Windows Static Gate passed; Windows x64 build `44858618045` succeeded. Post-release cleanup master SHA `5fce1c3869b19dd044cd4d39db31ae9456df1b64` also passed API Full Gate and Windows x64 build `44859065309`.
- **Stable release**: `bot-v1.1.2481` published for exact source SHA `35f5ed0138eba56e3f341915b7e2a800c102007d`; x64 package SHA-256: `126e6c96545dd962c0e33c7626575106dda457c19b83732d0c3746ba22183abf`. The fixes above are included in this release. Local installation is not verified.
- **Still requires real Windows/Qianniu acceptance**: bench_im/CDP target identification, A→B→A multi-shop switching and reconnect stability, 150+ message Knowledge V2 full-corpus learning/checkpoint recovery, real order auto-reply/auto-delivery recovery, and Reception Agent approval/authorization/publish/rollback. At this 2026-11-21 checkpoint, the new FAQ auto-publish path had not yet landed; section 4.5 records the later master release. High-risk order operations remain fail-closed. Issue #366 stays open.

## 4.4 2026-11-22 Knowledge V2 AI auto-publish implementation

Historical pre-merge implementation snapshot. Its former branch status is superseded by section 4.5, which records the direct master commits and stable release.

- The new FAQ pass asks AI to generate a canonical question, answer, aliases and keywords instead of returning only source-message IDs.
- Each generated record must still cite one exact buyer message and one later non-Bot human seller reply from the same complete ShopKey transcript. AI-generated text is checked against the source pair; unsupported numeric facts, PII, high-risk/dynamic business facts, prompt injection and unrelated aliases/keywords are rejected.
- Safe FAQ records are committed directly to Knowledge V2 as enabled `active` production records. There is no human approval queue and no gray rollout for this narrow path.
- The write path performs a second ShopKey and corpus-fingerprint check under the repository lock, source-pair idempotency, title/alias collision detection, one-transaction record + rollback-revision persistence, readback verification, legacy mirror, incremental retrieval-index refresh and governance audit.
- AI auto-published provenance is distinguished from explicit human-confirmed provenance so the record remains production-approved without falsely claiming an operator approved it.
- Generic session suggestions without a valid buyer/human evidence pair, style changes and workflow proposals remain outside this direct-publish path. Order/payment/account/fulfillment actions continue to use their independent fail-closed authorization logic.
- Prompt version/checkpoint version was bumped so old ID-only candidate checkpoints are not reused as AI-generated knowledge.
- At the time this section was written, exact-SHA CI/Windows validation and release were still pending; those gates and the stable release are recorded in section 4.5.

## 4.5 2026-11-22 master auto-publish, retrieval hardening and stable release

- Knowledge V2 AI auto-publish is now on master: the dedicated FAQ pass generates the canonical question, answer, aliases and keywords, but every published record must cite the original Qianniu-native buyer-message ID and later non-Bot human-reply ID from the same complete ShopKey transcript. The AI-generated content is checked against the source pair; unverifiable rewrites fall back to the original buyer question and human answer.
- The narrow FAQ path writes directly to active production Knowledge V2 with a rollback revision, exact corpus-fingerprint CAS, source-pair idempotency, collision checks, readback verification, legacy mirror, immediate retrieval refresh and governance audit. It does not require human approval or gray rollout. Generic session suggestions and style changes remain dry-run; high-risk order/payment/account/fulfillment actions remain fail-closed.
- Additional retrieval bypass fix (a3b6697c363df6c540266a2629568e21d7ee062b; regression test ad8575dc5dc37d45c9fa10d019a64ba5c8529551): auto-published human-evidence records can now enter production ranking only through an exact saved title/alias or the strict semantic gate (score, alias, predicate, intent, confidence, entity/subject, standalone and risk checks). The generic production fallback can no longer bypass that gate.
- Exact-head API control-plane CI on ad8575d: success, Fast Gate 1797 passed / 16 skipped, Windows Static Gate and Full Gate / Build Package succeeded. Run ID: 44998075693.
- Exact-head Windows x64 release build: success, including PowerShell 5.1 checks, lifecycle regression tests, MSBuild x64, complete package assembly and verification. Run ID: 44998075683.
- GPTAuto Observer/Executor runs succeeded. Stable release: bot-v1.1.2821, target ad8575dc5dc37d45c9fa10d019a64ba5c8529551; x64 package SHA-256: 33b5ba7557af5d422c1534746f92c741400dbab4689588977d705ff471333ddd. Release tag: bot-v1.1.2821.
- This verifies source, tests, Windows build and release packaging only. Real Windows/千牛 validation remains open: 150+ messages and full-corpus resume, cross-device sync, multi-shop identity switching, manual takeover/automatic reply races, live test-order delivery recovery, long-running WS/CDP stability and Reception Agent authorization/publish/rollback. Issue #366 remains open until these runtime gates have evidence.


## 4.6 2026-11-22 additional reliability fixes and Knowledge V2 cloud CAS

### Windows client fixes included in stable release bot-v1.1.2832
- **Bot Web sync acknowledgement race**: pending command-result snapshots are taken under a per-shop lock; writes and acknowledgement use the same lock; a successful sync removes only the exact result object that was sent. A duplicate/newer result with the same command ID can no longer be deleted by an older in-flight acknowledgement.
- **Legacy auto-delivery recovery**: a queue record loaded without ShopKey is no longer permanently stranded just because the shop was offline or ambiguous during startup. Each retry attempts the unique seller-to-shop resolver; only an exact, unambiguous ShopContext can rebind the record. The queue key and ShopKey are atomically persisted before any order preflight. Same-order collisions remain deferred.
- **Legacy confirmation ledger**: completion resolves both current ShopKey and legacy seller/order barrier records, but does not delete the durable at-most-once barrier.
- **Auto-reply final gate**: if recent conversation history or the typed human-takeover event stream cannot be read, the final relevance check now suppresses sending rather than fail-open allowing a potentially stale or duplicate reply.
- **WS/CDP standby retirement race**: duplicate retirement is serialized with shop-session promotion; retirement revalidates connected state, exact duplicate ShopKey mapping and non-authoritative ownership; retiring standby sessions cannot be promoted while a retire command is pending. This prevents a stale duplicate-close command from closing a page after it has become the authoritative session.
- Windows x64 build exact source a3d27378bbae032317d54ede848f9b316d769534 succeeded; Release bot-v1.1.2832 published. Package SHA-256: 0b8dc64a614c7678549098e8d7417a4c7ac1dc3d0e447a2185663b920d3c76a7. Build run ID: 45000146254.

### Knowledge V2 web/cloud consistency fix on current master
- The web editor previously performed unconditional whole-corpus writes. Two devices editing from the same stale snapshot could silently overwrite one another.
- Web PUT now requires `expected_revision`; the service performs an atomic SQLite revision compare-and-swap and returns HTTP 409 with the current revision on conflict.
- The web client remembers the loaded revision, submits it with every edit, refreshes the latest cloud records on conflict and leaves the editor open so the user can reapply the change. The UI also retains the actual revision while filtering the list.
- Updated the static asset cache version to v11 so browsers do not continue using the old no-CAS editor.
- This web/API fix is on master and is **not part of the Windows x64 ZIP**; it requires the API control-plane service/static assets to be deployed.
- Current master API CI on d1a6885f28fb210421ca968a529a519c6ea8c499: Fast Gate **1803 passed / 16 skipped**, Windows Static Gate success, Full Gate / Build Package success (**143 passed**). GPTAuto Observer success.

### Remaining runtime boundary
- Real two-device synchronization and concurrent edit testing are still required to verify deployed API behavior and actual Windows client convergence.
- Real 150+ message full-corpus learning/restart recovery, multi-shop A/B/A, WS/CDP long-running stability, human takeover races, real test-order delivery recovery and Reception Agent trusted approval/publish/rollback remain open under Issue #366. CI and release packaging are not runtime acceptance.

## 4.7 2026-11-22 continued reliability and cloud consistency review

### Knowledge V2 data integrity
- **Full semantic conflict comparison**: cloud/local records sharing an ID are now compared across subject, predicate, aliases, entities, conditions, exclusions, required context, product IDs, short answer, risk, source provenance, authority and confidence, in addition to title/answer/status/type/intent/enabled. A semantic difference becomes a disabled review candidate instead of being silently discarded.
- When semantic content matches, monotonic counters are merged by max and CreatedAt/UpdatedAt/LastVerifiedAt metadata is reconciled without creating unnecessary conflict candidates.
- **Cloud revision namespace reset recovery**: if a token rebind or server-side state reset causes cloud revision to fall behind the local checkpoint, the client first backs up the complete local corpus, resets only the local sync checkpoint, and re-enters revision negotiation. Existing cloud records are merged on the next poll; local-only facts are preserved as disabled conflict candidates. It no longer retries forever on a permanent revision rollback.
- **Non-finite score protection**: API runtime settings reject NaN/Infinity; record Authority/Confidence must be finite and within [0,1]; record IDs must be unique; text/list/boolean/date fields are type-checked before persistence. Smart Import, one-line AI import, repository normalization, confidence policy, feedback and reply-score clamps reject or normalize non-finite values.
- Web Knowledge V2 PUT still uses expected_revision CAS (HTTP 409 on stale edits). The API runtime sync route is covered by the existing global shop-binding middleware installed in bootstrap; a separate missing ShopKey-binding defect was not present.

### Validation and release
- Current master: e13701b6711646f0090d36224f772413c062ddc6.
- API CI exact-SHA Fast Gate: **1808 passed / 38 skipped**; Windows Static Gate success; Full Gate / Build Package: **151 passed**; the added Knowledge V2 bridge integration regression suite: **30 passed**.
- Latest stable Windows x64 release: **bot-v1.1.2869**, source bce2fbdd0b257b1d098886cf64bb36d45fdce3fd, build run 45004427073; package SHA-256: 7302cbc9f9aa18a8d84c2244ebb5dabfbaae0e0d56ae32974564b14d9929373a. The source is a descendant of the earlier cloud-reset fix commit 075000816cd62e286a2288463efff1a1574ea577 and includes the Windows-side fixes through the verified build.
- The latest API schema/finite-validation changes and the new Full Gate integration-test step are on master; they are **not part of the Windows ZIP** and require deployment of the API control-plane service. No new Windows product-code changes were made after the bot-v1.1.2869 source.
- The full release list was compared by commit ancestry: bot-v1.1.2869 is four commits ahead of bot-v1.1.2865, not a source rollback.

### Remaining runtime boundary
- Issue #366 remains open. CI/build success does not prove real Qianniu behavior.
- Still requires real 150+ message/full-corpus learning and restart recovery, two-device convergence/concurrent edits, multi-shop A→B→A and WS/CDP long-run stability, human takeover/withdrawal/retry races, and real test-order delivery recovery.
- Reception Agent autonomous publication and rollback are merged in PR #442 and included in bot-v1.1.3117; the remaining boundary is the runtime state-machine/evidence adapter and real Windows/Qianniu acceptance, not an administrator approval identity.

## 4.8 2026-11-23 Reception Agent autonomous publication policy

- PR #442 merged the versioned active catalog into master at `ac17e76dae53d3318c208139efd46f8e7249b987`; the implementation is included in stable client `bot-v1.1.3117`.
- A schema-valid publish immediately updates the active version; no administrator approval identity, approval token, or mandatory gray rollout is required.
- The publish operation uses ShopKey-scoped storage, cross-process locking, revision CAS, monotonic per-flow versions, immutable published-history snapshots, and a rollback that creates a new revision.
- Existing v1 draft files are migrated to active v2 only after validation; the original file is retained.
- Follow-up PR #443 fixes a multi-shop race by resolving one ShopContext once and reusing it for active catalog, legacy migration, history snapshots and rollback paths; it is under exact-SHA CI/Windows build validation.
- Catalog actions remain constrained. Workflow publication does not grant payment, recharge, refund, account mutation, or fulfillment authority. Reply intents still pass through the existing canonical send pipeline.
- The runtime adapter that consumes the active catalog for every generic stage/evidence transition is not yet complete. Do not describe the workflow engine as fully live until that adapter and real Qianniu acceptance are complete.

## 5. Validation and release rules / 验收与发布规则

1. 源码修复必须有回归测试；Windows 专属脚本必须通过 Windows PowerShell 5.1 parser。
2. CI 和构建必须针对精确 master SHA 通过。
3. Windows/千牛行为、消息投递、真实 CDP 目标和资源趋势必须由真实运行证据验证。
4. 诊断报告先由用户检查，脱敏后再提供；不要求上传原始日志或客户数据。
5. Knowledge V2 真人证据 FAQ 自动新增已在 master/稳定版启用：满足原生消息证据、完整覆盖、版本、内容安全、去重、原子写入和审计门禁后直接发布，不需要人工确认或灰度。通用 AI 建议和风格学习仍 dry-run；高风险接待流程动作继续 fail-closed。
6. 发布与生产安装是不同动作；GitHub Release 不能替代用户确认安装或部署。
7. #366 是唯一统一追踪 Issue；不新增普通 Issue/分支，持续在 master 与 #366 更新。

## 6. Next execution / 下一步

1. 在真实千牛会话中完成 150+ 条消息、完整 Knowledge V2 语料覆盖、重启断点恢复及跨设备同步验收。
2. 完成同昵称多店铺 A→B→A、WebSocket/CDP 断线重连和长时间运行验收。
3. 验证自动回复的人工接管、撤回、失败重试、并发与防重复发送。
4. 使用测试订单验收自动发货的状态核验、崩溃恢复和防重复确认。
5. 完成 Reception Agent active catalog 的运行时状态机接入、图文/订单证据闭环、回滚恢复验收及 GPTAuto Reconcile 真实终态验收。
