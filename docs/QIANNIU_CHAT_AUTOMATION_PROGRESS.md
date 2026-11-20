# Qianniu Chat Automation Progress / 千牛聊天自动化进度

Last updated / 最后更新：2026-11-20

本文件只记录**当前生产状态和仍需验证的事项**。早期 discovery / message lifecycle 研究证据保留在独立文档中，不再把历史实验 TODO 当作当前生产 TODO。

## 1. Current production baseline / 当前生产基线

- Default branch / 默认分支：`master`
- Current source/workflow implementation commit / 本轮源码与工作流修复提交：`dce1e34a685286e9c3e0b8513710e700f551d316`
- Current formal release / 当前正式版本：`bot-v1.1.2380`
- Release target commit：`839bb1a4ccb2fbf42a4fc3b6fbebdeff0fb3fcae`
- x64 package SHA-256：`b810c4cd5f335c9b6a34d24422e358c0ad03c33d87ef741f7c82083c06b401a9`
- 当前正式版的 Windows x64 release build、API control plane CI 均通过；当前发布信息以 GitHub Release `bot-v1.1.2380` 为准。

## 2. Completed / 已完成

### 2.1 Chat event ingestion / 聊天事件接入
- WebSocket / CDP business session discovery 已完成；
- 买家文本、订单卡片、图片消息进入统一处理链；
- 非买家会话隔离已完成；
- 重复 CDP 页面 exact payload 已增加长窗去重，避免恢复窗口重复入站。

### 2.2 Coalescing / Generation
- 买家 burst coalescing 已实现；
- 前置规则等待有上限；
- generation cancellation / fail-open 已实现；
- 正常文本 AI 总预算仍为 50 秒；
- PR #208 增加 ThreadPool starvation guard；
- PR #209 增加 dedicated background thread absolute-age watchdog；
- PR #220 将 watchdog 从“采样到 `Generating` 才登记”升级为“从 `BuyerActionAccepted` 起登记活动 generation”；
- 55 秒绝对期限覆盖 `Observed/Coalescing/Processing/Generating/Ready/Sending/Waiting`，终态才移除；
- watch registry 不再依赖 64 条 `RecentEvents` 诊断环，因此不会因高密度事件淘汰 accepted event 而失效。

### 2.3 Smart Reply / Knowledge
- Semantic Embedding 前台 true async；
- embedding 子 deadline 约 2.2 秒；
- Knowledge V2 FactKey 已细化；
- 人工答案学习与异常诊断 JSON 解析已切换为结构化恢复器；
- Knowledge V2 迟到答案在进入 Ready 前有 generation freshness / age barrier。

### 2.4 Order events / 订单事件
- OrderEventHub 跨进程原子持久化完成；
- order action durable ledger 完成；
- 固定回复按 segment / 业务动作幂等；
- 必要订单字段 enrichment 有界执行；
- 订单/CDP 入站重复 payload 有精确去重保护。

### 2.5 Reliable send / 可靠发送
- Bot-owned exact draft 校验；
- CDP / 安全 HWND / UIA 分层发送；
- fallback 前重新验证 seller/buyer/current conversation 和 owned draft；
- verified submission / seller echo / delivery verification 作为成功证据；
- 已提交等待回显时禁止盲目 resend；
- stale/cancelled Bot-owned draft 使用精确安全清理；
- 平台“服务态度提醒”走安全处理，不把“继续发送”作为普通自动 fallback；
- PR #220 给 CDP `_executeGate` 增加 1.5 秒排队上限；
- `EnsureActiveBuyerForSendAsync` 保留最多 22 次快速确认，但增加 9 秒总 wall-clock deadline。

### 2.6 Vision / OCR
- 本地 OCR worker/model 已从 Windows release 移除；
- 服务端 `/api/runtime/v1/ocr` 已实现；
- 客户端使用本店 `ShopControlPlaneConnectionStore` URL/token 自动解析 OCR endpoint；
- 图片续问允许 15 秒来源 clock skew；
- 图片派生 lease 保留原 `BuyerSessionAgent`、`SessionGeneration`、continuation context 和 cancellation token。

## 3. Remaining runtime work / 当前剩余运行时工作

### P0 — `bot-v1.1.2243` message mirror confirmation / 消息镜像确认

当前需要一份更新到正式版后的完整运行日志，重点确认：
- Bot Web 消息区完成首次同步后，等待 3～5 秒仍持续显示当前买家的历史消息，不再短暂闪现后变成“暂无聊天消息”；
- 切换买家后，消息列表与右侧会话始终对应同一个 buyer/session，不出现旧会话覆盖新会话。

### P1 — `bot-v1.1.2243` runtime verification / 新版真实日志验证

