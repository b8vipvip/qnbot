# Avoiding blocked sing-box downloads during control-plane upgrades

The host update reuses the **already-running API control-plane container image** as a trusted sing-box source when it contains `sing-box 1.13.19`. The update script only reads its version and tags the immutable local Docker image as `qnbot-sing-box-source:1.13.19`. This does **not** stop the old container or copy any merchant data, and the new Dockerfile copies only `/usr/local/bin/sing-box` from this image.

If no verified local source is present, Docker BuildKit falls back to the upstream official `ghcr.io/sagernet/sing-box:v1.13.19` image at a pinned digest. It no longer downloads the release `.tar.gz` with `curl --max-time 1500` during every build. This is important on servers that can reach GitHub SSH over port 443 but cannot reach GitHub Release asset hosts.

**Existing stuck task:** Code changes in GitHub cannot modify a build that has already fetched an older commit. Do not kill Docker or prune live build caches merely to activate this fix. Confirm the current task status and complete/fail outcome before starting a new update from the web console.

**Preflight diagnostics:** In the actual server repository root (as configured by the updater's `REPO_DIR` variable), inspect `services/api-control-plane/data/server-update/status.json` and `update.log`. Verify current service binary using `docker exec qianniu-api-control-plane /usr/local/bin/sing-box version`. The next update should log "复用正在运行的旧服务镜像中的 sing-box" and Docker BuildKit should show `sing-box-provider` from the local image, not GitHub Release curl.

If local image is missing, the pinned official GHCR fallback still requires network; it can fail before the old service stops. Keep normal backup and health-check rollback rules. No third-party mirror or auto-bypass of VLESS verification is permitted.
