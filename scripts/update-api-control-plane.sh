#!/usr/bin/env bash
set -Eeuo pipefail

REPO_URL="${REPO_URL:-git@github.com:b8vipvip/qnbot.git}"
REPO_DIR="${REPO_DIR:-/opt/qnbot}"
LEGACY_DIR="${LEGACY_DIR:-/opt/qianniu-api-control-plane}"
BRANCH="${BRANCH:-master}"
BACKUP_ROOT="${BACKUP_ROOT:-/opt/qnbot-backups}"
CONTAINER_NAME="${CONTAINER_NAME:-qianniu-api-control-plane}"
VERIFY_URL="${VERIFY_URL:-}"
# Fail closed before image builds and before stopping the live service for a backup.
MIN_FREE_BEFORE_BUILD_MIB="${CONTROL_PLANE_MIN_BUILD_FREE_MIB:-4096}"
MIN_FREE_BEFORE_BACKUP_MIB="${CONTROL_PLANE_MIN_BACKUP_FREE_MIB:-2048}"

# GitHub source-network tuning. Git repository traffic uses GitHub's official SSH-over-443
# endpoint by default, avoiding networks where outbound TCP/22 is unstable or filtered.
GITHUB_SSH_HOST="${GITHUB_SSH_HOST:-ssh.github.com}"
GITHUB_SSH_PORT="${GITHUB_SSH_PORT:-443}"
GITHUB_SSH_CONNECT_TIMEOUT="${GITHUB_SSH_CONNECT_TIMEOUT:-15}"
GITHUB_SSH_SERVER_ALIVE_INTERVAL="${GITHUB_SSH_SERVER_ALIVE_INTERVAL:-15}"
GITHUB_SSH_SERVER_ALIVE_COUNT_MAX="${GITHUB_SSH_SERVER_ALIVE_COUNT_MAX:-4}"
GIT_FETCH_RETRIES="${GIT_FETCH_RETRIES:-5}"
GIT_FETCH_TIMEOUT_SECONDS="${GIT_FETCH_TIMEOUT_SECONDS:-240}"
GIT_FETCH_RETRY_BASE_SECONDS="${GIT_FETCH_RETRY_BASE_SECONDS:-3}"

# Build/network tuning. These defaults are intentionally suitable for Tencent Cloud VPCs,
# while every value remains overridable from the shell for other environments.
BASE_IMAGE="${CONTROL_PLANE_BASE_IMAGE:-python:3.11-slim}"
BUILD_RETRIES="${CONTROL_PLANE_BUILD_RETRIES:-3}"
BUILD_RETRY_DELAY_SECONDS="${CONTROL_PLANE_BUILD_RETRY_DELAY_SECONDS:-12}"
BUILD_TIMEOUT_SECONDS="${CONTROL_PLANE_BUILD_TIMEOUT_SECONDS:-1800}"
BASE_IMAGE_PULL_RETRIES="${CONTROL_PLANE_BASE_IMAGE_PULL_RETRIES:-3}"
BASE_IMAGE_PULL_TIMEOUT_SECONDS="${CONTROL_PLANE_BASE_IMAGE_PULL_TIMEOUT_SECONDS:-180}"
REFRESH_BASE_IMAGE="${CONTROL_PLANE_REFRESH_BASE_IMAGE:-0}"
CONTROL_PLANE_BUILD_PIP_INDEX_URL="${CONTROL_PLANE_BUILD_PIP_INDEX_URL:-https://mirrors.cloud.tencent.com/pypi/simple}"
CONTROL_PLANE_BUILD_PIP_TRUSTED_HOST="${CONTROL_PLANE_BUILD_PIP_TRUSTED_HOST:-}"
CONTROL_PLANE_BUILD_PIP_TIMEOUT="${CONTROL_PLANE_BUILD_PIP_TIMEOUT:-120}"
CONTROL_PLANE_BUILD_PIP_RETRIES="${CONTROL_PLANE_BUILD_PIP_RETRIES:-8}"
CONTROL_PLANE_BUILD_APT_MIRROR="${CONTROL_PLANE_BUILD_APT_MIRROR:-https://mirrors.cloud.tencent.com/debian}"
CONTROL_PLANE_BUILD_APT_SECURITY_MIRROR="${CONTROL_PLANE_BUILD_APT_SECURITY_MIRROR:-https://mirrors.cloud.tencent.com/debian-security}"

