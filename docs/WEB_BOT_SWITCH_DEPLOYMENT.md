# Web Bot 总开关部署与更新

## 功能边界

Web 设置页中的“启用 Bot”是店铺级总开关：

- 开启：Windows 当前店铺的 `Params.Robot.CanUseRobot=true`；
- 关闭：Windows 当前店铺的 `Params.Robot.CanUseRobot=false`，Bot 不再参与该店铺消息处理；
- “智能自动回复”仍是独立的下一级开关；
- Windows 每 2.5 秒回传实际状态，Web 显示“启用 / 停用 / 待下发”；
- 一枚客户端令牌只允许绑定一个 ShopKey。

升级兼容：服务端首次收到新版 Windows 同步时，如果 Web 尚未保存过该开关，会采用 Windows 当前值作为初始值，不会因升级自动改变原开关状态。

## 推荐更新顺序

1. 合并并确认 `master` 的 CI 全部通过；
2. 更新服务端控制面；
3. 更新 Windows Bot；
4. 浏览器刷新 Web 页面并验证同步。

服务端新版兼容旧 Windows 客户端，因此应先更新服务端。

## 一、更新服务端

默认 Git 仓库目录：

```text
/opt/qnbot
```

在服务器终端执行：

```bash
cd /opt/qnbot
git status --short
sudo BRANCH=master bash scripts/update-api-control-plane.sh
```

更新脚本会：

- 拉取 `master` 最新提交；
- 检查服务器是否存在未提交的已跟踪修改；
- 构建新 Docker 镜像，此时旧服务继续运行；
- 停止旧容器并备份 `.env` 和 `data`；
- 启动新容器；
- 检查本机 `/healthz`；
- 检查公网域名、反向代理和 SSL；
- 启动或健康检查失败时自动回滚。

服务端备份默认位于：

```text
/opt/qnbot-backups/<时间戳>/
```

部署 PR 分支进行临时验收时，可把 `BRANCH=master` 改为：

```bash
BRANCH=feat/multi-shop-data-sync-isolation
```

生产环境建议合并后再使用 `master`。

## 二、更新 Windows Bot

从通过检查的 `Windows x64 release build` 下载完整 x64 ZIP，然后放到“下载”目录。

如果本机源码目录是：

```text
C:\qnbot
```

以管理员身份打开 PowerShell：

```powershell
cd C:\qnbot
Set-ExecutionPolicy -Scope Process Bypass
.\scripts\update-bot.ps1 -PackagePath "$env:USERPROFILE\Downloads\<完整x64包名>.zip"
```

如果 Bot 正在运行，脚本会自动识别安装目录。也可明确指定：

```powershell
.\scripts\update-bot.ps1 `
  -PackagePath "$env:USERPROFILE\Downloads\<完整x64包名>.zip" `
  -InstallDir "C:\QianniuAiBot"
```

脚本会：

- 停止 Bot 及安装目录中的相关进程；
- 备份旧程序；
- 解压并校验新包必须包含 `Bin\Bot.exe`；
- 替换程序文件；
- 保留 `%LocalAppData%\QianniuAiBot` 店铺数据；
- 启动并检查新 Bot；
- 新 Bot 无法稳定启动时自动恢复旧程序。

程序更新备份默认位于：

```text
%LocalAppData%\QianniuAiBotUpdater\backups\<时间戳>\
```

## 三、更新 Web

Web 页面、JavaScript 和接口均包含在服务端 Docker 镜像中，不需要单独上传 Web 文件。

服务端更新完成后打开：

```text
https://<CONTROL_PLANE_HOST>/bot/
```

浏览器执行一次强制刷新：

```text
Ctrl + F5
```

移动浏览器如果仍显示旧页面，可关闭该标签页后重新打开；本次页面脚本带版本查询参数，正常情况下会自动绕过旧缓存。

## 四、验证

1. Windows Bot 已启动并登录目标店铺；
2. 使用该店铺客户端令牌登录 Web；
3. 设置页应显示“启用 Bot”；
4. 关闭开关并保存；
5. Web 先显示“正在等待 Windows Bot 应用”，随后显示“Windows 当前实际状态：已停用”；
6. 状态页“Bot 总开关”显示“停用”；
7. Windows 当前店铺停止 Bot 消息处理，其他 ShopKey 不受影响；
8. 重新开启后，Web 和 Windows 恢复为启用；
9. 如果把同一令牌误配给另一 ShopKey，服务端应拒绝同步并提示令牌已绑定其他 ShopKey。

## 五、故障定位

服务端：

```bash
docker logs --tail 200 qianniu-api-control-plane
curl -fsS http://127.0.0.1:18081/healthz
```

Windows 店铺日志：

```text
%LocalAppData%\QianniuAiBot\shops\<ShopKey>\logs\runtime.txt
```

重点搜索：

```text
Web端 Bot 总开关同步
Web端 Bot 总开关已应用
该客户端令牌已绑定其他 ShopKey
```
