"""Runtime revision must identify the running container, not only host Git checkout."""
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]

def read(path):
    return (ROOT / path).read_text(encoding="utf-8-sig")

def test_build_stamps_actual_revision_without_invalidating_heavy_dependencies():
    docker = read("services/api-control-plane/Dockerfile")
    compose = read("services/api-control-plane/docker-compose.bt.yml")
    updater = read("scripts/update-api-control-plane.sh")
    assert "ARG CONTROL_PLANE_BUILD_COMMIT=unknown" in docker
    assert "ENV CONTROL_PLANE_BUILD_COMMIT=${CONTROL_PLANE_BUILD_COMMIT}" in docker
    assert "CONTROL_PLANE_BUILD_COMMIT: ${CONTROL_PLANE_BUILD_COMMIT:-unknown}" in compose
    assert 'export CONTROL_PLANE_BUILD_COMMIT="$NEW_COMMIT"' in updater
    assert docker.index("ARG CONTROL_PLANE_BUILD_COMMIT=unknown") > docker.index("COPY app.py")

def test_runtime_revision_is_preferred_to_mutable_updater_file():
    admin = read("services/api-control-plane/version_update_admin.py")
    app = read("services/api-control-plane/app.py")
    index = read("services/api-control-plane/static/index.html")
    js = read("services/api-control-plane/static/version-update.js")
    assert 'def _runtime_build_commit()' in admin
    assert 'return _runtime_build_commit() or str(status.get("current_commit") or "").strip()' in admin
    assert '"running_image_commit": running_image_commit' in admin
    assert '"recorded_commit": recorded_commit' in admin
    assert 'os.getenv("CONTROL_PLANE_BUILD_COMMIT", "unknown")' in app
    assert "当前运行容器提交" in js
    assert "旧镜像未嵌入构建提交" in js
    assert '/static/version-update.js?v=4' in index