export CONTROL_PLANE_BUILD_PIP_INDEX_URL
export CONTROL_PLANE_BUILD_PIP_TRUSTED_HOST
export CONTROL_PLANE_BUILD_PIP_TIMEOUT
export CONTROL_PLANE_BUILD_PIP_RETRIES
export CONTROL_PLANE_BUILD_APT_MIRROR
export CONTROL_PLANE_BUILD_APT_SECURITY_MIRROR
export DOCKER_BUILDKIT="${DOCKER_BUILDKIT:-1}"
export BUILDKIT_PROGRESS="${BUILDKIT_PROGRESS:-plain}"

SERVICE_REL="services/api-control-plane"
SERVICE_DIR="$REPO_DIR/$SERVICE_REL"
COMPOSE_FILE="$SERVICE_DIR/docker-compose.bt.yml"
TIMESTAMP="$(date +%Y%m%d-%H%M%S)"
BACKUP_DIR="$BACKUP_ROOT/$TIMESTAMP"
BUILD_LOG="/tmp/qianniu-control-plane-build-$TIMESTAMP.log"
OLD_COMMIT=""
OLD_DEPLOY_KIND="none"
OLD_SOURCE_DIR=""
BACKUP_READY=0

log() {
  printf '\n[%s] %s\n' "$(date '+%F %T')" "$*"
}

warn() {
  printf '\n[WARN] %s\n' "$*" >&2
}

die() {
  printf '\n[ERROR] %s\n' "$*" >&2
  exit 1
}

need() {
  command -v "$1" >/dev/null 2>&1 || die "缺少命令: $1"
}

# Use filesystem-available KiB (excludes root-reserved blocks).
check_free_kib() {
  local path="$1" minimum_kib="$2" stage="$3" available_kib
  available_kib="$(df -Pk "$path" | awk 'END { print $4 }')"
  [[ "$available_kib" =~ ^[0-9]+$ ]] || die "无法读取磁盘可用空间: $path"
  if (( available_kib < minimum_kib )); then
    die "${stage}磁盘空间不足: $path 可用 $((available_kib / 1024)) MiB，要求至少 $((minimum_kib / 1024)) MiB；旧服务未停止。"
  fi
  log "${stage}磁盘空间检查通过: $path 可用 $((available_kib / 1024)) MiB"
}

check_space_before_build() {
  check_free_kib "$SERVICE_DIR" "$(( MIN_FREE_BEFORE_BUILD_MIB * 1024 ))" "构建前"
}

check_space_before_backup() {
  local source_dir="$SERVICE_DIR/data" source_kib=0 required_kib
  if [[ "$OLD_DEPLOY_KIND" == "legacy" ]]; then
    source_dir="$LEGACY_DIR/data"
  fi
  if [[ -d "$source_dir" ]]; then
    source_kib="$(du -sk "$source_dir" | awk '{ print $1 }')"
    [[ "$source_kib" =~ ^[0-9]+$ ]] || die "无法估算生产数据大小: $source_dir"
  fi
  mkdir -p "$BACKUP_ROOT"
  required_kib=$(( source_kib + MIN_FREE_BEFORE_BACKUP_MIB * 1024 ))
  check_free_kib "$BACKUP_ROOT" "$required_kib" "停服备份前"
  check_free_kib "$SERVICE_DIR" "$(( MIN_FREE_BEFORE_BACKUP_MIB * 1024 ))" "停服备份前"
}

is_positive_integer() {
  [[ "${1:-}" =~ ^[1-9][0-9]*$ ]]
}

