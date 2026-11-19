# Client Business Policy JSON

The packaged `default-business-policy.json` is a **neutral template**, not a merchant profile. It contains no merchant-specific product IDs, knowledge IDs, tutorial URLs, prompts, fixed replies, handoff exceptions, or workflow phrases. Merchant-specific regex entries are empty and resolve to no-op matchers. Five universal privacy/channel detectors (phone, verification-code, phone/code request cues, and image-message marker) remain enabled; the generic runtime does not assume a particular store, product, device, payment method, or fulfillment flow.

## Per-shop storage and migration

When a verified `ShopSettingsScope` is active, the runtime policy is stored as `business-policy.json` under that ShopKey's private rules directory. Configure each shop independently and do not copy one shop's file to another.

- Existing per-shop policy files are left untouched by application upgrades.
- If a shop has no private file and exactly one shop is registered, the service may adopt the existing legacy global `business-policy.json` into that shop's private directory.
- In a multi-shop installation, the legacy global file is not automatically copied to any shop; this prevents one shop's business rules from leaking into another.
- If no verified `ShopSettingsScope` is available, runtime reads use only the packaged neutral template; the legacy global file is never a runtime fallback. Saving requires an explicit verified shop scope.
- A new shop with no existing private or eligible legacy file receives the neutral packaged template.
- **Restore default** replaces the current shop's policy with the neutral template after backing up the existing file. Review the result before enabling any business-specific workflow.

Open the editor from:

```text
知识库 → 店铺规则中心 → 运行策略JSON
```

The editor supports import, export, formatting, default restore, regex validation, automatic backup and live reload. A successful save becomes active within about two seconds without restarting the Bot.

## Sections

- `patterns`: optional deterministic message and answer regexes. An empty string disables that matcher.
- `stages`: configured stage names and next actions. The default only names the generic `一般咨询` state.
- `facts`: shop-specific facts supplied to the reply state.
- `buyerGoals`: shop-specific intent descriptions for short contextual messages.
- `prompts`: high-priority workflow boundaries supplied to the model.
- `validationIssues`: shop-specific pre-send validation messages.
- `handoffOverrides`: narrowly scoped business exceptions applied before the remote handoff compatibility layer.

## Safety boundary

Only reviewed, shop-owned business knowledge and workflow wording belongs in each private policy. Cross-buyer send protection, authentication, token secrecy, message deduplication, file validation, privacy redaction, updater integrity checks, mandatory human review and other engineering safety controls remain in code.

Invalid JSON or malformed non-empty regex is rejected before save. Empty regex values are valid and mean “disabled”; the runtime maps them to a never-match expression. The previous file is backed up before replacement.

## Synthetic configuration examples

These examples describe configurable behavior only; they are not included in the packaged default and do not represent a real merchant.

A shop may choose to distinguish a request for a page capture from a request to select a product:

```text
Which page should I capture?
Which product link or SKU should I choose?
```

A shop may configure a handoff override that allows ordinary account-capability questions while keeping password, verification-code, recovery, theft, or account-security issues with a human operator. No such exception is enabled by default.

## Regression and rollout

1. Import or enter only the current shop's verified rules.
2. Validate the patterns and review all prompts and fixed replies.
3. Test with synthetic examples and then shadow-evaluate against that shop's real conversations.
4. Keep high-risk account, payment, refund, privacy and fulfillment actions under the mandatory human-review gate.
5. Do not treat saving a policy as authorization to send a message, modify an order, recharge, refund, or update production knowledge.
