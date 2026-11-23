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
    assert 'gh api "repos/$REPOSITORY/pulls/$PR_NUMBER/merge" --method PUT' in executor



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
    assert 'release_payload="$(jq -n --arg ref "$DEFAULT_BRANCH" --arg source_sha "$MERGE_SHA"' in reconcile
    assert 'actions/workflows/${workflow_ref}/dispatches' in read(".github/gptauto/dispatch_workflow.sh")
    assert 'dispatch_workflow "$REPOSITORY" "$RELEASE_WORKFLOW_ID" "$release_payload"' in reconcile
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
        reconcile.index("- name: Publish terminal foreground receipt")
    ]
    receipt = reconcile[
        reconcile.index("- name: Publish terminal foreground receipt"):
        reconcile.index("- uses: ./.github/actions/upload-gptauto-log")
    ]
    upload = reconcile[
        reconcile.index("- uses: ./.github/actions/upload-gptauto-log"):
        reconcile.index("- name: Reopen lease if terminal persistence failed")
    ]
    rollback = reconcile[
        reconcile.index("- name: Reopen lease if terminal persistence failed"):
        reconcile.index("- name: Terminal receipt summary")
    ]
    summary = reconcile[reconcile.index("- name: Terminal receipt summary"):]

    assert "steps.validation-plan.outcome == 'success'" in terminal
    assert "steps.gates.outcome == 'success'" in terminal
    assert "steps.release-proof.outcome == 'success'" in terminal
    assert 'VALIDATION_WORKFLOW: ${{ steps.validation-plan.outputs.validation-workflow }}' in terminal
    assert '[[ -n "$MAIN_CI_RUN_ID" && -n "$VALIDATION_WORKFLOW" ]]' in terminal
    assert "validation_head_sha=" in terminal and "'.head_sha // \"\"'" in terminal
    assert "validation_name=" in terminal and "'.name // \"\"'" in terminal
    assert "validation_conclusion=" in terminal and "'.conclusion // \"\"'" in terminal
    assert '"$validation_head_sha" == "$MERGE_SHA"' in terminal
    assert '"$validation_name" == "$VALIDATION_WORKFLOW"' in terminal










    assert '--pr-ci-status "$pr_ci_status"' in terminal
    assert "pr_gate_rows=" in terminal
    assert 'select(.head_sha == $head)' in terminal
    assert 'select(.event == "pull_request")' in terminal
    assert 'select(.name == "API control plane CI" or .name == "CI" or .name == "Windows x64 release build")' in terminal
    assert "| group_by(.name)" in terminal
    assert "pr_ci_invalid_rows=" in terminal
    assert '| select(.conclusion != "skipped")' in terminal
    assert 'select(.status != "completed" or .conclusion != "success")' in terminal
    assert '[.[].id | tostring] | join(",")' in terminal
    assert 'pr_ci_status="completed"' in terminal
    assert 'pr_ci_conclusion="success"' in terminal
    assert "canonical post-merge workflow" in terminal
    assert 'pr_ci_run_id="$(jq -r' in terminal
    assert "steps.terminal.outcome == 'success'" in receipt
    assert "steps.terminal.outcome == 'success'" in upload
    assert "steps.lease-receipt.outcome == 'success'" in upload
    assert "failure() && steps.terminal.outcome == 'success'" in rollback
    assert "Completion Lease is ACTIVE" in rollback
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



def test_merged_tasks_cannot_become_terminal_without_reconcile_provenance():
    observer = read(".github/gptauto/observer.py")
    orchestrator = read(".github/gptauto/orchestrator.py")

    assert 'evidence_provenance == "reconciler"' in observer
    assert "terminal_reconcile_evidence" in observer
    assert '"terminal_done_provenance": "reconciler" if terminal_reconcile_evidence else ""' in observer
    assert 'str(task.metadata.get("terminal_done_provenance") or "") == "reconciler"' in orchestrator
    assert 'str(task.metadata.get("terminal_done_merge_sha") or "") == merge_sha' in orchestrator
    assert 'bool(str(task.metadata.get("terminal_done_pr_ci_run_id") or "").strip())' in orchestrator
    assert 'return "RECONCILE"' in orchestrator
    assert '"WAITING_GITHUB", "RECONCILE"' in orchestrator