validate_tuning() {
  local name value
  for name in BUILD_RETRIES BUILD_RETRY_DELAY_SECONDS BUILD_TIMEOUT_SECONDS BASE_IMAGE_PULL_RETRIES BASE_IMAGE_PULL_TIMEOUT_SECONDS CONTROL_PLANE_BUILD_PIP_TIMEOUT CONTROL_PLANE_BUILD_PIP_RETRIES GITHUB_SSH_CONNECT_TIMEOUT GITHUB_SSH_SERVER_ALIVE_INTERVAL GITHUB_SSH_SERVER_ALIVE_COUNT_MAX GIT_FETCH_RETRIES GIT_FETCH_TIMEOUT_SECONDS GIT_FETCH_RETRY_BASE_SECONDS MIN_FREE_BEFORE_BUILD_MIB MIN_FREE_BEFORE_BACKUP_MIB; do
    value="${!name}"
    is_positive_integer "$value" || die "$name 必须是正整数，当前值: $value"
  done
  [[ "$REFRESH_BASE_IMAGE" == "0" || "$REFRESH_BASE_IMAGE" == "1" ]] \
    || die "CONTROL_PLANE_REFRESH_BASE_IMAGE 只能是 0 或 1"
}

read_env_value() {
  local key="$1"
  local file="$2"
  [[ -f "$file" ]] || return 0
  awk -F= -v key="$key" '
    $0 !~ /^[[:space:]]*#/ && $1 == key {
      value=substr($0, index($0, "=")+1)
      gsub(/^[[:space:]\047"]+|[[:space:]\047"]+$/, "", value)
      found=value
    }
    END { if (found != "") print found }
  ' "$file"
}

compose() {
  docker compose -f "$COMPOSE_FILE" "$@"
}

container_exists() {
  docker ps -a --format '{{.Names}}' | grep -Fxq "$CONTAINER_NAME"
}

container_running() {
  docker ps --format '{{.Names}}' | grep -Fxq "$CONTAINER_NAME"
}

probe_url() {
  local label="$1"
  local url="$2"
  if curl -fsSI --connect-timeout 4 --max-time 10 "$url" >/dev/null 2>&1; then
    log "网络预检通过: $label -> $url"
    return 0
  fi
  warn "网络预检较慢或不可达: $label -> $url；构建仍会继续，并由内部重试保护。"
  return 1
}

github_ssh_command() {
  printf 'ssh -o HostName=%q -p %q -o ConnectTimeout=%q -o ConnectionAttempts=3 -o ServerAliveInterval=%q -o ServerAliveCountMax=%q -o TCPKeepAlive=yes -o StrictHostKeyChecking=accept-new' \
    "$GITHUB_SSH_HOST" "$GITHUB_SSH_PORT" "$GITHUB_SSH_CONNECT_TIMEOUT" "$GITHUB_SSH_SERVER_ALIVE_INTERVAL" "$GITHUB_SSH_SERVER_ALIVE_COUNT_MAX"
}

github_git() {
  local command
  command="$(github_ssh_command)"
  GIT_SSH_COMMAND="$command" git "$@"
}

github_clone_with_retry() {
  local attempt rc delay command
  command="$(github_ssh_command)"
  for attempt in $(seq 1 "$GIT_FETCH_RETRIES"); do
    log "通过 GitHub SSH ${GITHUB_SSH_HOST}:${GITHUB_SSH_PORT} 克隆源码，第 ${attempt}/${GIT_FETCH_RETRIES} 次"
    set +e
    timeout --foreground "${GIT_FETCH_TIMEOUT_SECONDS}s" env GIT_SSH_COMMAND="$command" git clone --branch "$BRANCH" "$REPO_URL" "$REPO_DIR"
    rc=$?
    set -e
    if [[ "$rc" -eq 0 ]]; then return 0; fi
    rm -rf "$REPO_DIR"
    if [[ "$attempt" -lt "$GIT_FETCH_RETRIES" ]]; then
      delay=$(( GIT_FETCH_RETRY_BASE_SECONDS * (2 ** (attempt - 1)) ))
      [[ "$delay" -gt 60 ]] && delay=60
      warn "GitHub SSH 克隆失败，${delay}s 后自动重试；已启用 SSH 443/keepalive。"
      sleep "$delay"
    fi
  done
  die "GitHub SSH 克隆连续失败 ${GIT_FETCH_RETRIES} 次，请检查服务器到 ssh.github.com:443 的网络或 SSH Key。"
}

