# Qianniu AI Bot 项目交接上下文

更新时间：2026-11-20  
仓库：`b8vipvip/qnbot`  
默认分支：`master`  
当前正式客户端：`bot-v1.1.2470`  
统一未完成事项：Issue #366

## 1. 当前目标

继续以当前 master 源码、精确 SHA 的 GitHub Actions 和最新正式客户端的真实运行证据为准，优先保证：

1. 买家消息接收、buyer/seller/ShopKey 身份和 Bot Web 消息镜像；
2. 多店铺 CDP 会话隔离、重连和长期资源生命周期；
3. Knowledge V2 完整会话、全语料覆盖、检查点恢复和安全候选；
4. 通用接待流程 Agent 的证据、审批、授权、回滚闭环；
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

- Latest stable release：`bot-v1.1.2470`
- Release target：`8fc1c21440f351ce336a58f2751ef16afae4cd97`
- x64 package SHA-256：`30ac46a1791f986e05bc836adcd67a7d74ef9db6d88d22705aa66539bc419830`
- 之后 master 上的源码修复必须通过精确 SHA CI/Windows build；Release target 之前的客户端不会自动包含后续提交。
- Validated product-source SHA: `8fc1c21440f351ce336a58f2751ef16afae4cd97`; API Full Gate passed (Fast Gate 1682 passed / 16 skipped, API package tests 141 passed, Windows Static Gate passed) and Windows x64 build run `44752223276` completed successfully. Later master commits in this sequence are documentation-only.
- Release `bot-v1.1.2470` includes the GPTAuto Reconcile recovery marker fix, Knowledge V2 full-corpus write gate, and read-only auto-delivery manual-review escalation. Automatic Knowledge V2 writes remain disabled and real Qianniu acceptance remains outstanding.
- Historical GPTAuto task PR #434 (`GA-ddf0aa319783`, merge `483b07f03eeeeef5e9339b286d2c179378489ccc`, release_required=false) remains ACTIVE after Reconcile run `44677995005` failed at terminal capture. Current master can retry the handoff, but a new Reconcile workflow_dispatch with the original task/PR/merge inputs is still required to obtain terminal DONE evidence.
- 2026-11-20 上传的 Windows 诊断显示：Bot-owned 41010 正常、1 个权威会话、0 个重复 standby；bench_im/CDP 目标尚未识别。后续诊断脚本正在补充命令行可见性、loopback 候选端口和 release-info.json 产品版本识别，尚需 exact-SHA CI/Windows build。
- 生产安装仍需要管理员/用户明确操作；CI 不得触发生产主机部署。

## 4. Repository status

- 当前没有开放 PR；#426/#429/#430/#431 已合并到 master。
- 唯一开放的普通跟踪 Issue 为 #366；不新建普通 Issue 或分支。
- 下一步应使用 `bot-v1.1.2470` 真实运行证据继续验收；后续 master 源码修复须先通过精确 SHA CI/Windows build。

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
- Knowledge V2 automatic writes remain disabled (dry-run). No production write enablement without transaction/backup/rollback and real Windows canary.
- PRs #426/#429/#430/#431 are already merged into master and included in the 1.1.2379 lineage; 1.1.2380 adds the ShopKey catalog refresh/settings fix.
- GPTAuto Release discovery, exact source SHA build selection and dynamic display_title correlation have been corrected in master; end-to-end Reconcile terminal evidence remains pending.

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
- Keep automatic writes disabled until human approval, atomic transaction, backup and rollback are verified.

### P1 — reception workflow Agent (#346)
- Persisted multi-shop/product/SKU/version workflow state.
- Trusted text/image/OCR/order evidence chain.
- Authenticated approver and authorized scheduler; manual takeover and rollback.
- Shadow decisions → human review → small canary → reversible release.
- High-risk actions remain disabled by default.

### P1 — GPTAuto Reconcile
- Dispatch one real Reconcile task after the workflow fixes.
- Verify exact source SHA, Windows build association, dynamic release run title and terminal Completion Lease state.
- Observer success alone is not terminal completion.

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
- Keep dry-run and fail-closed defaults until the relevant safety acceptance gates pass.
