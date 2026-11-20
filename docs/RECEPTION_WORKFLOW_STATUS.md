# Reception workflow engineering status

This document describes a generic, shop-scoped checkout and account-evidence flow. It contains no actual merchant URL, vendor branding, product identifiers, or buyer messages.

## Authorization boundary

Order created, payment confirmed, device eligibility, personal account binding, final fulfillment authorization, and delivery completion are **separate assertions**. No one stage implies the next.

## Workflow example

| Stage | Evidence needed | Allowed next step |
|---|---|---|
| `awaiting_order` | No authoritative order | Provide generic catalog guidance |
| `awaiting_evidence` | Paid order, missing required screenshot | Ask once, then WAIT |
| `account_review` | Current conversation image + OCR and provenance | Validate or ask for clearer evidence |
| `awaiting_binding` | Confirmed supported app but missing owner binding | Guide from per-shop private guide URL |
| `pending_order_review` | Binding verified by authoritative signal | Recheck current order; no automatic fulfillment |
| `human_handoff` | Manual takeover, unknown platform, or conflicting evidence | Do not send stale AI answer |
| `completed` | Confirmed final delivery event | Customer support only |

The planned generic model uses `shop_key`, `product_id`, `sku`, `flow_version`, `stage`, `required_evidence`, `guard`, `action`, `next_stage`, and idempotent audit events.

## Runtime protection already implemented

Current implementation includes:

- Shop-scoped knowledge quarantine, pre-send text/vision safety checks, a bounded recent-image follow-up window, conservative OCR decisions, and cautious learning candidates. Learning candidates do not automatically authorize financial or account operations.
- A privacy-minimized, cross-restart evidence ledger. It stores hashes and structured observations rather than raw chat, OCR, image URLs, phone numbers, or order IDs; writes are serialized per shop and retained for seven days (maximum 500 rows).
- A shop-scoped, versioned workflow draft catalog with strict schema validation, cross-process atomic writes, backup, and restore. Every saved flow remains `draft`; the catalog cannot activate or execute a workflow.

## Remaining approval and runtime gaps

- A trusted operator identity and authorization source for approval. A UI click or a local draft edit is not proof of an authorized approver.
- An auditable approval record bound to the exact ShopKey, flow ID/version, approved evidence requirements, approver identity, and approval time.
- Controlled publication of an approved immutable version, an active-version pointer, and an atomic rollback to the prior approved version. These operations are not implemented; the current catalog must remain draft-only.
- Trusted external order/account truth and guarded, idempotent execution of irreversible operations. The evidence ledger is diagnostic and must never be treated as order/payment authority.
- UI for reviewing learning proposals and editing/reviewing actual state transitions.
- Complete migration of legacy knowledge with human-approved conflict resolution.
- Field acceptance of real-world client sessions, customer images, manual takeover, and recovery after a real process restart.

Never declare the project completed or auto-merge high-risk changes based solely on unit tests. Perform a shadow evaluation, manual review, and canary rollout first.

## Public repository privacy

No real company name, SKU, shop key, tutorial hostname, product URL, customer account, or order must appear in publicly versioned documentation. Set merchant details using private per-shop runtime configuration instead.