github_fetch_with_retry() {
  local attempt rc delay command
  command="$(github_ssh_command)"
  for attempt in $(seq 1 "$GIT_FETCH_RETRIES"); do
    log "GitHub 源码同步: SSH ${GITHUB_SSH_HOST}:${GITHUB_SSH_PORT}，第 ${attempt}/${GIT_FETCH_RETRIES} 次"
    set +e
    timeout --foreground "${GIT_FETCH_TIMEOUT_SECONDS}s" env GIT_SSH_COMMAND="$command" \
      git -C "$REPO_DIR" fetch --prune origin "$BRANCH"
    rc=$?
    set -e
    if [[ "$rc" -eq 0 ]]; then
      log "GitHub 源码同步成功（SSH ${GITHUB_SSH_PORT}）"
      return 0
    fi
    if [[ "$attempt" -lt "$GIT_FETCH_RETRIES" ]]; then
      delay=$(( GIT_FETCH_RETRY_BASE_SECONDS * (2 ** (attempt - 1)) ))
      [[ "$delay" -gt 60 ]] && delay=60
      warn "GitHub fetch 第 ${attempt}/${GIT_FETCH_RETRIES} 次失败，${delay}s 后重试；旧服务保持运行。"
      sleep "$delay"
    fi
  done
  die "GitHub fetch 连续失败 ${GIT_FETCH_RETRIES} 次；旧服务未停止。"
}

# Verify and reuse the proxy binary from the currently running, previously
# deployed control-plane image. This prevents a blocked GitHub Release tarball
# from delaying an otherwise cached upgrade for 30-90 minutes. Never stop or
# mutate the running container; tagging its immutable image is safe.
prepare_sing_box_source() {
  local cache_tag="qnbot-sing-box-source:1.13.19"
  local candidate="" version=""
  if [[ -n "${CONTROL_PLANE_SING_BOX_SOURCE_IMAGE:-}" ]]; then
    log "使用已指定的 sing-box 来源镜像: $CONTROL_PLANE_SING_BOX_SOURCE_IMAGE"
    return 0
  fi
  if container_running; then
    version="$(timeout 12s docker exec "$CONTAINER_NAME" /usr/local/bin/sing-box version 2>/dev/null | head -n 1 || true)"
    if [[ "$version" == *"sing-box version 1.13.19"* ]]; then
      candidate="$(docker inspect -f '{{.Image}}' "$CONTAINER_NAME" 2>/dev/null || true)"
      if [[ -n "$candidate" ]] && docker image inspect "$candidate" >/dev/null 2>&1; then
        docker image tag "$candidate" "$cache_tag"
        export CONTROL_PLANE_SING_BOX_SOURCE_IMAGE="$cache_tag"
        log "复用正在运行的旧服务镜像中的 sing-box 1.13.19: $candidate（免下载 GitHub Release）"
        return 0
      fi
    fi
  fi
  # Reuse the previously vetted local tag if a restart left it behind.
  if docker image inspect "$cache_tag" >/dev/null 2>&1; then
    version="$(timeout 15s docker run --rm --network none --entrypoint /usr/local/bin/sing-box "$cache_tag" version 2>/dev/null | head -n 1 || true)"
    if [[ "$version" == *"sing-box version 1.13.19"* ]]; then
      export CONTROL_PLANE_SING_BOX_SOURCE_IMAGE="$cache_tag"
      log "复用宿主机已缓存的 sing-box 来源镜像: $cache_tag"
      return 0
    fi
  fi
  warn "未找到经过版本校验的本地 sing-box 镜像，将从官方 GHCR 固定摘要获取；如受限请先恢复旧容器或配置可信镜像通道。"
}

