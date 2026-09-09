# 自动更新现场验证 / Auto-update Field Validation

日期 / Date: 2026-09-09

本提交仅用于触发一轮新的正式 Windows x64 构建与稳定版自动发布，以现场验证已经运行在 1.1.1355 的客户端能否在无人为安装干预的情况下完成下一版本的自动更新、重启和版本确认。

This commit intentionally changes documentation only. Its purpose is to trigger a fresh verified Windows x64 build and stable release so the updater path from 1.1.1355 can be validated end-to-end without mixing the test with unrelated product changes.

验收条件 / Acceptance criteria:

1. 服务端发布新的 stable 版本并向客户端推送版本事件。
2. 1.1.1355 客户端自动发现新版本。
3. 客户端自动下载、校验 SHA-256、执行安全交接并重启。
4. 重启后的“关于与更新”页面显示新版本号。
5. 不需要用户手工点击“立即安装并重启”或运行救援更新脚本。
