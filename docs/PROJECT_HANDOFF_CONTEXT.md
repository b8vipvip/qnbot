# Qianniu AI Bot 项目交接上下文

更新时间：2026-11-23  
仓库：`b8vipvip/qnbot`  
默认分支：`master`  
当前正式客户端：`bot-v1.1.3117`  
统一未完成事项：Issue #366

## 1. 当前目标

继续以当前 master 源码、精确 SHA 的 GitHub Actions 和最新正式客户端的真实运行证据为准，优先保证：

1. 买家消息接收、buyer/seller/ShopKey 身份和 Bot Web 消息镜像；
2. 多店铺 CDP 会话隔离、重连和长期资源生命周期；
3. Knowledge V2 完整会话、全语料覆盖、检查点恢复和安全候选；
4. 通用接待流程 Agent 的自主发布、证据闭环、运行时执行和回滚；
5. GPTAuto Reconcile / Release 工作流精确 SHA 关联。

不得将源码实现、CI 通过、Release 发布和 Windows/千牛实机验收混为同一个完成状态。

## 2. Repository instructions and safety

- 修改前先读 `.github/GPTAUTO_SMART_UPDATE.md`、本交接文档、`docs/QIANNIU_CHAT_AUTOMATION_PROGRESS.md`；涉及发送链时再读 `docs/QIANNIU_SEND_RELIABILITY_INCIDENTS.md`。
- 生产消息接收仍以注入脚本、WebSocket 41010、CDP/QN 事件为主；UIA v2 / IMSDK 研究工具不得未经真实验证接入主链。
- 不使用固定 AliRender PID；通过命令行中的 `--render_id=bench_im` 识别 renderer。
- 真实发送测试默认 dry-run，必须显式确认，只能一次发送且不得自动重试。
- 不提交真实客户名称、账号标识、聊天正文、截图、订单/SKU 或令牌。
- 用户的 PowerShell/PSReadLine 不适合粘贴长多行命令；优先提交脚本并提供短命令。
- 所有高风险付款、账户、退款、履约和隐私动作必须 fail-closed。

## 3. Current release baseline

- Latest stable release：`bot-v1.1.3111`
- Release target：`ac17e76dae53d3318c208139efd46f8e7249b987`
- x64 package SHA-256：`3c82b949a0d6896ad0b4445a0a36d7f2aab9f3c209f4269c4285327967855cf7`
- 之后 master 上的源码修复必须通过精确 SHA CI/Windows build；Release target 之前的客户端不会自动包含后续提交。
- Validated release-source SHA: `35f5ed0138eba56e3f341915b7e2a800c102007d`; API Full Gate passed (Fast Gate 1686 passed / 16 skipped, API package tests 141 passed, Windows Static Gate passed) and Windows x64 build run `44858618045` completed successfully. Post-release cleanup master SHA `5fce1c3869b19dd044cd4d39db31ae9456df1b64` also passed API Full Gate and Windows x64 build run `44859065309`.
- Release `bot-v1.1.2481` includes the GPTAuto Reconcile recovery marker fix, Knowledge V2 full-corpus write gate, read-only auto-delivery manual-review escalation, bounded buyer alias cache, seller TargetId ingress validation and ambient ShopKey timeline guard. Automatic Knowledge V2 writes remain disabled and real Qianniu acceptance remains outstanding.
- Historical GPTAuto task PR #434 (`GA-ddf0aa319783`, merge `483b07f03eeeeef5e9339b286d2c179378489ccc`, release_required=false) is now terminal DONE. Reconcile run `44858934200` succeeded; Completion Lease is RELEASED with the exact merge SHA. The one-time recovery workflow was removed after success; the generic Executor retry fix remains.
- 2026-11-20 上传的 Windows 诊断显示：Bot-owned 41010 正常、1 个权威会话、0 个重复 standby；bench_im/CDP 目标尚未识别。后续诊断脚本正在补充命令行可见性、loopback 候选端口和 release-info.json 产品版本识别，尚需 exact-SHA CI/Windows build。
- 生产安装仍需要管理员/用户明确操作；CI 不得触发生产主机部署。