prepare_base_image() {
  local have_local=0
  if docker image inspect "$BASE_IMAGE" >/dev/null 2>&1; then
    have_local=1
  fi

  if [[ "$have_local" -eq 1 && "$REFRESH_BASE_IMAGE" != "1" ]]; then
    log "基础镜像已存在，直接复用本地缓存: $BASE_IMAGE（如需强制刷新可设置 CONTROL_PLANE_REFRESH_BASE_IMAGE=1）"
    return 0
  fi

  log "准备基础镜像: $BASE_IMAGE；最多重试 ${BASE_IMAGE_PULL_RETRIES} 次，每次超时 ${BASE_IMAGE_PULL_TIMEOUT_SECONDS}s"
  local attempt rc
  for attempt in $(seq 1 "$BASE_IMAGE_PULL_RETRIES"); do
    set +e
    timeout --foreground "${BASE_IMAGE_PULL_TIMEOUT_SECONDS}s" docker pull "$BASE_IMAGE"
    rc=$?
    set -e
    if [[ "$rc" -eq 0 ]]; then
      log "基础镜像准备完成: $BASE_IMAGE"
      return 0
    fi
    warn "基础镜像拉取第 ${attempt}/${BASE_IMAGE_PULL_RETRIES} 次失败，exit=$rc"
    if docker image inspect "$BASE_IMAGE" >/dev/null 2>&1; then
      warn "远端刷新失败，但本机已有完整基础镜像，将继续使用本地缓存。"
      return 0
    fi
    if [[ "$attempt" -lt "$BASE_IMAGE_PULL_RETRIES" ]]; then
      sleep "$BUILD_RETRY_DELAY_SECONDS"
    fi
  done
  die "基础镜像 $BASE_IMAGE 不存在且连续拉取失败，请检查 Docker 镜像加速或网络。"
}

print_build_failure_hint() {
  [[ -f "$BUILD_LOG" ]] || return 0
  if grep -Eq 'files\.pythonhosted\.org|ReadTimeoutError|Read timed out|pip.*timeout|HTTPSConnectionPool' "$BUILD_LOG"; then
    warn "检测到 PyPI/pip 下载超时。当前构建已配置腾讯云 PyPI 镜像、120s 读取超时和多次重试；若仍失败，可临时覆盖 CONTROL_PLANE_BUILD_PIP_INDEX_URL。"
  fi
  if grep -Eq 'apt-get|deb\.debian\.org|debian-security|Temporary failure resolving|Connection timed out' "$BUILD_LOG"; then
    warn "检测到 APT/Debian 软件源访问异常。当前 Dockerfile 已支持腾讯云 Debian 镜像和 Acquire::Retries。"
  fi
  if grep -Eq 'ghcr.io/sagernet/sing-box|sing-box-provider|failed to resolve.*ghcr.io' "$BUILD_LOG"; then
    warn "sing-box 官方镜像未拉取成功，请确认旧控制面容器是否包含 sing-box 1.13.19；后续更新将优先复用旧容器的已安装二进制。"
  fi
  if grep -Eq 'no space left on device|ENOSPC' "$BUILD_LOG"; then
    warn "检测到磁盘空间不足。请先运行 docker system df 和 df -h 检查空间。"
  fi
}

build_new_image() {
  log "构建网络配置:"
  printf '  GitHub source: SSH %s:%s, retries=%s\n' "$GITHUB_SSH_HOST" "$GITHUB_SSH_PORT" "$GIT_FETCH_RETRIES"
  printf '  Base image: %s\n' "$BASE_IMAGE"
  printf '  PyPI: %s\n' "$CONTROL_PLANE_BUILD_PIP_INDEX_URL"
  printf '  APT: %s\n' "$CONTROL_PLANE_BUILD_APT_MIRROR"
  printf '  APT security: %s\n' "$CONTROL_PLANE_BUILD_APT_SECURITY_MIRROR"
  printf '  Build retries: %s, timeout per attempt: %ss\n' "$BUILD_RETRIES" "$BUILD_TIMEOUT_SECONDS"

  local attempt rc started elapsed
  for attempt in $(seq 1 "$BUILD_RETRIES"); do
    log "构建新镜像，第 ${attempt}/${BUILD_RETRIES} 次；旧服务继续运行"
    started="$(date +%s)"
    : > "$BUILD_LOG"
    set +e
    (
      cd "$SERVICE_DIR"
      timeout --foreground "${BUILD_TIMEOUT_SECONDS}s" \
        docker compose -f docker-compose.bt.yml build
    ) 2>&1 | tee "$BUILD_LOG"
    rc=${PIPESTATUS[0]}
    set -e
    elapsed=$(( $(date +%s) - started ))

    if [[ "$rc" -eq 0 ]]; then
      log "新镜像构建成功，耗时 ${elapsed}s"
      return 0
    fi

    warn "新镜像构建第 ${attempt}/${BUILD_RETRIES} 次失败，exit=$rc，耗时 ${elapsed}s"
    print_build_failure_hint
    if [[ "$rc" -eq 124 ]]; then
      warn "本轮构建超过 ${BUILD_TIMEOUT_SECONDS}s 已主动终止，避免无限卡住。"
    fi
    if [[ "$attempt" -lt "$BUILD_RETRIES" ]]; then
      warn "保留 BuildKit/apt/pip 缓存，${BUILD_RETRY_DELAY_SECONDS}s 后自动重试。"
      sleep "$BUILD_RETRY_DELAY_SECONDS"
    fi
  done

  die "新镜像连续构建 ${BUILD_RETRIES} 次失败，旧服务未被停止。完整构建日志: $BUILD_LOG"
}

