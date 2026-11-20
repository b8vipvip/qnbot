# Qianniu Chat Automation Progress / 千牛聊天自动化进度

Last updated / 最后更新：2026-11-20

本文件记录当前生产基线、源码已完成项、尚需真实 Windows/千牛验收的项目，以及本轮新增的只读诊断工具。所有状态区分“源码实现”“CI/构建通过”“正式发布”“真实客户端验收”，不能互相替代。

## 1. Current production baseline / 当前生产基线

- Repository：`b8vipvip/qnbot`
- Default branch：`master`
- Current source baseline：以 GitHub `master` 当前 HEAD 为准；不要使用早期交接文档中的 2026-09 SHA。
- Latest stable client：`bot-v1.1.2460`
- Release：GitHub Release tag `bot-v1.1.2460`
- Release target：`7ece1834642e558cfa9ff5eb45272a8bb12a4bc2`
- x64 package SHA-256：`98df42ddf2130bc61923fca033f5deaa049ade495fa61143ee319849f5177533`
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
- **生产自动写入仍保持关闭（dry-run）**。没有完成原子更新/回滚、人工审核授权和 Windows 灰度验收前，不得启用自动写入。
- 高风险店铺规则、账户、付款、隐私和履约事实必须保留人工审核。

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

- 当前流程模型以 ShopKey/商品/SKU/版本隔离的草稿和证据账本为基础。
- 尚需可信审批主体、授权调度器、正式发布/回滚、只读订单工具和完整图文证据闭环。
- 影子决策→人工复核→小流量灰度；高风险操作默认关闭。
- 不得把草稿可保存等同于 Agent 已能安全执行生产流程。

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
- `scripts/Collect-QnBotMemory.ps1`：只读采样系统/进程内存、CPU、句柄和线程。
- `scripts/Collect-QnBotGcTrace.ps1`：管理员权限下有界 CLR GC ETW；ETL 可能含同机其他 .NET 程序事件，分享前需自行审查。

### 4.1 2026-11-20 Windows diagnostic follow-up

- Stable client baseline: `bot-v1.1.2460`, release target `7ece1834642e558cfa9ff5eb45272a8bb12a4bc2`.
- The uploaded runtime sample had one connected authoritative session, no duplicate standby, and Bot-owned loopback WebSocket 41010. It did not identify a bench_im renderer or unambiguous CDP endpoint.
- Corrected the runtime snapshot counter so `RecoverableStandbySessions` counts actual duplicate standby sessions that also advertise recoverable capability; `StandbyCapabilitySessions` separately counts all capable sessions, including the owner.
- Follow-up diagnostics now report AliRender command-line visibility, tolerate quoted renderer/debug-port arguments, list Qianniu-owned loopback listener candidates without probing them when bench_im identity is unknown, and read product version/tag/source SHA from the package-root `release-info.json` instead of presenting the assembly compatibility version as the product version.
- The diagnostic follow-up source/tests are on master after the stable release and require exact-SHA CI/Windows build before they are used as the next field diagnostic. They are not included in `bot-v1.1.2460`.

## 5. Validation and release rules / 验收与发布规则

1. 源码修复必须有回归测试；Windows 专属脚本必须通过 Windows PowerShell 5.1 parser。
2. CI 和构建必须针对精确 master SHA 通过。
3. Windows/千牛行为、消息投递、真实 CDP 目标和资源趋势必须由真实运行证据验证。
4. 诊断报告先由用户检查，脱敏后再提供；不要求上传原始日志或客户数据。
5. Knowledge V2 自动写入、高风险接待流程动作保持关闭，直到审批/回滚闭环成立。
6. 发布与生产安装是不同动作；GitHub Release 不能替代用户确认安装或部署。
7. #366 是唯一统一追踪 Issue；不新增普通 Issue/分支，持续在 master 与 #366 更新。

## 6. Next execution / 下一步

1. 先完成 master 源码审计和回归测试。
2. 使用只读诊断脚本准备一份不含客户内容的 Windows 状态报告。
3. 依据报告和少量真实测试定位剩余消息通道/多店问题。
4. 再完成 Knowledge V2 全库覆盖与断点恢复验收。
5. 最后完成接待流程 Agent 审批、灰度与 GPTAuto Reconcile 端到端验收。