## 4. Repository status

- 当前没有开放 PR；#426/#429/#430/#431 已合并到 master。
- 唯一开放的普通跟踪 Issue 为 #366；不新建普通 Issue 或分支。
- 下一步应使用 `bot-v1.1.2481` 真实运行证据继续验收；后续 master 源码修复须先通过精确 SHA CI/Windows build。

## 5. Architecture

- Windows WPF Bot：`src/Bot/`
- Qianniu WebSocket/CDP/QN and send chain：`src/Bot/ChromeNs/`
- Knowledge Center V2：`src/Bot/Knowledge/`
- Shop identity and control-plane scope：`src/Bot/ShopScope/`
- Ubuntu FastAPI control plane：`services/api-control-plane/`
- Discovery lab：`tools/qn_discovery_lab/`
- Windows diagnostics：`scripts/`
- Regression contracts：`tests/`

生产接收主链：

```text
Qianniu renderer
  → injected script / local WebSocket 41010
  → physical CDP session + ShopKey authority
  → buyer/seller conversation identity
  → message dedupe / burst coalescing / generation
  → Knowledge V2 / AI
  → Bot-owned draft + active conversation guard
  → verified submission + seller echo / delivery evidence
```

## 6. Current implementation state

- Multi-shop runtime coordinator uses exact ShopKey / CDP SessionId / TargetId and focused WebView proof.
- Generation watchdog, bounded CDP execute gate, active buyer confirmation deadline and reliable-send evidence checks are in the production source.
- Knowledge V2 session learning uses five-minute inactivity, complete paginated history, full-corpus batches, durable checkpoints, source/transcript fingerprints and per-ShopKey circuit breakers.
- The current `master` / stable client baseline still has the previous dry-run policy. Working branch `feat/knowledge-v2-human-evidence-auto-add` adds a separate AI-authored FAQ path: AI generates normalized questions, answers, aliases and keywords from exact buyer + non-Bot human-reply evidence; passing automatic checks writes active production V2 records immediately, with no operator approval queue or gray rollout. This branch is not yet in master or the stable client.
- BuyerIdentityAliasService now has a bounded 5,000-entry trigger / 4,000-entry LRU target with coalesced cleanup and conditional key/value removal.
- QN inbound buyer messages and seller echoes validate the seller endpoint/login nickname and stable TargetId when available; ConversationContextStore rejects timeline writes outside the exact ambient ShopKey scope.
- PRs #426/#429/#430/#431 are already merged into master and included in the 1.1.2379 lineage; 1.1.2380 adds the ShopKey catalog refresh/settings fix.
- GPTAuto Release discovery, exact source SHA build selection and dynamic display_title correlation are corrected in master; historical task #434 is now DONE with a RELEASED Completion Lease and exact Merge SHA receipt.

## 7. Next acceptance work

### P0 — message channel and mirror (#307)
- Confirm latest supported Qianniu client receives buyer identity + message body.
- Verify Bot Web history persists after initial sync and buyer switching never cross-renders old messages.
- Verify reconnect, restart, multi-window and multiple account behavior.
- Inspect AliRender bench_im, CDP target, IMSDK typeof probes and local 41010 ownership.

### P1 — multi-shop and long-running stability (#426/#429/#430/#341)
- A→B→A shop switching, same seller nickname across shops, stale frames and reconnect.
- Private profile v1→v2 migration, backup/restore and cross-process edits.
- Neutral defaults for new shops; old-shop strategy preserved; dangerous actions disabled.
- Duplicate standby channels, event subscription count, process memory/handles/threads under sustained operation.