restore_backup_to() {
  local target="$1"
  [[ "$BACKUP_READY" -eq 1 ]] || return 0
  mkdir -p "$target"
  if [[ -f "$BACKUP_DIR/.env" ]]; then
    cp -a "$BACKUP_DIR/.env" "$target/.env"
  fi
  if [[ -f "$BACKUP_DIR/data.tar.gz" ]]; then
    rm -rf "$target/data"
    mkdir -p "$target/data"
    tar -xzf "$BACKUP_DIR/data.tar.gz" -C "$target/data"
  fi
}

rollback() {
  warn "新版本启动或健康检查失败，开始自动回滚。"
  set +e
  docker rm -f "$CONTAINER_NAME" >/dev/null 2>&1 || true

  if [[ "$OLD_DEPLOY_KIND" == "repo" && -n "$OLD_COMMIT" ]]; then
    git -C "$REPO_DIR" reset --hard "$OLD_COMMIT"
    restore_backup_to "$REPO_DIR/$SERVICE_REL"
    if [[ -f "$REPO_DIR/$SERVICE_REL/docker-compose.bt.yml" ]]; then
      (
        cd "$REPO_DIR/$SERVICE_REL" || exit 1
        docker compose -f docker-compose.bt.yml up -d --build --force-recreate
      )
    fi
  elif [[ "$OLD_DEPLOY_KIND" == "legacy" && -n "$OLD_SOURCE_DIR" ]]; then
    restore_backup_to "$OLD_SOURCE_DIR"
    if [[ -f "$OLD_SOURCE_DIR/docker-compose.bt.yml" ]]; then
      (
        cd "$OLD_SOURCE_DIR" || exit 1
        docker compose -f docker-compose.bt.yml up -d --build --force-recreate
      )
    elif [[ -f "$OLD_SOURCE_DIR/docker-compose.yml" ]]; then
      (
        cd "$OLD_SOURCE_DIR" || exit 1
        docker compose up -d --build --force-recreate
      )
    fi
  fi

  warn "已执行回滚尝试。备份目录: $BACKUP_DIR"
  exit 1
}

need git
need ssh
need docker
need curl
need tar
need timeout
validate_tuning

docker compose version >/dev/null 2>&1 || die "未检测到 Docker Compose v2（docker compose）"

if [[ ! -d "$REPO_DIR/.git" ]]; then
  log "未发现 Git 仓库，使用服务器现有 GitHub SSH Key 克隆到 $REPO_DIR"
  mkdir -p "$(dirname "$REPO_DIR")"
  github_clone_with_retry
fi

[[ -d "$REPO_DIR/.git" ]] || die "$REPO_DIR 不是有效 Git 仓库"

TRACKED_DIRTY="$(git -C "$REPO_DIR" status --porcelain --untracked-files=no)"
if [[ -n "$TRACKED_DIRTY" ]]; then
  printf '%s\n' "$TRACKED_DIRTY" >&2
  die "仓库存在未提交的已跟踪文件修改。为避免覆盖服务器手工改动，已停止更新。"
fi

OLD_COMMIT="$(git -C "$REPO_DIR" rev-parse HEAD)"
if [[ -f "$SERVICE_DIR/.env" || -d "$SERVICE_DIR/data" ]]; then
  OLD_DEPLOY_KIND="repo"
  OLD_SOURCE_DIR="$SERVICE_DIR"