def test_terminal_reconcile_receipt_is_mandatory_and_verified_before_state_upload():
    reconcile = read(".github/workflows/gptauto-reconcile.yml")
    receipt_start = reconcile.index("- name: Publish terminal foreground receipt")
    upload_start = reconcile.index("- uses: ./.github/actions/upload-gptauto-log")
    rollback_start = reconcile.index("- name: Reopen lease if terminal persistence failed")
    summary_start = reconcile.index("- name: Terminal receipt summary")
    receipt = reconcile[receipt_start:summary_start]

    assert receipt_start < upload_start < rollback_start < summary_start
    assert "continue-on-error: true" not in reconcile[receipt_start:upload_start]
    assert "steps.lease-receipt.outcome == 'success'" in reconcile[upload_start:rollback_start]
    assert "gh api --paginate" in receipt
    assert "verified_comments=" in receipt
    assert "Completion Lease is RELEASED" in receipt
    assert "Terminal state: DONE" in receipt
    assert "Completion Lease comment was not durably updated to RELEASED" in receipt
    assert "failure() && steps.terminal.outcome == 'success'" in receipt
    assert "Unable to restore active Completion Lease" in receipt



def test_reconcile_cannot_bypass_required_pr_ci_and_executor_waits_for_it():
    observer = read(".github/gptauto/observer.py")
    orchestrator = read(".github/gptauto/orchestrator.py")
    executor = read(".github/gptauto/executor.py")

    assert 'bool(str(pr_ci_run_id or "").strip())' in observer
    assert 'str(pr_ci_status or "").strip().lower() == "completed"' in observer
    assert '"terminal_done_pr_ci_run_id": str(pr_ci_run_id or "") if terminal_reconcile_evidence else ""' in observer
    assert "if pr_ci and pr_ci.required and (" in orchestrator
    assert 'not str(task.metadata.get("pr_ci_run_id") or "").strip()' in orchestrator
    assert 'return "PR_CI"' in orchestrator
    assert '"PR_CI", "POST_MERGE_CI"' in orchestrator
    assert 'authority["phase"] == "PR_CI"' in executor
    assert "Waiting for the required exact-head PR CI success evidence" in executor



def test_pr_ci_workflow_run_finishing_after_merge_still_updates_pr_ci_gate():
    observer = read(".github/gptauto/observer.py")
    done = observer[
        observer.index("pr_ci_done ="):
        observer.index("# A Draft PR")
    ]
    failed = done[done.index("pr_ci_failed ="):]
    effective = observer[
        observer.index("effective_pr_ci_run_id ="):
        observer.index("plan = [")
    ]

    assert "and not merged" not in done
    assert "and not merged" not in failed
    assert "elif pr_number and branch != default_branch and not _release_workflow(workflow_name):" in effective
    assert 'effective_pr_ci_run_id = effective_pr_ci_run_id or str(run_id or "")' in effective

def test_reconcile_can_recover_a_historical_merge_only_with_exact_sha_success_evidence():
    """Do not lose a completed merge when later master commits exist."""
    reconcile = read(".github/workflows/gptauto-reconcile.yml")
    validation = reconcile[
        reconcile.index("- name: Resolve and dispatch canonical post-merge validation gate"):
        reconcile.index("- name: Wait for terminal post-merge validation gates")
    ]

    assert 'runs_before="$(gh api "repos/$REPOSITORY/actions/runs?head_sha=$MERGE_SHA&per_page=100")"' in validation
    assert "existing_success=" in validation
    assert 'if [[ "$current_main" != "$MERGE_SHA" ]]; then' in validation
    assert 'git merge-base --is-ancestor "$MERGE_SHA" "$current_main"' in validation
    assert '[[ -n "$existing_success" ]]' in validation
    assert "no successful exact-SHA validation exists" in validation
    assert "reusing exact-SHA validation run" in validation
    assert 'primary_workflow_file="${primary_path##*/}"' in validation
    assert 'actions/workflows/${workflow_ref}/dispatches' in read(".github/gptauto/dispatch_workflow.sh")
    assert 'dispatch_workflow "$REPOSITORY" "$primary_workflow_file" "$validation_payload"' in validation
    assert 'gh workflow run "$primary_path"' not in validation



