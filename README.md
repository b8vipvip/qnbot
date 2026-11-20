# 千牛 AI 客服机器人

面向 Windows 千牛接待台的 AI 客服与人工接管系统。

当前项目由三部分组成：

1. **Windows 千牛 Bot**：接收会话消息、聚合连续消息、使用 Knowledge Center V2 / 本地知识引擎或 AI 生成答案，并通过 UI Automation 发送回复；
2. **Ubuntu API 控制面**：管理 AI 供应商、模型、测试、Bot 客户端令牌、路由记录与企业微信配置；
3. **企业微信双向人工接管**：转人工通知、加密回调、工单回复队列和精确买家发送。

> 千牛客户端升级可能改变 DOM、AutomationId 或窗口结构。涉及自动发送的功能应先在测试账号和测试会话中验证。

## 当前状态

- 连续短消息聚合、补充、纠错、催促和重复去重已接入生产链路；
- 文本、图片、订单等业务任务可独立处理，最终千牛输入/发送仍由安全发送链路串行协调；
- 发送前执行稳定检查、目标会话确认和重复答案保护；
- **Knowledge Center V2 已进入重构主线**：SQLite 结构化知识、Intent/Subject/Predicate、倒排索引、Working Memory 补全、冲突页、学习候选、异步测试台和完整包导入导出；
- Knowledge Memory Engine v1 作为旧运行时实现退场，V2 无法证明本地直答时暂时兼容现有 Smart Reply / AI 链路；
- 企业微信双向人工接管已接入；
- 企业微信参数已迁移到控制面网页配置；
- Secret、回调 Token 和 EncodingAESKey 经 Fernet 加密后保存到 SQLite；
- Ubuntu 控制面、企业微信测试消息和加密回调已完成实机验收；
- API control plane CI、Windows CI 和 Windows x64 release build 由 GitHub Actions 自动验证。

完整的架构、功能矩阵、里程碑和后续计划：

- [`docs/PROJECT_STATUS_AND_ARCHITECTURE.md`](docs/PROJECT_STATUS_AND_ARCHITECTURE.md)
- [`docs/KNOWLEDGE_CENTER_V2.md`](docs/KNOWLEDGE_CENTER_V2.md)

## Windows 只读验收诊断

可在仓库根目录执行以下短命令：

```powershell
powershell.exe -NoProfile -File scripts\Invoke-QnBotAcceptanceDiagnostics.ps1
```

脚本会生成 JSON 和 Markdown 报告，检查 Bot/千牛进程、`bench_im` renderer、本地 WebSocket 41010、loopback CDP/IMSDK 属性，并读取 Bot 每 30 秒生成的只读运行态快照。快照仅包含 HMAC 化的 ShopKey/会话引用、权威会话/standby 状态、身份代次及计数；只接受与当前 Bot PID 和启动时间匹配且不超过 120 秒的快照。报告默认保存在 `%LOCALAPPDATA%\QianniuAiBot\diagnostics\`。

它不发送消息、不修改配置、不停止进程、不上传文件，也不收集聊天正文、账号昵称、Cookie、令牌、目标 URL、原始命令行或原始日志。真实消息投递、切店和重连仍须在测试店铺验收。

详细验收范围见 [千牛自动化进度](docs/QIANNIU_CHAT_AUTOMATION_PROGRESS.md)。

## 总体架构

```text
千牛聊天页面
  → 注入脚本 / WebSocket / CDP-QN 事件
  → 卖家和买家识别
  → 消息聚合 / 并发业务任务协调
  → Knowledge Center V2
      → 结构化消息理解
      → Exact / Intent / Predicate / Entity / Ngram 索引
      → 高置信本地直答
      → 否则兼容 Smart Reply / AI
  → 重复答案与发送前稳定检查
  → QNRpa / FlaUI / UI Automation
  → 确认目标会话并发送给买家

Windows Bot
  ↔ HTTPS 人工回复任务队列
Ubuntu FastAPI 控制面
  ↔ 企业微信应用消息与加密回调