elif [[ -f "$LEGACY_DIR/.env" || -d "$LEGACY_DIR/data" ]]; then
  OLD_DEPLOY_KIND="legacy"
  OLD_SOURCE_DIR="$LEGACY_DIR"
fi

check_space_before_build
log "拉取 GitHub 最新 $BRANCH 分支（Git SSH over 443 + 自动重试）"
github_fetch_with_retry
if git -C "$REPO_DIR" show-ref --verify --quiet "refs/heads/$BRANCH"; then
  git -C "$REPO_DIR" checkout "$BRANCH"
else
  git -C "$REPO_DIR" checkout -b "$BRANCH" "origin/$BRANCH"
fi
git -C "$REPO_DIR" merge --ff-only "origin/$BRANCH"
NEW_COMMIT="$(git -C "$REPO_DIR" rev-parse HEAD)"
log "代码版本: $OLD_COMMIT -> $NEW_COMMIT"
# BuildKit uses this immutable revision as image metadata. It is not a signal
# that the container has been switched: only a healthy started container is.
export CONTROL_PLANE_BUILD_COMMIT="$NEW_COMMIT"
log "即将构建的镜像来源提交: $CONTROL_PLANE_BUILD_COMMIT"

[[ -f "$SERVICE_DIR/Dockerfile" ]] || die "缺少 $SERVICE_DIR/Dockerfile"
[[ -f "$SERVICE_DIR/runtime_streaming_guard.py" ]] || die "缺少 runtime_streaming_guard.py，拒绝部署不完整版本"
[[ -f "$COMPOSE_FILE" ]] || die "缺少宝塔 Compose 文件: $COMPOSE_FILE"

# 老的 ZIP 部署通常位于 /opt/qianniu-api-control-plane。第一次切换到 Git 仓库部署时复用原配置。
if [[ ! -f "$SERVICE_DIR/.env" && -f "$LEGACY_DIR/.env" ]]; then
  log "检测到旧部署配置，复制 .env 到 Git 仓库服务目录"
  cp -a "$LEGACY_DIR/.env" "$SERVICE_DIR/.env"
fi

[[ -f "$SERVICE_DIR/.env" ]] || die "未找到 $SERVICE_DIR/.env。请先把现有 .env 放到该目录后重试。"

if grep -Eq 'replace-with-|change-me-in-production' "$SERVICE_DIR/.env"; then
  die ".env 仍包含示例占位密钥/密码，拒绝启动生产服务。"
fi

log "校验宝塔 Compose 配置"
(
  cd "$SERVICE_DIR"
  docker compose -f docker-compose.bt.yml config >/dev/null
)

# These probes are diagnostic only. A transient probe failure must not take production down.
probe_url "腾讯云 PyPI" "${CONTROL_PLANE_BUILD_PIP_INDEX_URL%/}/pip/" || true
probe_url "腾讯云 Debian" "${CONTROL_PLANE_BUILD_APT_MIRROR%/}/" || true

docker system df || true
prepare_base_image
prepare_sing_box_source
build_new_image
check_space_before_backup

log "停止旧控制面并创建冷备份"
if container_running; then
  docker stop "$CONTAINER_NAME" >/dev/null
fi

mkdir -p "$BACKUP_DIR"
ENV_SOURCE="$SERVICE_DIR/.env"
DATA_SOURCE="$SERVICE_DIR/data"
if [[ "$OLD_DEPLOY_KIND" == "legacy" ]]; then
  [[ -f "$LEGACY_DIR/.env" ]] && ENV_SOURCE="$LEGACY_DIR/.env"
  [[ -d "$LEGACY_DIR/data" ]] && DATA_SOURCE="$LEGACY_DIR/data"
fi

if [[ -f "$ENV_SOURCE" ]]; then
  cp -a "$ENV_SOURCE" "$BACKUP_DIR/.env"
fi
if [[ -d "$DATA_SOURCE" ]]; then
  tar -czf "$BACKUP_DIR/data.tar.gz" -C "$DATA_SOURCE" .
fi
printf '%s\n' "$OLD_COMMIT" > "$BACKUP_DIR/old-git-commit.txt"
printf '%s\n' "$NEW_COMMIT" > "$BACKUP_DIR/new-git-commit.txt"
BACKUP_READY=1
log "备份完成: $BACKUP_DIR"

