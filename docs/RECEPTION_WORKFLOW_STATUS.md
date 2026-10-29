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

Current implementation includes shop-scoped knowledge quarantine, pre-send text/vision safety checks, a bounded recent-image follow-up window, conservative OCR decisions, short-lived audit and cautious five-minute learning candidates. Learning candidates do not automatically authorize financial or account operations.

## Known gaps

- Durable cross-restart evidence ledger and generic versioned flow engine.
- Dynamic shop/product/SKU flow approval, with operator review.
- UI for editing actual state transitions and reviewing learning proposals.
- Trusted external order/account truth and guarded execution of irreversible operations.
- Complete migration of legacy knowledge with human-approved conflict resolution.
- Field acceptance of real-world client sessions, customer images, and manual takeover.

Never declare the project completed or auto-merge high-risk changes based solely on unit tests. Perform a shadow evaluation, manual review, and canary rollout first.

## Public repository privacy

No real company name, SKU, shop key, tutorial hostname, product URL, customer account, or order must appear in publicly versioned documentation. Set merchant details using private per-shop runtime configuration instead.
