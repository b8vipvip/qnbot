#!/usr/bin/env bash
set -Eeuo pipefail

REPO_DIR="${REPO_DIR:-/opt/qnbot}"
SERVICE_NAME="qianniu-api-update-agent.service"
UNIT_PATH="/etc/systemd/system/$SERVICE_NAME"
AGENT_SCRIPT="$REPO_DIR/scripts/api-control-plane-update-agent.sh"
DATA_DIR="${DATA_DIR:-$REPO_DIR/services/api-control-plane/data}"
APP_UID="${CONTROL_PLANE_APP_UID:-10001}"
APP_GID="${CONTROL_PLANE_APP_GID:-10001}"
GITHUB_SSH_HOST="${GITHUB_SSH_HOST:-ssh.github.com}"
GITHUB_SSH_PORT="${GITHUB_SSH_PORT:-443}"
GIT_FETCH_RETRIES="${GIT_FETCH_RETRIES:-5}"

if [[ "$(id -u)" -ne 0 ]]; then
  echo "请使用 sudo/root 执行此安装脚本。" >&2
  exit 1
fi
for command_name in systemctl git ssh docker python3 flock timeout; do
  command -v "$command_name" >/dev/null 2>&1 || { echo "未检测到 $command_name" >&2; exit 1; }
done
[[ -d "$REPO_DIR/.git" ]] || { echo "$REPO_DIR 不是 Git 仓库" >&2; exit 1; }
[[ -f "$AGENT_SCRIPT" ]] || { echo "缺少 $AGENT_SCRIPT" >&2; exit 1; }
[[ -f "$REPO_DIR/scripts/update-api-control-plane.sh" ]] || { echo "缺少 update-api-control-plane.sh" >&2; exit 1; }

install -d -m 0770 -o "$APP_UID" -g "$APP_GID" "$DATA_DIR/server-update"
cat > "$UNIT_PATH" <<EOF
[Unit]
Description=Qianniu API Control Plane Web Update Agent
After=docker.service network-online.target
Wants=network-online.target
Requires=docker.service

[Service]
Type=simple
User=root
WorkingDirectory=$REPO_DIR
Environment=REPO_DIR=$REPO_DIR
Environment=DATA_DIR=$DATA_DIR
Environment=GITHUB_SSH_HOST=$GITHUB_SSH_HOST
Environment=GITHUB_SSH_PORT=$GITHUB_SSH_PORT
Environment=GIT_FETCH_RETRIES=$GIT_FETCH_RETRIES
ExecStart=/bin/bash $AGENT_SCRIPT
Restart=always
RestartSec=3
TimeoutStopSec=20

[Install]
WantedBy=multi-user.target
EOF

systemctl daemon-reload
systemctl enable --now "$SERVICE_NAME"

if ! systemctl is-active --quiet "$SERVICE_NAME"; then
  echo "更新代理 systemd 服务未进入 active 状态。" >&2
  systemctl --no-pager --full status "$SERVICE_NAME" || true
  if command -v journalctl >/dev/null 2>&1; then
    journalctl -u "$SERVICE_NAME" -n 80 --no-pager || true
  fi
  exit 1
fi

HEARTBEAT_FILE="$DATA_DIR/server-update/agent.json"
HEARTBEAT_OK=0
for _ in {1..10}; do
  if python3 - "$HEARTBEAT_FILE" <<'PY'
from pathlib import Path
import sys
import time

path = Path(sys.argv[1])
try:
    fresh = path.is_file() and time.time() - path.stat().st_mtime <= 10
except OSError:
    fresh = False
raise SystemExit(0 if fresh else 1)
PY
  then
    HEARTBEAT_OK=1
    break
  fi
  sleep 1
done

if [[ "$HEARTBEAT_OK" -ne 1 ]]; then
  echo "更新代理进程已启动，但 API 数据目录没有收到新鲜心跳：$HEARTBEAT_FILE" >&2
  echo "请检查 DATA_DIR 是否与 docker-compose.bt.yml 的 /data 挂载源一致。" >&2
  systemctl --no-pager --full status "$SERVICE_NAME" || true
  if command -v journalctl >/dev/null 2>&1; then
    journalctl -u "$SERVICE_NAME" -n 80 --no-pager || true
  fi
  exit 1
fi

systemctl --no-pager --full status "$SERVICE_NAME" || true

echo
echo "版本更新主机代理已安装并通过心跳检查。"
echo "服务: $SERVICE_NAME"
echo "状态目录: $DATA_DIR/server-update"
echo "GitHub 源码通道: SSH $GITHUB_SSH_HOST:$GITHUB_SSH_PORT，失败最多重试 $GIT_FETCH_RETRIES 次"
echo "控制台现在可以直接执行“版本更新 -> 更新服务端”。"