# 首次从旧 ZIP 部署迁移到 Git 仓库部署时，冷复制原 data。
if [[ "$OLD_DEPLOY_KIND" == "legacy" ]]; then
  log "迁移旧部署的 .env 和 data 到 $SERVICE_DIR"
  cp -a "$LEGACY_DIR/.env" "$SERVICE_DIR/.env"
  rm -rf "$SERVICE_DIR/data"
  mkdir -p "$SERVICE_DIR/data"
  if [[ -d "$LEGACY_DIR/data" ]]; then
    cp -a "$LEGACY_DIR/data/." "$SERVICE_DIR/data/"
  fi
fi
mkdir -p "$SERVICE_DIR/data"

if container_exists; then
  docker rm -f "$CONTAINER_NAME" >/dev/null
fi

log "启动已经构建并验证过的新控制面镜像（不重复 build，不再次访问软件源）"
if ! (
  cd "$SERVICE_DIR"
  docker compose -f docker-compose.bt.yml up -d --no-build --force-recreate
); then
  rollback
fi

BIND_PORT="$(read_env_value CONTROL_PLANE_BIND_PORT "$SERVICE_DIR/.env")"
BIND_PORT="${BIND_PORT:-18081}"
LOCAL_HEALTH="http://127.0.0.1:${BIND_PORT}/healthz"

log "等待本机健康检查: $LOCAL_HEALTH"
HEALTH_OK=0
for _ in $(seq 1 60); do
  if curl -fsS --max-time 5 "$LOCAL_HEALTH" >/tmp/qianniu-control-plane-health.json 2>/dev/null; then
    HEALTH_OK=1
    break
  fi
  sleep 2
done

if [[ "$HEALTH_OK" -ne 1 ]]; then
  docker logs --tail 200 "$CONTAINER_NAME" || true
  rollback
fi
cat /tmp/qianniu-control-plane-health.json

CMD_JSON="$(docker inspect -f '{{json .Config.Cmd}}' "$CONTAINER_NAME" 2>/dev/null || true)"
if [[ "$CMD_JSON" != *"bootstrap.py"* ]]; then
  warn "容器启动命令不是 bootstrap.py: $CMD_JSON"
  rollback
fi

if [[ -z "$VERIFY_URL" ]]; then
  PUBLIC_BASE_URL="$(read_env_value PUBLIC_BASE_URL "$SERVICE_DIR/.env")"
  CONTROL_PLANE_DOMAIN="$(read_env_value CONTROL_PLANE_DOMAIN "$SERVICE_DIR/.env")"
  if [[ -n "$PUBLIC_BASE_URL" ]]; then
    VERIFY_URL="${PUBLIC_BASE_URL%/}/healthz"
  elif [[ -n "$CONTROL_PLANE_DOMAIN" ]]; then
    VERIFY_URL="https://${CONTROL_PLANE_DOMAIN}/healthz"
  else
    VERIFY_URL="https://aboter.mv3.cn/healthz"
  fi
fi

log "验证宝塔现有反代和 SSL: $VERIFY_URL"
if ! curl -fsS --max-time 15 "$VERIFY_URL" >/tmp/qianniu-control-plane-public-health.json; then
  warn "本机服务已健康，但公网域名验证失败。未回滚服务，因为这通常属于宝塔反代、DNS 或 SSL 层问题。"
  warn "请检查宝塔反向代理目标是否仍为 http://127.0.0.1:${BIND_PORT}"
  compose ps
  exit 2
fi
cat /tmp/qianniu-control-plane-public-health.json

log "更新成功"
printf 'Git commit: %s\n' "$NEW_COMMIT"
printf 'Git transport: SSH %s:%s, retries=%s\n' "$GITHUB_SSH_HOST" "$GITHUB_SSH_PORT" "$GIT_FETCH_RETRIES"
printf 'Service dir: %s\n' "$SERVICE_DIR"
printf 'Backup: %s\n' "$BACKUP_DIR"
printf 'Build log: %s\n' "$BUILD_LOG"
printf 'Local health: %s\n' "$LOCAL_HEALTH"
printf 'Public health: %s\n' "$VERIFY_URL"
compose ps