下一份完整日志重点检查：
- 55 秒 absolute-age deadline 是否从 buyer action 起稳定生效；
- 是否仍出现数分钟迟到 generation；
- `Ready/Sending/Waiting` 是否仍受同一 deadline 约束；
- CDP execute gate 拥塞是否在约 1.5 秒内 fail-fast；
- active buyer confirmation 异常是否在约 9 秒总预算内结束；
- 图片续问是否保留 generation cancellation 且不因轻微时钟逆序误判过期；
- 发送成功是否继续具有可信 submission/卖家 echo/delivery verification；
- OCR 是否使用 `shop-control-plane`；
- CDP 页面通道是否持续单调增长。

### P2 — CDP lifecycle observation / 生命周期观察

当前没有足够证据证明 page-channel 持续泄漏。保持观察；只有 `1.1.2243` 长时日志出现持续增长、旧 target/session 不释放时才继续改生命周期代码。

### P3 — Merged code awaiting real-client acceptance / 已合并代码待实机验收

以下改造已于 2026-11-19 合并到 master，并包含在正式版 bot-v1.1.2379（target c98fad57e21692435cde2b4c20a72ac67b490a73）中；后续 bot-v1.1.2380 继续包含本轮 ShopKey 商品目录修复：
- #426：CDP raw inbound 与重复会话转发的身份切换锁；需要真实 A→B 店铺切换确认旧会话不再接收业务事件。
- #429：私有接待配置按 ShopKey 隔离；需要多店铺及离线配置打开验证。
- #430：中性默认业务策略和无 scope fail-closed；需要旧配置升级与新店铺初始化验证。
- #431：AI 上游熔断与重启恢复；需要 502/503 故障注入和恢复验证。

以上 PR 已合并并进入正式版，不再属于 Draft PR。真实客户端验收仍未完成；CI 和 Release 通过只证明代码与构建契约，不代表运行时验收完成。
## 4. Validation rule / 验证规则

任何“已修复”至少满足其一：

1. 生产代码 + CI/静态契约证明；或
2. 新版本真实运行日志证明。

对于运行时竞态、真实发送、平台 UI、CDP 生命周期等问题，CI 通过不等于线上已验证，必须继续用新正式版本日志复核。

## 5. Next execution / 下一步

1. 客户端更新到 `bot-v1.1.2380`，先验证消息镜像持续显示；
2. 做真实买家文本、图片、订单、长耗时 AI、连续多消息、A→B 店铺切换与平台提醒测试；
3. 保持长时运行以观察 CDP/page-channel 生命周期；
4. 对 #429/#430/#431 分别完成多店铺隔离、策略迁移和上游故障恢复验收；
5. 导出完整日志；已合并、已发布不等于实机通过，不得把仍需运行验收的模块标记完成。

## 6. 2026-11-20 source-level audit / 源码审计

### 已修复并发布
- 商品目录刷新统一使用稳定 ShopKey 作为锁、缓存写入和定时任务键；通过 FindConnectedQnByShopKey 查找权威在线 CDP 会话，并在网络请求结束后再次校验 SessionId，防止旧会话结果覆盖当前缓存。
- 商品目录后台刷新使用 storage-only shop scope，不切换用户当前千牛工作台；设置页不再额外创建第二个按昵称运行的定时器。
- 设置页商品专属回复规则属于可选功能；其初始化异常已被隔离，不会阻断核心店铺设置页。新增回归测试保护该行为。
- 以上商品目录修复由 commit 839bb1a4ccb2fbf42a4fc3b6fbebdeff0fb3fcae 发布到正式版 bot-v1.1.2380。

### 已修复并进入 master 的发布验收逻辑
- GPTAuto Reconcile 按实际工作流路径/名称发现 Publish Bot auto-update release，不再错误假定工作流必须名为 Release。
- 手动发布必须携带精确 source_sha，只选择同一 SHA 的成功 Windows x64 构建，避免误发其他 master 提交的旧构建产物。
- 发布运行通过 workflow ID 和包含源 SHA 的 display_title 关联，避免把 workflow_run 自身的默认分支 SHA 误当成上游 Windows 构建 SHA。
- 相关实现与静态回归测试由 commit dce1e34a685286e9c3e0b8513710e700f551d316 进入 master；尚未通过一次新的 GPTAuto Reconcile 实际 dispatch 做端到端运行验收。

### 自动化验证
- Fast Gate：1621 passed，16 skipped。
- API control plane CI：Fast Gate、Windows Static Gate、Full Gate / Build Package 全部通过。
- Windows x64 release build：通过。
- GPTAuto Observer 与 GPTAuto Executor：通过。
- 正式发布 bot-v1.1.2380：target 839bb1a4ccb2fbf42a4fc3b6fbebdeff0fb3fcae，x64 包 SHA-256 为 b810c4cd5f335c9b6a34d24422e358c0ad03c33d87ef741f7c82083c06b401a9。

### 仍需真实 Windows/千牛验收
- 商品目录刷新需要在真实在线店铺确认：缓存、完整目录/部分目录合并、手动刷新、断线重连后恢复，以及多店切换不串店。
- #426/#429/#430/#431 的真实运行验收继续有效；正式发布和 CI 通过不能代替真实客户端证据。
