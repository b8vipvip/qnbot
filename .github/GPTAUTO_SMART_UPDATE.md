# GPTAuto smart update policy v1

The GPTAuto source repository owns the classifier at
`gptauto/smart_update.py`. Consumer Sync installs it as
`.github/gptauto/smart_update.py` in registered product repositories.
GPTAuto is the **decision authority for required verification**; the
existing host-only updater is the **sole owner of actual deployment**.

## Modes

| Mode | Meaning |
| --- | --- |
| `no-op` | Running revision equals target revision |
| `checks-only` | Documentation/test/managed GPTAuto changes only |
| `runtime-hotfix` | An explicit allowlist of runtime files; CI required; host-only updater may apply an atomic, reversible overlay |
| `client-build` | Windows client build required; do not treat as server deployment |
| `verified-image` | Full image-level proof and existing release gates required |
| `blocked` | Invalid Git history/diff/classifier; refuse deployment |

The plan is bound to the exact from/target SHA, and all required CI evidence
must be checked against the exact *target* SHA. File suffixes alone never
authorize production rollout: projects must perform dependency, active-image,
ownership, provenance, health and rollback checks before applying overlays.

`mv3c` continues to require a published, attested Release image for runtime
code. Its prebuilt/image/OCI candidate selection is the smart deployment path,
rather than an unaudited overlay that breaks image proof.

The CLI is read-only and fails closed:

```bash
python3 .github/gptauto/smart_update.py \
  --repo chat2api --worktree /opt/chat2api \
  --from-sha "$DEPLOYED_SHA" --target-sha "$TARGET_SHA"
```

Consumer-managed startup integrations should display the selected plan to the
administrator and refuse unknown modes or mismatched SHA/CI evidence.
Manual administrator click is always required for production deployment;
GPTAuto must never dispatch production deployment as a side effect of CI.
