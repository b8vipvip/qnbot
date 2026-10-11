from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
UPDATER = ROOT / "scripts" / "update-api-control-plane.sh"
BT_COMPOSE = ROOT / "services" / "api-control-plane" / "docker-compose.bt.yml"


def test_disk_preflight_refuses_insufficient_space_before_git_fetch_or_build():
    script = UPDATER.read_text(encoding="utf-8")
    assert 'MIN_FREE_BEFORE_BUILD_MIB="${CONTROL_PLANE_MIN_BUILD_FREE_MIB:-4096}"' in script
    assert 'MIN_FREE_BEFORE_BACKUP_MIB="${CONTROL_PLANE_MIN_BACKUP_FREE_MIB:-2048}"' in script
    assert "check_free_kib()" in script
    assert 'df -Pk "$path"' in script
    assert 'if (( available_kib < minimum_kib )); then' in script
    assert "旧服务未停止" in script
    assert "MIN_FREE_BEFORE_BUILD_MIB MIN_FREE_BEFORE_BACKUP_MIB; do" in script

    preflight = script.index("\ncheck_space_before_build\nlog ")
    fetch = script.index("\ngithub_fetch_with_retry\n", preflight)
    build = script.index("\nbuild_new_image\n", fetch)
    stop_service = script.index('log "停止旧控制面并创建冷备份"', build)
    assert preflight < fetch < build < stop_service


def test_cold_backup_space_accounts_for_persistent_data_and_runs_before_stop():
    script = UPDATER.read_text(encoding="utf-8")
    assert 'source_dir="$SERVICE_DIR/data"' in script
    assert 'source_dir="$LEGACY_DIR/data"' in script
    assert 'du -sk "$source_dir"' in script
    assert 'required_kib=$(( source_kib + MIN_FREE_BEFORE_BACKUP_MIB * 1024 ))' in script
    assert 'check_free_kib "$BACKUP_ROOT" "$required_kib"' in script

    build = script.index("\nbuild_new_image\n")
    backup_preflight = script.index("\ncheck_space_before_backup\n", build)
    stop_service = script.index('log "停止旧控制面并创建冷备份"', backup_preflight)
    stop_container = script.index('docker stop "$CONTAINER_NAME"', stop_service)
    assert build < backup_preflight < stop_service < stop_container


def test_bt_compose_keeps_bounded_json_logs_across_server_updates():
    compose = BT_COMPOSE.read_text(encoding="utf-8")
    assert 'driver: json-file' in compose
    assert 'max-size: "20m"' in compose
    assert 'max-file: "3"' in compose
    assert './data:/data' in compose
