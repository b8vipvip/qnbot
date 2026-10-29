# 生产更新与部署手册

本文记录本项目当前固定的更新方式，后续新会话或换人维护时优先按本文执行，不再依赖聊天记录。

## 当前生产环境约定

### Ubuntu / 宝塔 API 控制面

- GitHub 仓库：`$REPO_CLONE_URL`
- Git 分支：`master`
- Git 仓库目录：`/opt/qnbot`
- API 服务目录：`/opt/qnbot/services/api-control-plane`
- 旧 ZIP 部署目录兼容：`/opt/qianniu-api-control-plane`
- 生产域名：`https://<CONTROL_PLANE_HOST>`
- 宝塔反向代理目标：`http://127.0.0.1:18081`
- SSL 与反向代理由宝塔维护，更新脚本不会修改 Nginx、域名或 SSL 配置。
- Compose 文件：`docker-compose.bt.yml`
- 容器名：`qianniu-api-control-plane`
- 容器启动入口：`python bootstrap.py`
- 永久服务数据：`services/api-control-plane/data/`
- 服务密钥和环境配置：`services/api-control-plane/.env`

`.env` 与 `data/` 已加入服务目录 `.gitignore`，不得提交到 GitHub。

## Ubuntu：第一次切换到 Git 仓库自动更新

服务器已经配置 GitHub SSH 时执行：

```bash
cd /opt

# 已存在 /opt/qnbot/.git 时跳过 clone。
[ -d /opt/qnbot/.git ] || \
  git clone $REPO_CLONE_URL /opt/qnbot

cd /opt/qnbot
sudo bash scripts/update-api-control-plane.sh
```

更新脚本会自动：

1. 使用 GitHub SSH 拉取 `master` 最新代码；
2. 拒绝覆盖服务器上未提交的已跟踪代码修改；
3. 兼容旧的 `/opt/qianniu-api-control-plane` ZIP 部署；
4. 保留并迁移原 `.env` 和 `data/`；
5. 在停止旧服务前先构建新 Docker 镜像，尽量减少停机时间；
6. 停止旧容器后对 `.env` 和整个 `data/` 做冷备份；
7. 使用 `docker-compose.bt.yml` 重建并启动服务；
8. 验证容器实际运行 `bootstrap.py`；
9. 验证本机 `http://127.0.0.1:18081/healthz`；
10. 验证公网 `https://<CONTROL_PLANE_HOST>/healthz`；
11. 本机启动失败时自动尝试回滚旧代码和冷备份数据。

备份默认保存在：

```text
/opt/qnbot-backups/YYYYMMDD-HHMMSS/
```

其中包含：

```text
.env
data.tar.gz
old-git-commit.txt
new-git-commit.txt
```

## Ubuntu：以后每次更新

只需要：

```bash
cd /opt/qnbot
sudo bash scripts/update-api-control-plane.sh
```

查看当前版本：

```bash
cd /opt/qnbot
git rev-parse HEAD
```

查看服务状态：

```bash
cd /opt/qnbot/services/api-control-plane
docker compose -f docker-compose.bt.yml ps
```

查看日志：

```bash
docker logs --tail 200 -f qianniu-api-control-plane
```

手工健康检查：

```bash
curl -fsS http://127.0.0.1:18081/healthz
curl -fsS https://<CONTROL_PLANE_HOST>/healthz
```

## Ubuntu：自定义目录或域名验证

脚本支持通过环境变量覆盖默认值：

```bash
sudo env \
  REPO_DIR=/opt/qnbot \
  LEGACY_DIR=/opt/qianniu-api-control-plane \
  BRANCH=master \
  VERIFY_URL=https://<CONTROL_PLANE_HOST>/healthz \
  bash /opt/qnbot/scripts/update-api-control-plane.sh
```

不要在生产 `.env` 中随意更换 `API_KEY_ENCRYPTION_KEY`。更换后，数据库中已经加密保存的上游 ApiKey、企业微信 Secret、Token 等将无法正常解密。

---

# Windows Bot 更新

## 固定数据原则

新版 Bot 的永久用户数据位于：

```text
%LocalAppData%\QianniuAiBot\data
```

程序升级不能删除该目录。

GitHub Actions 下载的是完整 x64 运行包，应更新整个运行包，不要只替换 `Bot.exe`。

仓库提供：

```text
scripts/update-bot.ps1
```

该脚本会：

1. 优先自动识别当前正在运行的 `Bot.exe` 安装目录；
2. 自动停止 Bot；
3. 备份旧程序目录；
4. 备份 `%LocalAppData%\QianniuAiBot\data`；
5. 解压并验证新包包含 `Bin\Bot.exe`；
6. 兼容旧版本程序目录中的 `data/`，供新版首次启动迁移；
7. 替换完整运行包；
8. 启动新 Bot 并确认进程保持运行；
9. 启动失败时自动恢复旧程序和永久数据备份。

脚本会拒绝把运行包覆盖到包含 `.git` 的源码仓库，防止误删 `C:\qnbot` 源码目录。

## Windows：标准更新命令

假设：

- 源码仓库：`C:\qnbot`
- 下载包位于：`C:\Users\codex\Downloads`
- Bot 当前处于运行状态，脚本可自动识别实际运行目录。

先更新本地脚本：

```powershell
cd C:\qnbot
git checkout master
git pull --ff-only origin master
```

然后执行完整包更新：

```powershell
cd C:\Users\codex\Downloads
Set-ExecutionPolicy -Scope Process Bypass -Force
& C:\qnbot\scripts\update-bot.ps1 `
  -PackagePath .\qianniu-bot-pr16-final-93516b8-x64.zip
```

PowerShell 中也可以写成一行：

```powershell
cd C:\Users\codex\Downloads; Set-ExecutionPolicy -Scope Process Bypass -Force; & C:\qnbot\scripts\update-bot.ps1 -PackagePath .\qianniu-bot-pr16-final-93516b8-x64.zip
```

Bot 没有运行、无法自动识别安装目录时，明确指定真正的运行目录：

```powershell
& C:\qnbot\scripts\update-bot.ps1 `
  -PackagePath C:\Users\codex\Downloads\qianniu-bot-pr16-final-93516b8-x64.zip `
  -InstallDir C:\QianniuAiBot
```

以后 ZIP 文件名变化时，也可以不传 `-PackagePath`。脚本会从当前目录和 `Downloads` 中自动选择最新的 `qianniu-bot* x64*.zip`：

```powershell
cd C:\Users\codex\Downloads
& C:\qnbot\scripts\update-bot.ps1
```

Windows 更新备份默认保存在当前用户目录：

```text
%LocalAppData%\QianniuAiBotUpdater\backups\YYYYMMDD-HHMMSS\
```

更新完成后至少实测：

1. API 服务连接正常；
2. 知识库和设置仍存在；
3. `优化问答` 按钮可见；
4. 买家下单固定回复可以真实发送；
5. 买家连续秒发时旧 AI 流会取消；
6. 新答案只在完整生成后发送；
7. `日志与调试` 可正常查看。
