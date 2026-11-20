from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def read(path: str) -> str:
    return (ROOT / path).read_text(encoding="utf-8-sig")


def test_observer_wakes_on_the_actual_pr_validation_workflows():
    observer = read(".github/workflows/gptauto-observer.yml")

    assert "workflows: [CI, API control plane CI, Windows x64 release build]" in observer
    assert 'if [[ "$WORKFLOW_NAME" == "CI" || "$WORKFLOW_NAME" == "API control plane CI" || "$WORKFLOW_NAME" == "Windows x64 release build" ]]; then' in observer
    assert 'select(.event == "pull_request")' in observer
    assert 'select(.name == "API control plane CI" or .name == "CI" or .name == "Windows x64 release build")' in observer


def test_gptauto_workflow_only_master_commits_do_not_create_orphan_observer_tasks():
    observer = read(".github/workflows/gptauto-observer.yml")

    assert "paths-ignore:" in observer
    assert "- '.github/workflows/gptauto-*.yml'" in observer
    assert "- 'tests/test_gptauto_workflow_ingress_static.py'" in observer
    assert "- '.github/gptauto/**'" in observer


def test_executor_defers_draft_prs_without_failing_or_bypassing_the_merge_gate():
    executor = read(".github/workflows/gptauto-executor.yml")

    assert "if [[ \"$(jq -r '.draft' <<<\"$pr\")\" == \"true\" ]]; then" in executor
    assert "merge deferred until required real-client acceptance is recorded" in executor
    assert "[[ \"$actual_head\" == \"$EXPECTED_HEAD\" ]]" in executor
    assert "gh api --method PUT \"repos/$REPOSITORY/pulls/$PR_NUMBER/merge\"" in executor



def test_pr_validation_fingerprint_changes_when_existing_runs_complete():
    observer = read(".github/workflows/gptauto-observer.yml")
    library = read(".github/gptauto/observer.py")

    assert "pr_gate_rows=" in observer
    assert 'select(.name == "API control plane CI" or .name == "CI" or .name == "Windows x64 release build")' in observer
    assert 'PR_CI_STATUS="in_progress"' in observer
    assert 'PR_CI_STATUS="completed"' in observer
    assert '"pr_ci_status": m.get("pr_ci_status") or "",' in library
    assert '"pr_ci_conclusion": m.get("pr_ci_conclusion") or "",' in library


def test_observer_ignores_all_gptauto_control_plane_self_commits():
    observer = read(".github/workflows/gptauto-observer.yml")

    assert "- '.github/gptauto/**'" in observer


def test_draft_pr_becomes_explicit_user_action_after_all_ci_passes():
    workflow = read(".github/workflows/gptauto-observer.yml")
    observer = read(".github/gptauto/observer.py")
    orchestrator = read(".github/gptauto/orchestrator.py")
    executor = read(".github/gptauto/executor.py")
    executor_workflow = read(".github/workflows/gptauto-executor.yml")

    assert "PR_DRAFT=" in workflow
    assert '--pr-draft "$PR_DRAFT"' in workflow
    assert "draft_requires_action = bool(pr_number) and not merged and draft and pr_ci_done" in observer
    assert "if (approval_required or draft_requires_action) and pr_number and not merged:" in observer
    assert "if task.state == State.BLOCKED:" in orchestrator
    assert orchestrator.index("if task.state == State.BLOCKED:") < orchestrator.index("merge = _step(task, Gate.MERGE)")
    assert "draft_pr_requires_real_client_acceptance" in executor
    assert 'blocked-reason=$(jq -r' in executor_workflow
    assert "Complete the required Windows/千牛 real-client acceptance" in executor_workflow
    assert 'elif [[ "$BLOCKED_REASON" == "draft_pr_requires_real_client_acceptance" ]]; then' in executor_workflow


