# syntax=docker/dockerfile:1.7
# For upgrades, the host updater supplies the already-working local image. Fresh
# deployments use the upstream official version/digest, never a third-party mirror.
ARG SING_BOX_SOURCE_IMAGE=ghcr.io/sagernet/sing-box:v1.13.19@sha256:78ca1bc9f3b563b0944fd6b1126522ee631e7b44d540b91fc435ba7062ef08b2
FROM ${SING_BOX_SOURCE_IMAGE} AS sing-box-provider
FROM python:3.11-slim

ARG APT_MIRROR=""
ARG APT_SECURITY_MIRROR=""
ARG PIP_INDEX_URL="https://pypi.org/simple"
ARG PIP_TRUSTED_HOST=""
ARG PIP_DEFAULT_TIMEOUT="120"
ARG PIP_RETRIES="8"
ENV PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1 \
    DATA_DIR=/data \
    PORT=8080

RUN --mount=type=cache,target=/var/cache/apt,sharing=locked \
    --mount=type=cache,target=/var/lib/apt/lists,sharing=locked \
    set -eux; \
    for source_file in /etc/apt/sources.list /etc/apt/sources.list.d/*.list /etc/apt/sources.list.d/*.sources; do \
      [ -f "$source_file" ] || continue; \
      if [ -n "$APT_SECURITY_MIRROR" ]; then \
        sed -i \
          -e "s#http://deb.debian.org/debian-security#$APT_SECURITY_MIRROR#g" \
          -e "s#https://deb.debian.org/debian-security#$APT_SECURITY_MIRROR#g" \
          -e "s#http://security.debian.org/debian-security#$APT_SECURITY_MIRROR#g" \
          -e "s#https://security.debian.org/debian-security#$APT_SECURITY_MIRROR#g" \
          "$source_file"; \
      fi; \
      if [ -n "$APT_MIRROR" ]; then \
        sed -i \
          -e "s#http://deb.debian.org/debian#$APT_MIRROR#g" \
          -e "s#https://deb.debian.org/debian#$APT_MIRROR#g" \
          "$source_file"; \
      fi; \
    done; \
    apt-get -o Acquire::Retries=5 -o Acquire::http::Timeout=30 -o Acquire::https::Timeout=30 update; \
    apt-get -o Acquire::Retries=5 -o Acquire::http::Timeout=30 -o Acquire::https::Timeout=30 \
      install -y --no-install-recommends \
        ca-certificates \
        curl \
        fonts-dejavu-core \
        libgl1 \
        libglib2.0-0t64 \
        libx11-6 \
        libxcb1 \
        libxext6 \
        libxrender1


WORKDIR /app
COPY requirements.txt .
RUN --mount=type=cache,target=/root/.cache/pip,sharing=locked \
    set -eux; \
    trusted_args=""; \
    if [ -n "$PIP_TRUSTED_HOST" ]; then trusted_args="--trusted-host $PIP_TRUSTED_HOST"; fi; \
    python -m pip install \
      --disable-pip-version-check \
      --retries "$PIP_RETRIES" \
      --timeout "$PIP_DEFAULT_TIMEOUT" \
      --index-url "$PIP_INDEX_URL" \
      $trusted_args \
      -r requirements.txt

# RapidOCR stores its default models below the installed package directory. The
# runtime process is intentionally non-root, so prefetch the checksum-verified
# models while building the image instead of allowing the first buyer request to
# attempt a write into root-owned site-packages or depend on runtime Internet.
RUN set -eux; \
    python -c "import cv2; print('OpenCV runtime ready: ' + cv2.__version__)"; \
    for attempt in 1 2 3; do \
      if rapidocr download_models; then \
        break; \
      fi; \
      if [ "$attempt" -eq 3 ]; then \
        echo 'RapidOCR model prefetch failed after 3 attempts' >&2; \
        exit 1; \
      fi; \
      sleep $((attempt * 5)); \
    done; \
    python -c "from rapidocr import RapidOCR; RapidOCR(); print('RapidOCR default models initialized')"

# Reuse the already-working sing-box from the old running container image,
# rather than fetching GitHub Release tarballs during *every* control-plane build.
# The host upgrade script checks the binary version and provides a local tag.
# This stage copies only the executable, never old application files or secrets.
COPY --from=sing-box-provider /usr/local/bin/sing-box /usr/local/bin/sing-box
RUN set -eux; \
    /usr/local/bin/sing-box version | grep -F 'sing-box version 1.13.19'

COPY app.py bootstrap.py bot_client_shop_binding.py bot_update_cache.py bot_update_prefetch.py bot_update_progress.py bot_update_push.py bot_web_console.py bot_web_ai_model_settings.py bot_web_auto_reply_rules.py bot_web_bot_enabled.py bot_web_admin.py bot_web_bot_qa.py bot_web_bot_qa_logic.py bot_web_conversation_knowledge.py bot_web_settings_ack.py ai_request_records.py chat2api_runtime_guard.py client_data_backup.py console_cache_guard.py deep_test_guard.py github_vless_proxy.py message_processing_traces.py knowledge_v2_sync.py manual_recharge.py runtime_shop_ai_proxy.py runtime_auth_failures.py store_rule_sync.py recharge_status_query.py runtime_routing_guard.py runtime_streaming_guard.py runtime_embedding_guard.py runtime_ocr.py runtime_ocr_priority.py scheduled_deep_test_retry.py version_update_admin.py server_runtime_logs.py wecom_bridge.py wecom_crypto.py wecom_settings.py wecom_policy_migration.py ./
COPY static ./static

RUN useradd --create-home --uid 10001 appuser \
    && mkdir -p /data \
    && chown -R appuser:appuser /app /data

# Stamp the actual source revision into the new image, not into persisted
# update status. Kept near the end to preserve expensive Python/OCR caches.
ARG CONTROL_PLANE_BUILD_COMMIT=unknown
ENV CONTROL_PLANE_BUILD_COMMIT=${CONTROL_PLANE_BUILD_COMMIT}
USER appuser
EXPOSE 8080
VOLUME ["/data"]

HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
  CMD curl -fsS http://127.0.0.1:8080/healthz || exit 1

CMD ["python", "bootstrap.py"]