def test_skipped_or_neutral_pr_workflows_cannot_satisfy_product_validation():
    observer = read(".github/workflows/gptauto-observer.yml")
    reconcile = read(".github/workflows/gptauto-reconcile.yml")

    for workflow in (observer, reconcile):
        assert '| select(.conclusion != "skipped")' in workflow
        assert '.conclusion != "success"' in workflow
    assert 'and .conclusion != "neutral"' not in observer
    assert 'and .conclusion != "neutral"' not in reconcile

def test_reconcile_terminal_state_requires_versioned_post_merge_and_release_receipts():
    observer = read(".github/gptauto/observer.py")
    orchestrator = read(".github/gptauto/orchestrator.py")
    terminal = orchestrator[
        orchestrator.index("def canonical_phase"):
        orchestrator.index("def canonicalize_task")
    ]

    for field in (
        "terminal_done_main_ci_run_id",
        "terminal_done_release_required",
        "terminal_done_release_published",
        "terminal_done_release_run_id",
        "terminal_done_release_id",
        "terminal_done_release_tag",
        "terminal_done_reconcile_run_id",
        "terminal_done_evidence_version",
    ):
        assert '"' + field + '"' in observer
        assert 'task.metadata.get("' + field + '")' in terminal

    assert "terminal_evidence_version < 2" in terminal
    assert "except (TypeError, ValueError)" in terminal
    assert "Malformed version metadata is not legacy evidence" in terminal
    assert "versioned_terminal_evidence_complete" in terminal
    assert "task.metadata.get(\"terminal_done_release_published\") is True" in terminal
    assert "bool(str(task.metadata.get(\"terminal_done_main_ci_run_id\") or \"\").strip())" in terminal


def test_failed_reconcile_dispatch_marker_does_not_block_terminal_recovery():
    executor = read(".github/workflows/gptauto-executor.yml")
    adoption = executor[
        executor.index("- name: Adopt externally merged completion lease"):
        executor.index("- name:", executor.index("- name: Adopt externally merged completion lease") + 10)
    ]

    # A historical dispatch marker is not proof that Reconcile reached DONE.
    assert 'completion_marker="<!-- gptauto-completion-lease:$TASK_ID -->"' in adoption
    assert 'grep -Fq "Completion Lease is RELEASED"' in adoption
    assert 'grep -Fq "Terminal state: DONE"' in adoption
    assert 'grep -Fq "Merge SHA: $MERGE_SHA"' in adoption
    assert 'actions/workflows/${workflow_ref}/dispatches' in read(".github/gptauto/dispatch_workflow.sh")
    assert 'dispatch_workflow "$REPOSITORY" "gptauto-reconcile.yml" "$reconcile_payload"' in adoption
    assert "gh workflow run gptauto-reconcile.yml" not in adoption
    assert 'gh api "repos/$REPOSITORY/issues/comments/$handoff_id" --method PATCH' in adoption
    assert 'if grep -Fq "$marker" <<<"$existing"; then' not in adoption
    assert "dispatch lease reserved at" in adoption
    assert "This recent marker suppresses duplicate dispatch" in adoption




def test_reconcile_run_title_includes_task_identity_for_deduplication():
    reconcile = read(".github/workflows/gptauto-reconcile.yml")
    observer = read(".github/workflows/gptauto-observer.yml")

    assert 'run-name: "GPTAuto Reconcile (${{ inputs.task_id }} / PR #${{ inputs.pr_number }})"' in reconcile
    assert "- 'docs/QIANNIU_CHAT_AUTOMATION_PROGRESS.md'" in observer
    assert "- 'docs/PROJECT_HANDOFF_CONTEXT.md'" in observer


