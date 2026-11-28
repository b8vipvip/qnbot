# Knowledge Engine V2-only 发布与数据完整性检查

本清单用于验证客户端只运行 Knowledge Engine V2。它不再指导从 V1 问答库迁移数据。

## 发布前静态检查

- [ ] 不存在 `KnowledgeBaseEntry`、`KnowledgeBaseJson`、`BotFeatureStore.GetKnowledgeBase` 或 `SaveKnowledgeBase`。
- [ ] 不存在 V1 Memory Engine、V1 embedding retriever、V1 contextual reply、旧版管理页、旧版策略编辑器和旧版云同步协议。
- [ ] `Bot.csproj`、`Directory.Build.props`、`Directory.Build.targets` 不再引用已删除的 V1 文件。
- [ ] `KnowledgeEngineV2Repository` 不包含 V1 自动迁移、双写镜像或旧库回退。
- [ ] 历史扫描、智能导入和自动学习只解析并写入 `KnowledgeV2Record`。

## 店铺数据边界

- [ ] 每次写入都解析稳定 ShopKey，并核对 SellerNick 与当前千牛/CDP 会话。
- [ ] 两个店铺即使昵称相同，也使用独立 V2 SQLite、索引、Working Memory、审计、备份和云同步状态。
- [ ] 缺少 ShopKey、身份不匹配或云端凭据失效时 fail closed，不猜测店铺归属。
- [ ] 新安装没有 V2 数据库时只创建空 V2 数据库；不会从旧 JSON 自动导入。
- [ ] 数据恢复只接受 Knowledge V2 完整包或本店 V2 云端数据。

## 检索与自动学习

- [ ] 买家文本回复先经过 V2 检索与风险/条件判断；高风险、订单上下文不足或冲突知识不会被本地直答。
- [ ] 图片/OCR 只作为证据补充，不覆盖买家原始问题的意图和会话记忆。
- [ ] 完整语料自动新增必须具备真实买家问题消息ID和真人客服回复消息ID。
- [ ] Bot-only、来源不明、缺少原始消息ID、高风险、历史覆盖不完整或指纹漂移均不能自动发布。
- [ ] 通过全部证据门禁的新增/修订直接写入 active、刷新 V2 索引并保留可回滚修订。
- [ ] 断线、重启和并发更新不会丢失 V2 记录、审计或索引最终一致性。

## CI 与构建

- [ ] Python 静态测试全部通过。
- [ ] Windows 生命周期与 ShopKey 隔离测试全部通过。
- [ ] Bot x64 Release 构建成功。
- [ ] 安装包包含 V2 SQLite、V2 UI、V2 云同步和必要诊断组件，不包含 V1 知识文件。
