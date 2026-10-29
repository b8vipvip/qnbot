"""Security and bounded-reading tests for administrator runtime diagnostics."""
from pathlib import Path

import pytest

import server_runtime_logs as logs


def test_redact_sensitive_data_without_lying_about_build_output():
    value = (
        "authorization: Bearer very-secret-string "
        "api_key=secretABC password=123456 "
        "验证码：123456 充值码: x "
        "手机号 13812345678 "
        "https://example.invalid/health?token=1234"
    )
    result = logs.redact(value)
    assert "very-secret-string" not in result
    assert "secretABC" not in result
    assert "13812345678" not in result
    assert "验证码：123456" not in result
    assert "token=1234" not in result
    assert "https://example.invalid/health" in result


def test_private_runtime_log_reads_last_lines_and_rejects_unlisted_source(tmp_path, monkeypatch):
    path = tmp_path / "runtime.log"
    path.write_text("\n".join(f"event-{i}" for i in range(25)) + "\n", encoding="utf-8")
    monkeypatch.setattr(logs, "RUNTIME_FILE", path)
    result = logs._read("runtime", 10)
    assert result["available"] is True
    assert len(result["lines"]) == 10
    assert result["lines"][-1] == "event-24"
    assert all("event-0" not in line for line in result["lines"])
    with pytest.raises(Exception):
        logs._read("../.env", 10)


def test_build_tail_shows_failures_without_exposing_secrets(tmp_path, monkeypatch):
    path = tmp_path / "update.log"
    path.write_text(
        "[2026] 构建新镜像，第 2/3 次\n"
        "#10 pip install ...\n"
        "#10 ERROR: Read timed out\n"
        "token=private-password\n",
        encoding="utf-8",
    )
    monkeypatch.setattr(logs, "UPDATE_FILE", path)
    result = logs._read("build", 50)
    lines = "\n".join(result["lines"])
    assert "第 2/3 次" in lines
    assert "Read timed out" in lines
    assert "private-password" not in lines
