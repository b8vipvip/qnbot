# Reception Agent autonomous workflow publishing status

This document describes the generic, shop-scoped reception workflow catalog. It contains no merchant URL, vendor branding, product identifiers, or buyer messages.

## Product policy

Reception Agent may create, edit, validate, publish, and roll back workflow definitions autonomously. A validated publication becomes the active version immediately. No administrator approval, manual activation token, or mandatory gray-rollout stage is required.

This policy applies to publishing workflow definitions. It does not turn workflow publication into payment, account, refund, order, or fulfillment authority.

## Published workflow model

The catalog is versioned by `shop_key`, `product_id`, `sku`, flow ID, and flow version. Each step declares its state, required evidence, allowed action, next state, and timeout.

Allowed catalog actions are limited to:

- `WAIT` — wait for a later event or required evidence.
- `ASK_EVIDENCE` — request missing or clearer evidence.
- `HANDOFF` — hand the conversation to a human.
- `READ_ONLY_TOOL` — request a read-only lookup.
- `REPLY` / `REPLY_REVIEWED` — express a reply intent; outgoing text must still pass the existing canonical send pipeline and its takeover, relevance, identity, deduplication, and pre-send checks.

The catalog cannot grant arbitrary code execution, payment, recharge, refund, account modification, or actual fulfillment actions.

## Autonomous publication and recovery

- The catalog uses a stable, uniquely resolved ShopKey and the exact per-shop settings scope.
- `Publish` validates the complete document and publishes it in one cross-process locked transaction.
- A revision compare-and-swap prevents stale editors or concurrent Agents from overwriting a newer publication.
- Catalog revision and per-flow versions are monotonic.
- Previous published snapshots are retained as immutable history records.
- `RollbackToPreviousPublished` restores the previous valid content as a new published revision; rollback is also immediate and does not require administrator approval.
- Existing `qnbot.reception-workflow-drafts.v1` files are migrated to the new active catalog only after schema and action validation. The original legacy file is retained.
- A corrupt or unreadable current catalog, failed lock, revision conflict, or invalid history snapshot blocks the write; it never silently resets the active version.

## Existing runtime protection

The existing application still owns the actual conversation and business side effects:

- Manual takeover, stale-generation cancellation, exact buyer/seller/ShopKey identity, send deduplication, delivery evidence, and pre-send validation remain in the canonical send pipeline.
- The privacy-minimized evidence ledger is diagnostic. It stores hashes and structured observations rather than raw chat, OCR, image URLs, phone numbers, or order IDs.
- Order created, payment confirmed, device eligibility, personal account binding, final fulfillment authorization, and delivery completion are separate assertions. No one stage implies the next.
- The evidence ledger is never an order/payment authority and cannot authorize an irreversible operation.

## Remaining implementation and acceptance boundary

- The active catalog read API is available to the Reception Agent/runtime adapter. The complete generic state-machine executor still needs to be connected to real message, OCR, order, and account evidence sources; publishing a catalog does not by itself prove that every flow transition is executing.
- Trusted external order/account truth and guarded, idempotent execution of irreversible operations remain separate integrations. The workflow catalog must not enable these operations.
- UI/runtime review of active state transitions, full migration of legacy knowledge, and field acceptance of real client sessions, customer images, manual takeover, and process restart recovery remain open.
- CI/build success does not replace real Windows/Qianniu acceptance.

## Public repository privacy

No real company name, SKU, ShopKey, tutorial hostname, product URL, customer account, order, or raw customer message may appear in public documentation. Merchant details belong in private per-shop runtime configuration.