def test_api_ci_runs_for_bot_source_and_release_skips_superseded_exact_sha():
    api_ci = read(".github/workflows/api-control-plane-ci.yml")
    publish = read(".github/workflows/publish-bot-auto-update-release.yml")

    assert api_ci.count("src/Bot/**") >= 2
    assert api_ci.count("src/BotLib/**") >= 2
    assert api_ci.count("src/DbEntity/**") >= 2
    assert api_ci.count("src/Directory.Build.props") >= 2
    assert "latest_master_sha" in publish
    assert 'select(.name == "API control plane CI" and .head_branch == "master" and .head_sha == $sha)' in publish
    assert 'select(.name == "Windows x64 release build" and .head_branch == "master" and .head_sha == $sha)' in publish
    assert 'echo "superseded=true" >> "$GITHUB_OUTPUT"' in publish
    assert "steps.api-ci.outputs.superseded != 'true'" in publish
    assert "Skip superseded release candidate" in publish

def test_executor_deduplicates_active_reconcile_runs_before_dispatch():
    executor = read(".github/workflows/gptauto-executor.yml")
    merge = executor[
        executor.index("- name: Execute safe merge gate"):
        executor.index("- name: Adopt externally merged completion lease")
    ]
    adoption = executor[
        executor.index("- name: Adopt externally merged completion lease"):
        executor.index("- name: Retry transient failed jobs only")
    ]

    for section in (merge, adoption):
        assert 'reconcile_title="GPTAuto Reconcile ($TASK_ID / PR #$PR_NUMBER)"' in section
        assert 'actions/workflows/gptauto-reconcile.yml/runs?per_page=100' in section
        assert 'select(.display_title == $title and .status != "completed")' in section
        assert 'if [[ -n "$active_reconcile_run" ]]' in section
        assert 'exit 0' in section

def test_reconcile_adoption_debounces_recent_handoff_when_run_list_is_eventually_consistent():
    executor = read(".github/workflows/gptauto-executor.yml")
    adoption = executor[
        executor.index("- name: Adopt externally merged completion lease"):
        executor.index("- name: Retry transient failed jobs only")
    ]

    assert 'handoff_updated_at="$(jq -r --arg marker "$marker"' in adoption
    assert 'date -u -d "$handoff_updated_at" +%s' in adoption
    assert "if (( handoff_age < 600 )); then" in adoption
    assert "deferring recovery dispatch to avoid an eventual-consistency duplicate" in adoption
    assert "Existing Reconcile handoff timestamp is unreadable; suppressing duplicate dispatch" in adoption
    assert adoption.index("active_reconcile_run") < adoption.index("handoff_updated_at") < adoption.index('dispatch_workflow "$REPOSITORY" "gptauto-reconcile.yml" "$reconcile_payload"')

def test_reconcile_recovery_persists_dispatch_lease_before_workflow_dispatch():
    executor = read(".github/workflows/gptauto-executor.yml")
    adoption = executor[
        executor.index("- name: Adopt externally merged completion lease"):
        executor.index("- name: Retry transient failed jobs only")
    ]

    lease = adoption.index('recovery_body="$marker')
    dispatch = adoption.index('dispatch_workflow "$REPOSITORY" "gptauto-reconcile.yml" "$reconcile_payload"')
    scheduled = adoption.index('scheduled_body="$marker')
    assert lease < adoption.index('gh api "repos/$REPOSITORY/issues/comments/$handoff_id" --method PATCH') < dispatch
    assert lease < adoption.index('gh api "repos/$REPOSITORY/issues/$PR_NUMBER/comments" --method POST') < dispatch
    assert dispatch < scheduled
    assert "dispatch lease reserved at $(date -u +%Y-%m-%dT%H:%M:%SZ)" in adoption