企业微信自建应用 / 个人微信插件入口
```

## Knowledge Center V2

V2 不再把知识仅保存为“问题 + 答案 + 相似度”。核心知识单元包含：

```text
Type
Intent
Subject
Predicate
Entities
Aliases
Answer
Conditions / Exclusions / RequiredContext
ProductIds
RiskLevel
Authority / Confidence
Learning / correction evidence
```

每个店铺使用独立 SQLite：

```text
<shop knowledge root>/knowledge-center-v2.db
<shop knowledge root>/knowledge-feedback-v2.db
<shop knowledge root>/knowledge-revision-v2.db
<shop knowledge root>/knowledge-governance-v2.db
```

运行时建立常驻内存倒排索引，买家查询不使用“定时过期后全量重建”方式。当前性能目标：约 800 条知识的热查询 `P95 <= 50ms`。

Knowledge Center V2 UI 一级导航：

```text
知识 / 商品知识 / 流程 / 学习 / 冲突 / 测试台 / 导入导出 / 设置
```

“测试台”会显示 Parse / Recall / Rank / Decision / Total 独立耗时，并支持 30 次热查询 P50/P95 测试。

顶部 `质量 / 修订 / 治理` 入口使用真实反馈形成质量闭环、人工复核修订和治理队列。治理窗口包含店铺隔离的操作历史以及可配置验证过期阈值；扫描本身只读，任何停用、应用或回滚都需要人工确认。

客户端可从 `设置 → 知识库 → 使用帮助` 或 Knowledge Center V2 顶部打开完整配置说明。详细使用方法、设计与迁移策略参见 [`docs/KNOWLEDGE_CENTER_V2_USER_HELP.md`](docs/KNOWLEDGE_CENTER_V2_USER_HELP.md)、[`docs/KNOWLEDGE_CENTER_V2.md`](docs/KNOWLEDGE_CENTER_V2.md) 和 [`docs/KNOWLEDGE_CENTER_V2_GOVERNANCE_AUDIT_SETTINGS.md`](docs/KNOWLEDGE_CENTER_V2_GOVERNANCE_AUDIT_SETTINGS.md)。

## 核心功能

### Windows 千牛 Bot

- 接收千牛聊天消息并识别卖家、买家和当前会话；
- 聚合一句话拆多条、补充信息、纠错和连续催促；
- 多 AI 供应商、多模型和失败切换；
- 文本与视觉模型；
- 自动回复、人工确认和规则控制；
- 店铺隔离的 Knowledge Center V2；
- 文本、HTML、图片资料的 AI 智能导入；
- 结构化知识搜索、编辑、商品绑定、冲突处理、学习候选和完整包导入导出；
- UIA/FlaUI 输入、发送和结果确认；
- 重复答案保护和真实发送成功后的知识学习；
- 企业微信人工回复任务领取和精确买家发送。

### Ubuntu API 控制面

- AI 供应商、Base URL、协议、密钥和模型管理；
- 文本与视觉模型探测；
- 普通、深度和计划任务测试；
- 实际路由、协议、耗时和结果记录；
- Windows Bot 客户端令牌管理；
- 人工回复任务绑定、短租约领取和完成回报；
- 企业微信网页配置、测试消息、工单和加密回调；
- Docker、Compose 和 HTTPS 部署。

### 企业微信人工接管

- 创建唯一工单号 `QN-XXXXXXXX`；
- 通知包含卖家、买家和同一轮买家消息；
- 图片、视频、语音、表情、文件和位置使用安全占位符；
- 手机号和疑似 API Key 在服务端脱敏；
- 回调执行 SHA1 签名校验和 AES-CBC 解密；
- 只接受授权成员、文本消息和有效工单；
- 只有创建工单的 Windows Bot 客户端可以领取回复；
- 只有千牛真实发送成功后才完成任务并学习知识。

人工回复格式：

```text
QN-XXXXXXXX 回复内容
```

## 主要目录

```text
src/Bot/                         Windows WPF Bot 主程序
src/Bot/ChromeNs/                千牛连接、AI、消息协调和发送链路
src/Bot/Knowledge/               Knowledge Center V2 / 知识导入与学习
src/Bot/Automation/              Windows 自动化
src/BotLib/                      通用基础库与 SQLite
src/DbEntity/                    数据实体
services/api-control-plane/      Ubuntu FastAPI 控制面
services/api-control-plane/static/ 控制面网页
services/api-control-plane/tests/  控制面与企业微信测试
tools/qn_discovery_lab/          千牛 UIA / IMSDK 研究工具
docs/                            架构、进度、验证与交接文档
.github/workflows/               GitHub Actions
```

## 关键代码

### Knowledge Center V2

```text
src/Bot/Knowledge/KnowledgeEngineV2.Models.cs
src/Bot/Knowledge/KnowledgeEngineV2.Repository.cs
src/Bot/Knowledge/KnowledgeEngineV2.Semantics.cs
src/Bot/Knowledge/KnowledgeEngineV2.Service.Index.cs
src/Bot/Knowledge/KnowledgeEngineV2.Service.Public.cs
src/Bot/Knowledge/KnowledgeEngineV2FeedbackService.cs
src/Bot/Knowledge/KnowledgeEngineV2RevisionService.cs
src/Bot/Knowledge/KnowledgeEngineV2GovernanceService.cs
src/Bot/Knowledge/KnowledgeEngineV2GovernanceAuditService.cs
src/Bot/Knowledge/KnowledgeCenterHelpWindow.cs
src/Bot/Knowledge/KnowledgeCenterV2Ui.cs
src/Bot/Knowledge/KnowledgeCenterV2RecordsPage.cs
src/Bot/Knowledge/KnowledgeCenterV2OperationsPages.cs
src/Bot/ChromeNs/KnowledgeEngineV2RuntimeBridge.cs
src/Bot/ChromeNs/KnowledgeEngineV2LearningBridge.cs
```

### Windows 消息与发送链路

```text
src/Bot/ChromeNs/MyWebSocketServer.cs
src/Bot/ChromeNs/CDPClient.cs
src/Bot/ChromeNs/QN.cs
src/Bot/ChromeNs/MyOpenAI.cs
src/Bot/ChromeNs/QNRpa.cs
src/Bot/ChromeNs/WeComAppBridgeClient.cs
src/Bot/ChromeNs/HandoffNotificationService.cs
```

### Ubuntu 控制面与企业微信

```text
services/api-control-plane/app.py
services/api-control-plane/bootstrap.py
services/api-control-plane/wecom_bridge.py
services/api-control-plane/wecom_crypto.py
services/api-control-plane/wecom_settings.py
services/api-control-plane/static/wecom.html
```

## 环境要求

### Windows Bot

- Windows 10/11；
- .NET Framework 4.8；
- 千牛 Windows 客户端；
- Windows UI Automation 可访问千牛无障碍树；
- 可用的 OpenAI 兼容 AI 接口；
- Ubuntu 控制面地址和 Bot 客户端令牌。

### Ubuntu 控制面

- Linux / Ubuntu；
- Docker 与 Docker Compose；
- HTTPS 域名和反向代理；
- 长期固定的 `API_KEY_ENCRYPTION_KEY`；
- 持久化的 `.env` 与 `data/` 目录。

企业微信 CorpID、Secret、AgentId、成员、回调 Token 和 EncodingAESKey 不从 `.env` 读取，应在控制面“企业微信”页面配置。

## 获取 Windows 构建

推荐从 GitHub Actions 下载完整运行包，而不是只替换单个 `Bot.exe`：

```text
GitHub → Actions → Windows x64 release build → 最新成功运行 → Artifacts
```

Windows 更新时必须保留现有用户数据目录和本地参数/知识数据。

## Ubuntu 更新原则

控制面部署目录通常为：

```text
/opt/qianniu-api-control-plane
```

生产更新必须：

1. 核对目标 Git SHA；
2. 备份 `.env`；
3. 停止控制面后冷备份整个 `data/`；
4. 更新代码时排除 `.env` 和 `data/`；
5. 重建容器；
6. 验证本机与公网 `/healthz`；
7. 验证容器运行 `python bootstrap.py`。

数据库加密主密钥丢失或变化会导致敏感配置无法解密，必须长期固定并纳入备份。

## GitHub Actions

主要工作流：

- `Windows CI`：Debug/x64 编译和产物验证；
- `Windows x64 release build`：测试、Release/x64 编译和完整运行包；
- `API control plane CI`：静态测试、控制面测试、Python/JavaScript 检查、Docker 构建、Compose 校验和 Ubuntu 部署包。

## 本地编译

在安装了 Visual Studio Build Tools、MSBuild 和 NuGet 的 Windows 环境中：

```powershell
nuget restore src\Bot.sln -NonInteractive
msbuild src\Bot.sln /m /t:Rebuild /p:Configuration=Debug /p:Platform=x64
```

## UIA / IMSDK 研究

生产消息接收仍以注入脚本、WebSocket 和 CDP/QN 事件为主。`tools/qn_discovery_lab/` 中的 UIA v2 生命周期、IMSDK 和 Frida 研究尚未替换生产接收链路。

研究文档：

- [`docs/QIANNIU_CHAT_AUTOMATION_PROGRESS.md`](docs/QIANNIU_CHAT_AUTOMATION_PROGRESS.md)
- [`docs/QIANNIU_MESSAGE_LIFECYCLE_VALIDATION.md`](docs/QIANNIU_MESSAGE_LIFECYCLE_VALIDATION.md)

后续推荐先以 Shadow Mode 接入 UIA v2，只记录和比对，不触发 AI 或自动发送。

## 使用提醒

1. 先使用测试店铺和测试会话验证自动回复；
2. Knowledge Engine V2 可先切到 `shadow` 比较命中结果，再切 `production`；
3. 自动发送前必须确认目标会话；
4. 退款、投诉、赔偿、订单隐私等高风险问题应优先人工处理；
5. 千牛升级后应重新验证消息接收、输入框定位和发送确认；
6. 生产升级前必须备份并保留所有用户数据。

## 免责声明

本项目仅供学习、研究和技术交流。使用者应自行确认符合淘宝/千牛平台规则、当地法律法规、用户隐私要求以及第三方 AI 服务条款。
