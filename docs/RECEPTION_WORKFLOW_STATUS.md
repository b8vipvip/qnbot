# Reception Agent autonomous workflow publishing status

This document describes the generic, shop-scoped reception workflow catalog. It contains no merchant URL, vendor branding, product identifiers, or buyer messages.

## Autonomous publication policy

## Product policy

Reception Agent may create, edit, validate, publish, and roll back workflow definitions autonomously. A validated publication becomes the active version immediately. No administrator approval, manual activation token, or mandatory gray-rollout stage is required.

This policy applies to publishing workflow definitions. It does not turn workflow publication into payment, account, refund, order, or fulfillment authority.

## Published workflow model

The catalog is versioned by `shop_key`, `product_id`, `sku`, flow ID, and flow version. Each step declares its state, required evidence, allowed action, next state, and timeout.

Allowed catalog actions are limited to:

- `WAIT` — wait for a later event or required evidence.
- `ASK_EVIDENCE` — request missing or clearer evidence.
- `HANDOFF` — hand the conversation to a human.
- `READ_ONLY_TOOL` — reserved for a future read-only tool adapter; AI auto-publishing currently rejects it and runtime fails closed if a legacy flow requests it.
- `REPLY` / `REPLY_REVIEWED` — the AI may publish a reply step only when it points to a real human reply in the same evidence-bound session; outgoing text still passes the canonical send pipeline and its takeover, relevance, identity, deduplication, and pre-send checks.

The catalog cannot grant arbitrary code execution, payment, recharge, refund, account modification, or actual fulfillment actions. These high-risk business actions remain outside autonomous workflow publication.

## Autonomous publication and recovery

- Every public catalog operation requires the exact ShopKey captured by the Knowledge Center or message event, resolves one stable ShopContext once, and reuses that exact context for the active catalog, legacy migration, history snapshots, and rollback paths. If a seller nickname is present it must match the resolved profile; offline profiles may be accessed by exact ShopKey even when the nickname is unavailable. Seller nickname is never the primary key and is never re-resolved midway through a write.
- The active-version pointer is stored in the same catalog document as the immutable flow definitions, so publication and pointer replacement share one atomic write.
- `Publish` validates the complete document and publishes it in one cross-process locked transaction.
- A revision compare-and-swap prevents stale editors or concurrent Agents from overwriting a newer publication.
- Catalog revision and per-flow versions are monotonic.
- Previous published snapshots are retained as immutable history records.
- `RollbackToPreviousPublished` restores the previous valid content as a new published revision through an atomic rollback; rollback is also immediate and does not require administrator approval.
- Existing `qnbot.reception-workflow-drafts.v1` files are migrated to the new active catalog only after schema and action validation. The original legacy file is retained.
- A corrupt or unreadable current catalog, failed lock, revision conflict, or invalid history snapshot blocks the write; it never silently resets the active version.

## Existing runtime protection

The existing application still owns the actual conversation and business side effects:

- Manual takeover, stale-generation cancellation, exact buyer/seller/ShopKey identity, send deduplication, delivery evidence, and pre-send validation remain in the canonical send pipeline.
- The privacy-minimized evidence ledger is diagnostic. It stores hashes and structured observations rather than raw chat, OCR, image URLs, phone numbers, or order IDs.
- Order created, payment confirmed, device eligibility, personal account binding, final fulfillment authorization, and delivery completion are separate assertions. No one stage implies the next.
- The evidence ledger is never an order/payment authority and cannot authorize an irreversible operation.

## AI generation and runtime execution

- Session learning now supplies alias-only evidence context for real buyer messages (`M`), recent visual/OCR observations (`V`), and exact ShopKey-scoped nonterminal order snapshots (`O`). Raw message IDs and order IDs are not sent to the model or stored in the active flow.
- AI flow proposals are automatically published only after deterministic checks: confidence >= 0.90, buyer message + real human correction + current configured product/order evidence, supported state transitions, safe evidence requests, and human-sourced reply templates. No administrator approval is required.
- `ASK_EVIDENCE` text is constrained to evidence requests. `REPLY` must reference a real human reply from the same session; high-risk payment, refund, account, delivery, and fulfillment claims are rejected.
- The runtime index is invalidated on publish/rollback and checks catalog revision on subsequent events so another process's publication is observed without restart. Multiple matching or already-progressed flows are detected as an ambiguity and handed off rather than selecting the first catalog entry.
- Runtime state recovery is ShopKey-isolated and durable: the per-shop file stores only buyer/message hashes, flow versions, state cursors, pending-send markers, and anomaly counters. State is restored after restart; stale flow versions and expired states are automatically reset. A pending send older than two minutes is treated as delivery-uncertain and handed off instead of being retried.
- Text and vision decisions return to the existing canonical send pipelines. The workflow runtime never sends directly. It commits a state transition only after the canonical send reports success; uncertain delivery is not retried automatically.
- `HANDOFF` requires a configured shop notification channel and queues a ShopKey-bound handoff notification. Repeated deterministic runtime faults are counted durably; three faults for the same flow/catalog revision within ten minutes trigger a revision-CAS rollback when a valid previous snapshot exists.

## Remaining implementation and acceptance boundary

- The code-level AI generation → evidence binding → immediate publish → text/image runtime execution → durable state recovery → anomaly rollback path is now implemented. This is not a claim that every merchant workflow has passed live acceptance.
- The current runtime consumes buyer messages, recent visual/OCR observations, and verified nonterminal order snapshots. It does not grant account-state, payment, recharge, refund, or fulfillment authority; trusted external order/account truth and idempotent irreversible-operation adapters remain separate integrations.
- Real Windows/Qianniu acceptance remains open: multi-shop A→B→A, same-nickname shops, restart recovery, long-running sessions, handoff notification delivery, image/OCR state progression, and test-buyer end-to-end workflow execution.
- CI/build success does not replace real Windows/Qianniu acceptance.

## Public repository privacy

No real company name, SKU, ShopKey, tutorial hostname, product URL, customer account, order, or raw customer message may appear in public documentation. Merchant details belong in private per-shop runtime configuration.