### P1 — Knowledge V2 full learning (#339/#431)
- 150+ turns, complete timeline, all current V2 records covered in batches.
- Restart checkpoint resume, source revision drift, upstream 502/503 and scheduler invariant circuit recovery.
- Human evidence IDs must map to the exact current transcript.
- The current master baseline remains dry-run. The new branch intentionally removes human approval and gray rollout for the narrow AI-generated FAQ auto-publish path; it still requires exact-SHA CI/build, shop isolation, evidence/PII/risk/dedup/version gates, atomic transaction, rollback history and immediate index refresh before merge/release.

### P1 — Reception Agent autonomous workflow publication (#346)
- PR #442 changed the workflow catalog from draft-only to a ShopKey-scoped active catalog; validated Agent publications take effect immediately without administrator approval or mandatory gray rollout.
- PR #443 requires the exact ShopKey from the Knowledge Center/event, verifies seller↔ShopKey identity, and pins active, legacy and history paths to one resolved ShopContext. Publish/rollback use cross-process locking, revision CAS, monotonic per-flow versions, immutable history and legacy migration.
- The current branch adds AI workflow generation to session learning, alias-bound buyer/human-reply/visual/order evidence, immediate active-catalog publication, runtime-index invalidation/revision refresh, and a durable per-shop state machine integrated with canonical text and vision reply pipelines.
- Runtime state is restored after restart; stale versions/expired cursors are repaired; uncertain sends are not retried; repeated deterministic faults trigger revision-CAS rollback. HANDOFF requires a configured shop notification channel. READ_ONLY_TOOL remains fail-closed and is not auto-generated.
- Payment, recharge, refund, account mutation and actual fulfillment remain outside catalog authority and require separate authoritative evidence and idempotent execution guards.
- Remaining acceptance is real Windows/Qianniu execution: same-nickname multi-shop isolation, A→B→A, restart, long-running sessions, image/OCR progression, notification delivery, and end-to-end test-buyer workflows. CI/build success is not field acceptance.

### P1 — GPTAuto Reconcile regression maintenance
- Keep exact source SHA, Windows build association, task-aware run title and terminal Completion Lease checks covered by regression tests.
- Do not treat Observer/Executor success or a dispatch marker as terminal completion.
- Historical PR #434 is closed with terminal DONE evidence; future tasks must retain the same exact-SHA receipt discipline.

### P1 — order automation crash recovery
- Verify auto-reply and virtual-goods auto-delivery recovery on real test orders.
- Confirm restart recovery never repeats a consumed confirmation click or uncertain send.
- Verify persistent manual-review escalation and operator-visible recovery status.

## 8. Windows read-only diagnostics

Run from repository root:

```powershell
powershell.exe -NoProfile -File scripts\Invoke-QnBotAcceptanceDiagnostics.ps1
```

The script reports OS/.NET version, Bot/Qianniu process counts and resource metrics, bench_im renderer presence, local 41010 ownership/reachability, and a loopback-only read-only CDP probe for `window.imsdk`, `invoke`, and `on` when the target can be identified unambiguously.

It writes JSON and Markdown under `%LOCALAPPDATA%\QianniuAiBot\diagnostics\`. It does not collect chat text, account nicknames, cookies, tokens, target URLs, raw command lines, raw logs or screenshots; it does not send messages, modify settings, stop processes or upload reports.

Optional existing samplers:
- `scripts/Collect-QnBotMemory.ps1`: process/system memory, CPU, handles and threads.
- `scripts/Collect-QnBotGcTrace.ps1`: bounded CLR GC ETW; the ETL may contain events from other .NET apps and must be reviewed before sharing.

## 9. Acceptance and issue management

- #366 remains the only unified open issue.
- Do not create new ordinary issues or branches; continue direct source fixes and update #366.
- Every source fix gets regression coverage.
- Every Windows script must pass the Windows PowerShell 5.1 parser.
- Exact-SHA CI and x64 build are required before release.
- Real runtime behavior still requires real Windows/Qianniu evidence.
- Keep high-risk business operations fail-closed until their independent evidence and idempotency gates pass. Reception workflow definition publication itself is autonomous and does not require administrator approval.
