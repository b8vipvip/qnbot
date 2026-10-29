# Knowledge Center V2 / Local Knowledge Engine V2

最后更新：2026-08-24

## 目标

Knowledge Center V2 用于替换旧的“问答列表 + 全量相似度扫描 + 单独可靠度策略 + Memory Engine v1”运行时知识决策方式。

本次重构的核心目标：

- 保留现有知识数据，但不保留旧检索架构；
- 将知识从“问题文本”升级为结构化业务事实；
- 买家消息不得触发定时全量索引重建；
- 热查询目标 `P95 <= 50ms`；
- 明确问题优先使用当前消息，Working Memory 只补全缺失主体；
- 只有相同 `Subject + Predicate` 的事实才参与冲突判断；
- 学习数据先进入候选区，不自动获得生产直答资格；
- V2 无法证明本地直答时，继续兼容现有 Smart Reply / AI 链路。

## 数据模型

V2 使用 `KnowledgeV2Record` 作为知识基本单元：

```text
Id
Type
Title
Intent
Subject
Predicate
Entities[]
Aliases[]
Answer
ShortAnswer
Conditions[]
Exclusions[]
RequiredContext[]
ProductIds[]
RiskLevel
SourceType / SourceId
Authority
Confidence
UseCount
AcceptedCount
CorrectionCount
WithdrawCount
Enabled
Status
CreatedAt / UpdatedAt / LastVerifiedAt
```

典型示例：

```text
Title: 电视会员是什么
Intent: capability
Subject: 示例音频应用/电视/会员
Predicate: membership_type
Entities: 示例音频应用, 电视, 会员
Aliases: 这个是电视会员吗, TV版会员吗
Answer: ...
```

“电视会员是什么”和“电视会员在哪里买”即使包含相同实体，也因为 `Predicate` 分别为 `membership_type` 与 `purchase_channel`，不会进入同一个事实冲突池。

## 持久化

每个店铺拥有独立 SQLite 数据库：

```text
<shop knowledge root>/knowledge-center-v2.db
```

表：

- `KnowledgeV2RecordRow`
- `KnowledgeV2MetaRow`

旧问答知识在首次运行时自动迁移。迁移不会要求用户重新录入现有知识。

V2 编辑的正式知识会同步镜像回旧知识列表，供过渡期兼容链路继续读取；生产本地直答以 V2 Repository 和 V2 Snapshot 为准。

学习候选即使在 V2 内处于 Enabled，也会在旧知识镜像中强制 `Enabled=false`，避免 Smart Reply / AI 兼容回退链路提前使用尚未批准的候选答案。批准候选后才会转换为正式知识并重新启用兼容镜像。

## 索引

运行时 Snapshot 常驻内存，包含：

- Exact Index
- Intent Index
- Predicate Index
- Entity Index
- 中文 2-gram Index

检索过程：

```text
消息理解
  -> Exact / Predicate / Intent / Entity / Ngram 召回
  -> 候选集合（限制规模）
  -> 结构化精排
  -> 冲突 / 风险 / 可信度判断
  -> Local Direct 或兼容 AI 链路
```

与 Memory Engine v1 不同，查询不再对全部知识逐条执行策略文件读取与全表 Score。

Snapshot 没有“10 分钟过期后自动全量重建”的时间过期规则。普通启用知识的新增/编辑通过 `KnowledgeEngineV2.Service.Incremental.cs` 对当前店铺 Snapshot 做原子增量更新：先复制当前完整 Snapshot、只删除/增加该知识对应的索引 posting，再一次性替换 Snapshot 引用，因此并发买家查询只会看到更新前或更新后的完整状态。删除、停用、完整包替换和重新迁移等结构性操作才使 Snapshot 失效并在后台/下次预热时重建。

## Working Memory

Working Memory 只作为指代补全器：

```text
当前明确消息 > 当前消息实体 > Working Memory > 更早历史
```

只有“这个呢”“能用吗”“怎么弄”等缺少完整主体的消息才允许使用最近明确的 Subject / Predicate / Intent / Entities 补全。

如果买家当前消息已经明确包含新主体，例如“手机会员多少钱”，旧会话中的“电视”上下文不得覆盖当前消息。

## 冲突定义

冲突 FactKey：

```text
Compact(Subject) + "|" + Predicate
```

只有 FactKey 相同且答案存在实质差异时才标记冲突。

例如：

```text
电视会员 | feature_support | 支持K歌
电视会员 | feature_support | 不支持K歌
```

属于冲突。

下面两条不属于冲突：

```text
电视会员 | membership_type
电视会员 | purchase_channel
```

学习候选不参与生产事实冲突统计，避免未批准建议把正常 active 知识错误标成冲突。

## 学习候选

旧人工回复/会话学习事件仍可作为数据来源，但 V2 学习桥会把新学习内容转换为：

```text
Type = learning_candidate
Status = candidate
```

候选知识默认不能直接发送，需要在 Knowledge Center V2 的“学习”页面批准后才进入 active 状态。V2 本地直答层和旧知识兼容镜像都会同时阻止未批准候选进入生产回复。

如果学习内容是在修正已存在知识，V2 会生成独立候选修正记录，避免自动覆盖生产事实。

## 运行模式

设置页提供：

- `production`
- `shadow`

Production：满足结构化高置信条件的知识可以本地直答。

