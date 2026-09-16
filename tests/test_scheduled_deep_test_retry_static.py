from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
SERVICE = ROOT / "services" / "api-control-plane"


def test_failed_scheduled_deep_tests_retry_every_ten_minutes_until_recovery():
    source = (SERVICE / "scheduled_deep_test_retry.py").read_text(encoding="utf-8")
    assert 'FAILED_TEXT_STATUS = "不可用：没有模型通过文本调用测试"' in source
    assert 'SCHEDULED_DEEP_TEST_RETRY_MINUTES", "10"' in source
    assert 'latest_run.get("mode") != "scheduled"' in source
    assert 'latest_run.get("status") != "completed"' in source
    assert 'provider.get("auto_test_enabled")' in source
    assert 'provider.get("enabled")' in source
    assert 'options["auto_apply_results"] = True' in source
    assert 'create_test_run(provider_id, "scheduled", options)' in source
    assert 'args=(control_plane, provider_id, options, run_id)' in source


def test_retry_scheduler_is_registered_and_shipped_in_server_package():
    bootstrap = (SERVICE / "bootstrap.py").read_text(encoding="utf-8")
    dockerfile = (SERVICE / "Dockerfile").read_text(encoding="utf-8")
    workflow = (ROOT / ".github" / "workflows" / "api-control-plane-ci.yml").read_text(encoding="utf-8")
    env_example = (SERVICE / ".env.example").read_text(encoding="utf-8")

    assert "import scheduled_deep_test_retry" in bootstrap
    assert "scheduled_deep_test_retry.install(control_plane)" in bootstrap
    assert "scheduled_deep_test_retry.py" in dockerfile
    assert "scheduled_deep_test_retry.py" in workflow
    assert "services/api-control-plane/tests/**" in workflow
    assert "SCHEDULED_DEEP_TEST_RETRY_MINUTES=10" in env_example
    assert "SCHEDULED_DEEP_TEST_RETRY_POLL_SECONDS=60" in env_example