def test_reconcile_discovers_the_actual_release_workflow_and_pins_the_merge_sha():
    reconcile = read(".github/workflows/gptauto-reconcile.yml")
    release = read(".github/workflows/publish-bot-auto-update-release.yml")

    assert 'publish-bot-auto-update-release.yml' in reconcile
    assert 'Publish Bot auto-update release' in reconcile
    assert 'release-path=$release_path' in reconcile
    assert 'release-name=$release_name' in reconcile
    assert 'release-workflow-id=$release_workflow_id' in reconcile
    assert 'RELEASE_PATH: ${{ steps.validation-plan.outputs.release-path }}' in reconcile
    assert 'RELEASE_NAME: ${{ steps.validation-plan.outputs.release-name }}' in reconcile
    assert 'RELEASE_WORKFLOW_ID: ${{ steps.validation-plan.outputs.release-workflow-id }}' in reconcile
    assert '-f source_sha="$MERGE_SHA"' in reconcile
    assert 'select(.display_title == $title' in reconcile
    assert 'select(.name == $workflow and .display_title == $title' not in reconcile
    assert 'baseline="$(jq -r \'[.workflow_runs[].id] | max // 0\' <<<"$runs")"' in reconcile
    assert 'actions/workflows/$RELEASE_WORKFLOW_ID/runs?per_page=100' in reconcile
    assert 'run-name: Publish Bot auto-update release' in release
    assert 'github.event.workflow_run.head_sha || github.event.inputs.source_sha' in release
    assert 'event == "workflow_run"' in reconcile
    assert 'event == "workflow_dispatch"' in reconcile
    assert 'inputs.source_sha' in release
    assert 'SOURCE_SHA: ${{ inputs.source_sha }}' in release
    assert 'head_sha == $source_sha' in release
    assert 'source_sha:' in release
    assert 'timeout-minutes: 60' in reconcile


def test_reconcile_never_releases_completion_lease_without_successful_exact_sha_gates():
    reconcile = read(".github/workflows/gptauto-reconcile.yml")

    terminal = reconcile[
        reconcile.index("- name: Capture terminal DONE evidence directly"):
        reconcile.index("- uses: ./.github/actions/upload-gptauto-log")
    ]
    upload = reconcile[
        reconcile.index("- uses: ./.github/actions/upload-gptauto-log"):
        reconcile.index("- name: Publish terminal foreground receipt")
    ]
    receipt = reconcile[
        reconcile.index("- name: Publish terminal foreground receipt"):
        reconcile.index("- name: Terminal receipt summary")
    ]
    summary = reconcile[reconcile.index("- name: Terminal receipt summary"):]

    assert "steps.validation-plan.outcome == 'success'" in terminal
    assert "steps.gates.outcome == 'success'" in terminal
    assert "steps.release-proof.outcome == 'success'" in terminal
    assert 'VALIDATION_WORKFLOW: ${{ steps.validation-plan.outputs.validation-workflow }}' in terminal
    assert '[[ -n "$MAIN_CI_RUN_ID" && -n "$VALIDATION_WORKFLOW" ]]' in terminal
    assert 'validation_head_sha="$(jq -r '.head_sha // ""' <<<"$validation_row")"' in terminal
    assert 'validation_name="$(jq -r '.name // ""' <<<"$validation_row")"' in terminal
    assert 'validation_conclusion="$(jq -r '.conclusion // ""' <<<"$validation_row")"' in terminal
    assert '"$validation_head_sha" == "$MERGE_SHA"' in terminal
    assert '"$validation_name" == "$VALIDATION_WORKFLOW"' in terminal
    assert "steps.terminal.outcome == 'success'" in upload
    assert "steps.terminal.outcome == 'success'" in receipt
    assert "steps.terminal.outcome == 'success'" in summary


def test_release_waits_for_exact_sha_api_control_plane_ci_before_publishing():
    release = read(".github/workflows/publish-bot-auto-update-release.yml")

    gate = release[
        release.index("- name: Require exact-SHA API control plane CI"):
        release.index("- name: Download complete x64 artifact")
    ]
    artifact = release.index("- name: Download complete x64 artifact")
    api_gate = release.index("- name: Require exact-SHA API control plane CI")
    assert api_gate < artifact
    assert 'steps.build.outputs.head_sha' in gate
    assert 'select(.name == "API control plane CI" and .head_branch == "master" and .head_sha == $source_sha)' in gate
    assert '[[ "$conclusion" == "success" ]]' in gate
    assert 'Timed out waiting for API control plane CI on exact release SHA' in gate
