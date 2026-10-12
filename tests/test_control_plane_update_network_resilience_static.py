from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_update_script_reuses_base_image_retries_build_and_does_not_rebuild_on_start():
    script = read("scripts/update-api-control-plane.sh")

    assert "prepare_base_image" in script
    assert "CONTROL_PLANE_BUILD_RETRIES" in script
    assert "CONTROL_PLANE_BUILD_TIMEOUT_SECONDS" in script
    assert "timeout --foreground" in script
    assert "保留 BuildKit/apt/pip 缓存" in script
    assert "完整构建日志" in script
    assert "docker compose -f docker-compose.bt.yml up -d --no-build --force-recreate" in script
    assert "docker compose -f docker-compose.bt.yml build --pull" not in script


def test_dockerfile_uses_tunable_mirrors_retries_and_buildkit_caches():
    dockerfile = read("services/api-control-plane/Dockerfile")

    assert "ARG APT_MIRROR" in dockerfile
    assert "ARG APT_SECURITY_MIRROR" in dockerfile
    assert "ARG PIP_INDEX_URL" in dockerfile
    assert "ARG PIP_DEFAULT_TIMEOUT" in dockerfile
    assert "ARG PIP_RETRIES" in dockerfile
    assert "Acquire::Retries=5" in dockerfile
    assert "--mount=type=cache,target=/var/cache/apt" in dockerfile
    assert "--mount=type=cache,target=/root/.cache/pip" in dockerfile
    assert "--retries \"$PIP_RETRIES\"" in dockerfile
    assert "--timeout \"$PIP_DEFAULT_TIMEOUT\"" in dockerfile
    assert "--index-url \"$PIP_INDEX_URL\"" in dockerfile


def test_bt_compose_defaults_to_tencent_cloud_package_mirrors_but_allows_override():
    compose = read("services/api-control-plane/docker-compose.bt.yml")

    assert "CONTROL_PLANE_BUILD_APT_MIRROR" in compose
    assert "https://mirrors.cloud.tencent.com/debian" in compose
    assert "CONTROL_PLANE_BUILD_APT_SECURITY_MIRROR" in compose
    assert "https://mirrors.cloud.tencent.com/debian-security" in compose
    assert "CONTROL_PLANE_BUILD_PIP_INDEX_URL" in compose
    assert "https://mirrors.cloud.tencent.com/pypi/simple" in compose
    assert "CONTROL_PLANE_BUILD_PIP_TIMEOUT:-120" in compose
    assert "CONTROL_PLANE_BUILD_PIP_RETRIES:-8" in compose


def test_update_script_reports_pip_timeout_and_apt_network_failures_explicitly():
    script = read("scripts/update-api-control-plane.sh")

    assert "files\\.pythonhosted\\.org" in script
    assert "ReadTimeoutError" in script
    assert "PyPI/pip 下载超时" in script
    assert "APT/Debian 软件源访问异常" in script
    assert "docker system df" in script


def test_env_reader_does_not_emit_awk_invalid_quote_escape_warning(tmp_path):
    import subprocess

    script = read("scripts/update-api-control-plane.sh")
    start = script.index("read_env_value() {")
    end = script.index("\ncompose() {", start)
    function = script[start:end]
    sample = tmp_path / ".env"
    sample.write_text(
        'PUBLIC_BASE_URL="https://example.test/path"\n'
        "CONTROL_PLANE_BIND_PORT='18081'\n"
        "EMPTY_VALUE=\n",
        encoding="utf-8",
    )
    result = subprocess.run(
        [
            "bash",
            "-c",
            function
            + '\nread_env_value PUBLIC_BASE_URL "$1"\n'
            + 'read_env_value CONTROL_PLANE_BIND_PORT "$1"\n',
            "bash",
            str(sample),
        ],
        text=True,
        capture_output=True,
        check=True,
    )
    assert result.stdout.splitlines() == ["https://example.test/path", "18081"]
    assert result.stderr == ""
