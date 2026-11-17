from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_observer_wakes_on_the_actual_pr_validation_workflows():
    observer = read(".github/workflows/gptauto-observer.yml")

    assert "workflows: [CI, API control plane CI, Windows x64 release build]" in observer
    assert 'if [[ "$WORKFLOW_NAME" == "CI" || "$WORKFLOW_NAME" == "API control plane CI" || "$WORKFLOW_NAME" == "Windows x64 release build" ]]; then' in observer
    assert 'select(.name == "API control plane CI" and .event == "pull_request")' in observer
    assert 'select(.name == "CI" and .event == "pull_request")' in observer
    assert 'select(.name == "Windows x64 release build" and .event == "pull_request")' in observer


def test_gptauto_workflow_only_master_commits_do_not_create_orphan_observer_tasks():
    observer = read(".github/workflows/gptauto-observer.yml")

    assert "paths-ignore:" in observer
    assert "- '.github/workflows/gptauto-*.yml'" in observer
    assert "- 'tests/test_gptauto_workflow_ingress_static.py'" in observer


def test_executor_defers_draft_prs_without_failing_or_bypassing_the_merge_gate():
    executor = read(".github/workflows/gptauto-executor.yml")

    assert "if [[ \"$(jq -r '.draft' <<<\"$pr\")\" == \"true\" ]]; then" in executor
    assert "merge deferred until required real-client acceptance is recorded" in executor
    assert "[[ \"$actual_head\" == \"$EXPECTED_HEAD\" ]]" in executor
    assert "gh api --method PUT \"repos/$REPOSITORY/pulls/$PR_NUMBER/merge\"" in executor