Shadow：V2 仍执行解析、召回、精排和决策，但不直接发送，继续走兼容回复链路，适合上线前比较。

店铺设置键：

```text
knowledge.engine_v2.enabled
knowledge.engine_v2.mode
knowledge.engine_v2.direct_threshold
knowledge.engine_v2.min_confidence
```

设置通过店铺隔离的 `ShopScopedSettingsStore` 保存，并在运行时做短期内存缓存；不在每个候选知识评分时重复读取和解密配置文件。

## 运行时接入

`KnowledgeEngineV2RuntimeBridge` 安装在 `BuyerMessageBurstCoordinator` 外层：

```text
BuyerMessageBurstCoordinator
  -> Knowledge Engine V2
      -> 高置信本地答案：直接进入安全发送链路
      -> 无法证明：兼容 Smart Reply / AI 链路
```

V2 启动后会停止 Memory Engine v1 的运行时 Timer，并剥离已经挂载的 v1 wrapper，避免 V1/V2 重复套娃。

本地答案发送仍必须经过：

- 自动回复开关；
- 当前任务有效性检查；
- 并发相关性门控；
- 目标买家确认；
- `SendTextWithRetryAsync`；
- Bot 消息后缀；
- 发送回显与失败诊断。

因此知识检索重构不会绕过既有发送安全边界。

## Knowledge Center V2 UI

### 客户端使用帮助

客户端在 `设置 → 知识库` 以及 Knowledge Center V2 顶部都提供 `使用帮助`。帮助窗口为只读内容，覆盖首次配置、字段填写、回复工作原理、学习/质量/修订/治理、备份和常见排查，不会修改知识或店铺设置。完整文字版见 `docs/KNOWLEDGE_CENTER_V2_USER_HELP.md`。

旧知识中心显示层由 V2 Shell 替换，新一级导航：

```text
知识
商品知识
流程
学习
冲突
测试台
导入导出
设置
```

### 知识 / 商品知识 / 流程 / 学习

使用结构化三栏管理方式，编辑字段包含：

- 类型
- Intent
- Subject
- Predicate
- Entities
- Aliases
- 标准答案 / 简短答案
- 适用条件 / 排除条件 / 必要上下文
- 商品 ID
- 风险等级
- 可信度 / 权威度
- Enabled / Status

### 冲突

按 FactKey 展示冲突组，可：

- 保留所选答案并停用其他冲突记录；
- 将其余记录转为学习候选。

### 测试台

显示独立耗时：

```text
ParseMs
RecallMs
RankMs
DecisionMs
TotalMs
```

同时展示：

- Intent
- Subject
- Predicate
- Entities
- Working Memory 是否参与
- 候选数量
- Top Matches
- FactKey
- 是否本地直答
- 拒绝原因

支持 30 次热查询性能测试并计算 P50 / P95 / MAX。单次测试与性能循环都在后台 Task 中运行，不阻塞 WPF UI 线程。

### 导入导出

V2 完整包包含：

- 全部结构化知识；
- V2 运行设置；
- Schema Version。

导入完整包前会自动在 V2 数据目录下创建 JSON 备份。

也支持只导出结构化知识，以及从当前旧知识重新迁移。

## 性能目标

当前阶段目标：

```text
约 800 条知识：热查询 P95 <= 50ms
普通明确问题本地决策：尽量 <= 30ms
普通启用知识新增/编辑：增量 Snapshot 更新时间 <= 100ms
买家消息：不得承担定时全量索引重建
UI 测试：不得在 WPF 主线程同步执行完整检索/性能循环
```

测试台的单次检索和 30 次性能测试均在后台 Task 中执行。

## 迁移与退场策略

1. 首次启动创建 `knowledge-center-v2.db`。
2. 将旧问答、策略可靠度和人工证据迁移为 V2 Record。
3. 后台建立 Snapshot。
4. Memory Engine v1 运行时停用。
5. Production 模式下 V2 高置信本地直答。
6. 其余消息暂时继续兼容 Smart Reply / AI。
7. 新人工学习进入 V2 candidate，不自动污染 active 知识，也不会被旧兼容知识链提前使用。
8. 普通知识编辑使用原子增量 Snapshot 更新；结构性批量操作才重建。
9. 生产数据稳定后，再逐步删除旧检索实现；旧格式只保留导入兼容。

原则：**保数据，不保旧运行时架构。**

## 关键文件

```text
src/Bot/Knowledge/KnowledgeEngineV2.Models.cs
src/Bot/Knowledge/KnowledgeEngineV2.Repository.cs
src/Bot/Knowledge/KnowledgeEngineV2.Semantics.cs
src/Bot/Knowledge/KnowledgeEngineV2.Service.Index.cs
src/Bot/Knowledge/KnowledgeEngineV2.Service.Incremental.cs
src/Bot/Knowledge/KnowledgeEngineV2.Service.Public.cs
src/Bot/Knowledge/KnowledgeCenterV2Ui.cs
src/Bot/Knowledge/KnowledgeCenterV2RecordsPage.cs
src/Bot/Knowledge/KnowledgeCenterV2OperationsPages.cs
src/Bot/ChromeNs/KnowledgeEngineV2RuntimeBridge.cs
src/Bot/ChromeNs/KnowledgeEngineV2LearningBridge.cs
```

回归测试：

```text
tests/test_knowledge_center_v2_static.py
```
