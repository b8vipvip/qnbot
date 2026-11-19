# ShopKey Reception Profile v2

This file documents the private per-shop configuration edited from Knowledge Center → 接待规则. The runtime file is stored under the current stable ShopKey's private rules directory as `kugou-reception-rules.json`; it is not a repository asset and must not be copied between shops.

## Safety and rollout

- A missing profile is equivalent to a disabled profile (默认关闭). The shipped/default profile contains no product IDs, knowledge IDs, tutorial URL, visual rules or business prompt.
- `enabled` defaults to `false`. Enabling requires at least one verified `product_ids` value and a manually reviewed `policy_prompt`.
- Profile reads and writes require a stable SellerId and exact ShopKey. Nickname-only or fallback identities fail closed.
- `enable_auto_guidance` requires an enabled profile and at least one visual rule.
- `enable_phone_wait` requires an enabled profile, required/any cue lists and a non-empty `phone_wait_reason`.
- `manual_review_required` must remain `true`; `automatic_recharge_enabled` must remain `false`. The validator rejects attempts to change either value.
- Saving creates a backup and uses an atomic write. Restore validates the backup before replacing the current file.
- Do not put customer messages, account credentials, payment details, tokens or other personal data in this file.

## Schema fields

| Field | Meaning |
|---|---|
| `enabled` | Master switch for this shop's profile; defaults to false |
| `product_ids` | Verified product IDs that bind the profile to records in this shop's Knowledge V2 corpus |
| `quarantine_knowledge_ids` | Knowledge record IDs that must not be returned by retrieval |
| `quarantine_text_patterns` | Regex patterns for additional shop-specific retrieval quarantine |
| `blocked_outgoing_patterns` | Regex patterns that suppress unsafe or inaccurate outgoing replies |
| `policy_prompt` | Human-reviewed private business facts and response policy appended to the model context |
| `guide_url` | Optional reviewed HTTPS guide URL; use `{guide_url}` in a visual rule reply to insert it |
| `visual_uncertain_patterns` / `visual_uncertain_reply` | Patterns indicating insufficient or uncertain visual evidence, plus the safe evidence-request reply |
| `visual_rules` | Ordered visual rules; the first matching rule supplies the configured reply, reason and stage |
| `photo_instruction_all_cues` | Every cue in this list must occur in the delivered photo instruction |
| `photo_instruction_any_cues` | At least one cue in this list must occur |
| `photo_instruction_excluded_cues` | If any cue occurs, the photo instruction is not accepted for phone-wait state |
| `phone_wait_reason` | Private customer-facing explanation used when a phone-only reply is treated as waiting for the configured evidence |
| `screenshot_question_cues` / `screenshot_reply_pattern` | Guard for mismatched replies to screenshot questions |
| `enable_auto_guidance` / `enable_phone_wait` | Explicit feature switches; both default to false |
| `manual_review_required` / `automatic_recharge_enabled` | Immutable safety fields: true / false |

Regex fields are compiled during validation and have a 100 ms runtime match timeout. A timeout is handled as a safety match: retrieval is quarantined, outgoing content is suppressed, or visual handling requests more evidence.

## Visual rule matching

Each item in `visual_rules` must contain these fields:

- `id`, `reply`, `reason`, `stage`
- `all_cue_groups`: every group must match; within one group, any cue is sufficient.
- `positive_cue_groups`: every group must contain at least one cue that is positively asserted. A nearby negation such as “未/没有/不是/不” prevents the cue from counting as positive.
- `any_cues`: optional; when non-empty, at least one cue must match.
- `none_cues`: optional; any match rejects the rule.
- `none_positive_cues`: optional; any positively asserted cue rejects the rule.

Example structure (replace all sample values with reviewed values from this shop):

```json
{
  "schema": "qnbot.kugou.reception-rules.v2",
  "version": 2,
  "enabled": false,
  "product_ids": [],
  "quarantine_knowledge_ids": [],
  "enable_auto_guidance": false,
  "enable_phone_wait": false,
  "guide_url": "",
  "policy_prompt": "",
  "quarantine_text_patterns": [],
  "blocked_outgoing_patterns": [],
  "visual_uncertain_patterns": [],
  "visual_uncertain_reply": "",
  "visual_rules": [
    {
      "id": "example_visual_rule",
      "all_cue_groups": [["CUE_A", "CUE_B"], ["CUE_C"]],
      "positive_cue_groups": [["POSITIVE_CUE_A", "POSITIVE_CUE_B"]],
      "any_cues": [],
      "none_cues": ["EXCLUDED_CUE"],
      "none_positive_cues": ["NEGATED_OR_CONFLICTING_CUE"],
      "reply": "REVIEWED_REPLY_TEXT {guide_url}",
      "reason": "example_reason",
      "stage": "manual_review"
    }
  ],
  "photo_instruction_all_cues": [],
  "photo_instruction_any_cues": [],
  "photo_instruction_excluded_cues": [],
  "phone_wait_reason": "",
  "screenshot_question_cues": [],
  "screenshot_reply_pattern": "",
  "manual_review_required": true,
  "automatic_recharge_enabled": false,
  "notes": "Private per-ShopKey profile; keep disabled until reviewed."
}
```

The example is deliberately disabled and contains placeholders, not a working merchant configuration. Do not enable it without replacing the placeholders with verified shop-owned values and testing against the actual shop's message and image examples.

## Legacy v1 migration

A v1 profile is presented as a v2 draft on read. Migration:
- keeps the profile disabled;
- resets auto-guidance and phone-wait to false;
- preserves only an existing valid HTTPS guide URL;
- does not copy the old source-code product IDs, knowledge IDs, visual cues, fixed replies or business prompt into the private profile.

The operator must review and populate the private profile, then save it. The original v1 file is backed up (备份) by the first save. Do not assume the migrated draft is production-ready.
