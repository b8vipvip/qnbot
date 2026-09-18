# GPTAuto v0.2 — qnbot

## 中文（默认）

本仓库内嵌 GPTAuto v0.2，GitHub 工程任务默认采用**最终目标驱动**协议。用户无需每次额外声明“使用 GPTAuto”，只需说明最终目标。

执行语义：

    GOAL → PLAN / DoD → EXECUTE selected Dynamic Gates → VERIFY DoD → DONE

可选 Gate：INSPECT、IMPLEMENT、COMMIT、PR、PR_CI、MERGE、MAIN_CI、RELEASE、DEPLOY、RUNTIME_VERIFY。只有当前目标选中的 required Gate 才能阻止 DONE。

默认分支：`master`。项目原有业务 CI / Build / Release 工作流继续作为可选择的执行与验收能力；旧的通用 Actions Governor / Policy Check / Recovery / Housekeeping 基础治理不属于 GPTAuto v0.2 本仓库集成，已移除（若此前存在）。

ChatGPT/Work 应读取本文件与 `.github/gptauto/`：先根据用户最终目标、仓库规则和风险生成 DoD 与 Dynamic Gates，再持续执行和验证。Commit、PR、CI、Merge、Release 是否为终态或必要步骤，由当前 DoD 决定，而不是固定流水线决定。

只有权限/凭据缺失、重大产品决策歧义、未授权高风险破坏操作、修复预算耗尽或不可修复平台条件才进入 BLOCKED 并询问用户。

## English

This repository embeds GPTAuto v0.2. GitHub engineering work is goal-bound by default. Users only need to state the desired final outcome; they do not need to mention GPTAuto on every task.

Lifecycle: `GOAL → PLAN/DoD → EXECUTE selected Dynamic Gates → VERIFY DoD → DONE`. Existing project-specific CI/build/release workflows remain available as task-selected capabilities. Legacy generic Actions Governor/Policy/Recovery/Housekeeping governance is not part of this integration and is removed when previously installed.
