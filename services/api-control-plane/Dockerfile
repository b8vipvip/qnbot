# syntax=docker/dockerfile:1.7
FROM python:3.11-slim

ARG APT_MIRROR=""
ARG APT_SECURITY_MIRROR=""
ARG PIP_INDEX_URL="https://pypi.org/simple"
ARG PIP_TRUSTED_HOST=""
ARG PIP_DEFAULT_TIMEOUT="120"
ARG PIP_RETRIES="8"
ARG SING_BOX_VERSION="1.13.19"
ARG SING_BOX_SHA256="ef88a9e577d474210867bd708933d042e9b70106529df2656182c9db90106aa1"

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

RUN --mount=type=cache,target=/root/.cache/sing-box,sharing=locked \
    set -eux; \
    cache_archive="/root/.cache/sing-box/sing-box-${SING_BOX_VERSION}-linux-amd64.tar.gz"; \
    directory="/tmp/sing-box-${SING_BOX_VERSION}-linux-amd64"; \
    url="https://github.com/SagerNet/sing-box/releases/download/v${SING_BOX_VERSION}/sing-box-${SING_BOX_VERSION}-linux-amd64.tar.gz"; \
    if [ ! -s "$cache_archive" ] || ! echo "${SING_BOX_SHA256}  ${cache_archive}" | sha256sum -c - >/dev/null 2>&1; then \
      rm -f "$cache_archive"; \
      curl -fL --retry 8 --retry-all-errors --retry-delay 2 --connect-timeout 15 --max-time 1500 "$url" -o "$cache_archive"; \
    fi; \
    echo "${SING_BOX_SHA256}  ${cache_archive}" | sha256sum -c -; \
    tar -xzf "$cache_archive" -C /tmp; \
    install -m 0755 "${directory}/sing-box" /usr/local/bin/sing-box; \
    /usr/local/bin/sing-box version; \
    rm -rf "$directory"

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

COPY app.py bootstrap.py bot_client_shop_binding.py bot_update_cache.py bot_update_prefetch.py bot_update_progress.py bot_update_push.py bot_web_console.py bot_web_ai_model_settings.py bot_web_auto_reply_rules.py bot_web_bot_enabled.py bot_web_admin.py bot_web_bot_qa.py bot_web_bot_qa_logic.py bot_web_conversation_knowledge.py bot_web_settings_ack.py chat2api_runtime_guard.py client_data_backup.py console_cache_guard.py deep_test_guard.py github_vless_proxy.py message_processing_traces.py knowledge_v2_sync.py manual_recharge.py runtime_shop_ai_proxy.py store_rule_sync.py recharge_status_query.py runtime_routing_guard.py runtime_streaming_guard.py runtime_embedding_guard.py runtime_ocr.py runtime_ocr_priority.py scheduled_deep_test_retry.py version_update_admin.py wecom_bridge.py wecom_crypto.py wecom_settings.py wecom_policy_migration.py ./
COPY static ./static

RUN useradd --create-home --uid 10001 appuser \
    && mkdir -p /data \
    && chown -R appuser:appuser /app /data

USER appuser
EXPOSE 8080
VOLUME ["/data"]

HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
  CMD curl -fsS http://127.0.0.1:8080/healthz || exit 1

CMD ["python", "bootstrap.py"]
