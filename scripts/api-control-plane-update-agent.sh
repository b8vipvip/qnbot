#!/usr/bin/env bash
set -Eeuo pipefail

REPO_DIR="${REPO_DIR:-/opt/qnbot}"
DATA_DIR="${DATA_DIR:-$REPO_DIR/services/api-control-plane/data}"
UPDATE_DIR="${UPDATE_DIR:-$DATA_DIR/server-update}"
REQUEST_FILE="$UPDATE_DIR/request.json"
STATUS_FILE="$UPDATE_DIR/status.json"
AGENT_FILE="$UPDATE_DIR/agent.json"
LOG_FILE="$UPDATE_DIR/update.log"
LOCK_FILE="$UPDATE_DIR/agent.lock"
# The log is a host-owned append-only diagnostic source shared with the API container.
BUILD_PHASE=0
BUILD_START_AT=0
BUILD_LAST_REPORT_AT=0
BUILD_ATTEMPT="?"
POLL_SECONDS="${POLL_SECONDS:-2}"
GITHUB_SSH_HOST="${GITHUB_SSH_HOST:-ssh.github.com}"
GITHUB_SSH_PORT="${GITHUB_SSH_PORT:-443}"
GIT_FETCH_RETRIES="${GIT_FETCH_RETRIES:-5}"

mkdir -p "$UPDATE_DIR"

json_get() {
  local path="$1" key="$2"
  python3 - "$path" "$key" <<'PY'
import json,sys
try:
    data=json.load(open(sys.argv[1],encoding='utf-8'))
    print(data.get(sys.argv[2],''))
except Exception:
    print('')
PY
}

write_json() {
  local path="$1" payload="$2"
  local temp="${path}.tmp.$$"
  printf '%s\n' "$payload" > "$temp"
  mv -f "$temp" "$path"
}

write_agent() {
  local current=""
  if [[ -d "$REPO_DIR/.git" ]]; then current="$(git -C "$REPO_DIR" rev-parse HEAD 2>/dev/null || true)"; fi
  write_json "$AGENT_FILE" "$(python3 - "$current" "$GITHUB_SSH_HOST" "$GITHUB_SSH_PORT" "$GIT_FETCH_RETRIES" <<'PY'
import json,os,sys,time
print(json.dumps({
 'pid':os.getppid(), 'last_seen_at_unix':time.time(), 'current_commit':sys.argv[1],
 'agent_version':2, 'git_transport':'ssh-443' if sys.argv[3]=='443' else 'ssh',
 'git_host':sys.argv[2], 'git_port':int(sys.argv[3]), 'git_fetch_attempts':int(sys.argv[4])
},ensure_ascii=False))
PY
)"
}

write_status() {
  local state="$1" phase="$2" progress="$3" message="$4" request_id="${5:-}" current="${6:-}" target="${7:-}"
  write_json "$STATUS_FILE" "$(python3 - "$state" "$phase" "$progress" "$message" "$request_id" "$current" "$target" <<'PY'
import json,sys,time
print(json.dumps({
 'state':sys.argv[1], 'phase':sys.argv[2], 'progress_percent':int(sys.argv[3]),
 'message':sys.argv[4], 'request_id':sys.argv[5], 'current_commit':sys.argv[6],
 'target_commit':sys.argv[7], 'updated_at_unix':time.time()
},ensure_ascii=False))
PY
)"
}

heartbeat_loop() {
  while true; do write_agent; sleep "$POLL_SECONDS"; done
}