def test_all_gptauto_workflow_dispatches_use_rest_json_payloads_not_cli_positional_args():
    executor = read(".github/workflows/gptauto-executor.yml")
    reconcile = read(".github/workflows/gptauto-reconcile.yml")
    observer = read(".github/workflows/gptauto-observer.yml")
    assert 'dispatch_workflow "$REPOSITORY" "gptauto-reconcile.yml" "$reconcile_payload"' in executor
    assert 'dispatch_workflow "$REPOSITORY" "gptauto-repair.yml" "$repair_payload"' in executor
    assert 'dispatch_workflow "$REPOSITORY" "gptauto-observer.yml" "$observer_payload"' in observer
    assert 'dispatch_workflow "$REPOSITORY" "$primary_workflow_file" "$validation_payload"' in reconcile
    assert 'dispatch_workflow "$REPOSITORY" "$RELEASE_WORKFLOW_ID" "$release_payload"' in reconcile
    helper = read(".github/gptauto/dispatch_workflow.sh")
    for workflow in (executor, reconcile, observer):
        assert "gh workflow run" not in workflow
        assert "--input -" not in workflow
        assert "dispatch_workflow" in workflow
    assert "curl --silent --show-error" in helper
    assert 'Authorization: Bearer $token' in helper
    assert 'if [[ "$status" != "204" ]]' in helper
    assert 'GITHUB_API_URL:-https://api.github.com' in helper




def test_manual_reconcile_comment_ingress_is_owner_only_and_requires_exact_merged_task():
    observer = read(".github/workflows/gptauto-observer.yml")
    assert "issue_comment:" in observer
    assert '"$COMMENT_AUTHOR" == "$REPOSITORY_OWNER"' in observer
    assert '"$COMMENT_BODY" =~ ^/gptauto-reconcile' in observer
    assert "GA-[a-f0-9]{12}" in observer
    assert "github.event.issue.number" in observer
    assert '"$requested_task_id" == "$TASK_ID"' in observer
    assert 'PR_MERGED" == "true"' in observer
    assert "Manual Reconcile requires a merged pull request." in observer
    assert "Manual Reconcile task ID does not match the PR-derived canonical task ID." in observer



def test_manual_reconcile_comment_forces_executor_even_when_observation_is_unchanged():
    observer = read(".github/gptauto/observer.py")
    assert 'event in {"workflow_dispatch", "issue_comment"}' in observer
    assert "and not (previous_terminal_done and authority[\"terminal_done\"])" in observer



def test_reconcile_reuses_exact_sha_api_ci_when_default_branch_has_advanced():
    reconcile = read(".github/workflows/gptauto-reconcile.yml")
    validation = reconcile[reconcile.index("id: validation-plan"):reconcile.index("- name: Wait for terminal post-merge validation gates")]
    assert 'if [[ "$current_main" != "$MERGE_SHA" && -z "$existing_success"' in validation
    assert '"$primary" == "CI" || "$primary" == "Build and Test"' in validation
    assert 'select(.name == "API control plane CI" and' in validation
    assert 'primary="API control plane CI"' in validation
    assert 'Reusing exact-SHA API control-plane CI run' in validation
    assert 'Default branch advanced to $current_main and no successful exact-SHA validation exists' in validation



def test_github_cli_mutations_put_endpoint_before_method_flags():
    executor = read(".github/workflows/gptauto-executor.yml")
    observer = read(".github/workflows/gptauto-observer.yml")
    reconcile = read(".github/workflows/gptauto-reconcile.yml")
    repair = read(".github/workflows/gptauto-repair.yml")
    for workflow in (executor, observer, reconcile, repair):
        assert "gh api --method " not in workflow
    assert 'gh api "repos/$REPOSITORY/issues/comments/$handoff_id" --method PATCH' in executor
    assert 'gh api "repos/$REPOSITORY/issues/$PR_NUMBER/comments" --method POST' in reconcile