phase_from_line() {
  local line="$1" request_id="$2" current="$3" target="$4"
  case "$line" in
    *"拉取 GitHub 最新"*) write_status running "同步 GitHub 源码" 7 "$line" "$request_id" "$current" "$target" ;;
    *"GitHub 源码同步:"*) write_status running "Git SSH 443 同步源码" 10 "$line" "$request_id" "$current" "$target" ;;
    *"GitHub fetch"*"失败"*) write_status running "GitHub 网络重试" 10 "$line" "$request_id" "$current" "$target" ;;
    *"GitHub 源码同步成功"*) write_status running "GitHub 源码同步完成" 13 "$line" "$request_id" "$current" "$target" ;;
    *"代码版本:"*) write_status running "确认目标版本" 15 "$line" "$request_id" "$current" "$target" ;;
    *"校验宝塔 Compose 配置"*) write_status running "校验部署配置" 20 "$line" "$request_id" "$current" "$target" ;;
    *"准备基础镜像:"*) write_status running "准备构建环境" 25 "$line" "$request_id" "$current" "$target" ;;
    *"构建新镜像"*)
      BUILD_PHASE=1
      BUILD_START_AT="$(date +%s)"
      BUILD_LAST_REPORT_AT="$BUILD_START_AT"
      if [[ "$line" =~ 第[[:space:]]*([0-9]+/[0-9]+)[[:space:]]*次 ]]; then
        BUILD_ATTEMPT="${BASH_REMATCH[1]}"
      fi
      write_status running "构建新服务镜像" 35 "镜像构建第 ${BUILD_ATTEMPT} 次；旧服务继续运行。可打开服务端运行日志查看 Docker 构建输出。" "$request_id" "$current" "$target"
      ;;
    *"构建第 "*"次失败"*|*"新镜像构建第 "*"次失败"*)
      BUILD_PHASE=0
      write_status running "构建失败，等待重试" 35 "$line" "$request_id" "$current" "$target"
      ;;
    *"GPTAuto 智能热修复："*) write_status running "智能热修复验证" 55 "$line" "$request_id" "$current" "$target" ;;
    *"GPTAuto 智能热修复成功"*) write_status running "热修复完成" 92 "$line" "$request_id" "$current" "$target" ;;
    *"无需容器部署"*) write_status running "无运行时代码修改" 95 "$line" "$request_id" "$current" "$target" ;;
    *"新镜像构建成功"*) BUILD_PHASE=0; write_status running "镜像构建完成" 60 "$line" "$request_id" "$current" "$target" ;;
    *"停止旧控制面并创建冷备份"*) write_status running "备份并切换服务" 66 "$line" "$request_id" "$current" "$target" ;;
    *"备份完成:"*) write_status running "备份完成" 74 "$line" "$request_id" "$current" "$target" ;;
    *"启动已经构建并验证过的新控制面镜像"*) write_status running "启动新版本" 82 "$line" "$request_id" "$current" "$target" ;;
    *"等待本机健康检查:"*) write_status running "本机健康检查" 90 "$line" "$request_id" "$current" "$target" ;;
    *"验证宝塔现有反代和 SSL:"*) write_status running "公网健康检查" 95 "$line" "$request_id" "$current" "$target" ;;
    *"更新成功"*) write_status running "更新完成，正在收尾" 99 "$line" "$request_id" "$current" "$target" ;;
  esac
  # Keep a bounded heartbeat while BuildKit emits logs. 35% is a phase marker,
  # not byte-level progress. Never pretend to know actual compilation percentage.
  if [[ "$BUILD_PHASE" -eq 1 ]]; then
    local now elapsed
    now="$(date +%s)"
    if (( now - BUILD_LAST_REPORT_AT >= 10 )); then
      elapsed=$(( now - BUILD_START_AT ))
      write_status running "构建新服务镜像" 35 "构建第 ${BUILD_ATTEMPT} 次，已耗时 ${elapsed}s，Docker 输出仍在更新；查看服务端运行日志获取详情。" "$request_id" "$current" "$target"
      BUILD_LAST_REPORT_AT="$now"
    fi
  fi
}

process_request() {
  local processing="$UPDATE_DIR/request.processing.json"
  mv -f "$REQUEST_FILE" "$processing"
  local request_id target current
  request_id="$(json_get "$processing" request_id)"
  target="$(json_get "$processing" requested_commit)"
  current="$(git -C "$REPO_DIR" rev-parse HEAD 2>/dev/null || true)"
  write_status running "准备更新" 3 "主机更新代理已接管任务；GitHub 源码将使用 SSH 443 + 自动重试" "$request_id" "$current" "$target"
  : > "$LOG_FILE"

  heartbeat_loop &
  local heartbeat_pid=$!
  set +e
  GITHUB_SSH_HOST="$GITHUB_SSH_HOST" GITHUB_SSH_PORT="$GITHUB_SSH_PORT" GIT_FETCH_RETRIES="$GIT_FETCH_RETRIES" \
    bash "$REPO_DIR/scripts/update-api-control-plane.sh" > >(
      while IFS= read -r line; do
        printf '%s\n' "$line" >> "$LOG_FILE"
        phase_from_line "$line" "$request_id" "$current" "$target"
      done
    ) 2>&1
  local rc=$?
  set -e
  kill "$heartbeat_pid" >/dev/null 2>&1 || true
  wait "$heartbeat_pid" >/dev/null 2>&1 || true

  local actual
  actual="$(git -C "$REPO_DIR" rev-parse HEAD 2>/dev/null || true)"
  if [[ "$rc" -eq 0 || "$rc" -eq 2 ]]; then
    local msg="服务端已更新并通过健康检查"
    [[ "$rc" -eq 2 ]] && msg="服务端已更新且本机健康；公网反代/SSL 验证存在警告，请查看更新日志"
    write_status completed "更新完成" 100 "$msg" "$request_id" "$actual" "$actual"
  else
    local tail_msg
    tail_msg="$(tail -n 1 "$LOG_FILE" 2>/dev/null || true)"
    write_status failed "更新失败" 100 "${tail_msg:-更新脚本执行失败，exit=$rc}" "$request_id" "$current" "$target"
  fi
  rm -f "$processing"
  write_agent
}

exec 9>"$LOCK_FILE"
if ! flock -n 9; then
  echo "api-control-plane update agent already running" >&2
  exit 0
fi

write_agent
if [[ ! -f "$STATUS_FILE" ]]; then
  current="$(git -C "$REPO_DIR" rev-parse HEAD 2>/dev/null || true)"
  write_status idle "等待更新" 0 "主机更新代理在线；GitHub 源码使用 SSH 443" "" "$current" "$current"
fi

while true; do
  write_agent
  if [[ -f "$REQUEST_FILE" ]]; then
    process_request
    # GitHub contents writes do not preserve executable mode, so always reload through bash.
    exec /bin/bash "$REPO_DIR/scripts/api-control-plane-update-agent.sh"
  fi
  sleep "$POLL_SECONDS"
done
