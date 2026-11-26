# Generic image evidence and conditional business advice

## Design boundary

Image knowledge is a domain-neutral visual evidence source. It is not a device, TV, account, membership, or recharge classifier. A shop may use it for any image category it configures: products, labels, documents, order/payment/logistics screenshots, UI screens, defects, usage scenes, or other business evidence.

The image knowledge record supplies:
- the image/OCR fingerprint and configured HitInfo rules;
- a neutral description and tags;
- a shop-scoped match identifier.

Knowledge V2 supplies the business meaning and the reply. Typed evidence requirements are evaluated by the conditional-advice policy, not inferred from the OCR text alone.

## Reply routing and order-evidence boundaries

The commerce authority is question-aware. It loads the structured order ledger when the current question or active conversation topic indicates an order-dependent request (order, payment, shipping, delivery, refund, return, etc.). For unrelated questions it initially marks commerce context as `not_required` and does not read the order ledger. If Knowledge V2 later selects a record with typed `evidence:*` requirements, the runtime lazily loads the exact ShopKey-scoped order context and evaluates the dedicated evidence policy. This allows image-only follow-ups such as "这张图支持吗？" to use order evidence without making every image or every store depend on device/order classification.

A Knowledge V2 record containing any supported `evidence:*` requirement cannot become a local direct reply unless the dedicated evidence policy authorizes that selected record. An OCR match alone, a visual description alone, or a high model confidence score is not authorization. The current local allow-list is deliberately narrow: the four image + paid/open order requirements below can authorize only a safe refund recommendation.

## Typed RequiredContext values

The first supported conditional action is a *refund recommendation*, not a refund operation. A production-approved Knowledge V2 record may opt in by including all four values below in `RequiredContext`:

```text
evidence:image.matches_record
evidence:order.paid
evidence:order.open
evidence:order.product_matches_record
```

These values are generic and may be reused by any shop whose business rule genuinely depends on an image and a paid order.

The record must also:
- use `Type=after_sale`;
- use `RiskLevel=high`;
- use `Intent=after_sale` with `Predicate=refund_policy` or `refund_advice`;
- be active and production-approved, with `Authority >= 0.90` and match confidence at least `0.94`;
- match an exact saved alias (or a very strong same-intent/same-predicate match);
- include a safe suggestion such as “建议您在订单详情页面申请退款”;
- not claim that a refund was submitted, completed, or executed.

If `ProductIds` is populated, it must match the structured order's item ID. Otherwise, a specific entity/subject/title term must match the order's item title or SKU. Generic words such as “商品”“会员”“服务”“设备” are not sufficient product evidence.

## Fail-closed behavior

No local conditional refund suggestion is sent when:
- the order ledger is missing, unreadable, or ambiguous;
- the order is unpaid, closed, cancelled, refunded, shipped, or delivered;
- the image is unmatched, withdrawn, disabled, stale, from another buyer/shop, or predates the order evidence;
- the image does not match the selected knowledge record;
- the order product does not match the knowledge record;
- the knowledge record conflicts, is not production-approved, or its answer claims to perform a refund.

The current image must be the newest same-shop/same-buyer image observation and must still match the active image-knowledge rule; a newer unmatched or withdrawn image invalidates an older match. The image message must also be the exact current OCR message when the decision is made in the image path.

A two-layer delivery ledger protects the advice key by ShopKey + buyer + order ID + knowledge record ID. The Windows ledger coordinates local processes; the control plane stores a second atomic lease keyed by ShopKey plus a server-HMAC of the buyer/order/advice scope. The API uses SQLite `BEGIN IMMEDIATE`, encrypts display identifiers at rest, and allows different Bot client tokens bound to the same ShopKey to share the same barrier.

A device must acquire the server lease before sending and renew it at the final pre-send guard. A delivered result becomes a permanent dedupe marker; a confirmed pre-send cancellation releases the lease; an ambiguous result or an expired process lease becomes an indefinite manual-review barrier. An expired lease receives a two-minute safety grace period before an operator may resolve it. The Knowledge V2 records page merges local and server pending records and exposes **条件性建议发送核对**. Releasing a not-delivered reservation does not bypass any evidence check on the next attempt.

Cross-device coordination requires every device to use the same control-plane endpoint and its shared persistent database. If the control plane is unavailable or the distributed lease cannot be acquired/renewed, the conditional recommendation is fail-closed and is not sent. This is an at-most-once barrier, not proof that the buyer saw the message; real Qianniu acceptance is still required.

## Four Windows / Qianniu acceptance scenarios

Run these against the same test shop with auto-reply enabled and record the diagnostic log for each case.

| Scenario | Setup | Expected result |
|---|---|---|
| 1. Positive | Active image knowledge matches; image belongs to this buyer/shop and is newer than the order evidence; exact product match; structured order is paid and open; exact knowledge alias matches | One local conditional recommendation is sent. The delivery ledger records it. |
| 2. Unpaid | Same image and product, but the structured order is unpaid or payment is not confirmed | No refund recommendation is sent. Continue through contextual AI/manual handling. |
| 3. Terminal order | Same image and product, but the latest order event is closed, cancelled, refunded, shipped, or delivered | No new refund recommendation is sent. An earlier paid snapshot must not override the terminal event. |
| 4. Mismatch / repeat | Use a different buyer or shop, a withdrawn/disabled/unmatched image, a compatible/different product, an ambiguous multi-order selection, or replay the same advice after it was delivered | No cross-buyer/shop/product advice and no duplicate recommendation. |

Also verify that normal non-order image knowledge continues to work for unrelated shop types and does not require any order evidence unless its own record explicitly requests it.

After each real scenario, run the existing read-only diagnostics command:

```powershell
# From a repository checkout:
powershell.exe -NoProfile -File scripts\\Invoke-QnBotAcceptanceDiagnostics.ps1

# From the packaged Windows update:
powershell.exe -NoProfile -File diagnostics\\Invoke-QnBotAcceptanceDiagnostics.ps1
```

The report now includes the conditional-advice ledger summary and recent gate/delivery outcomes. It exposes only HMAC-pseudonymized Shop/Buyer/Order/Image/Knowledge/Advice references and fixed reason codes; it does not export raw buyer names, order IDs, image message IDs, chat text, or logs. `delivery_sent` means the client send call was confirmed by the local sender, not that a human buyer has visually acknowledged receipt.

Expected outcome codes:
- Positive: `evidence_ready` → `advice_authorized` → `delivery_reserved` → `delivery_sent`.
- Unpaid/unverified: `evidence_rejected / order_unverified_or_not_paid`.
- Closed/refunded/shipped/delivered: `evidence_rejected / order_not_paid_or_terminal`.
- Image/product mismatch: `evidence_rejected` or `advice_rejected / knowledge_record_or_image_order_match_failed`.
- Replay after successful send: `delivery_blocked_duplicate / already_delivered`.

## Windows package

The branch is based on current `master`, which already contains #504 deterministic HitInfo matching and #505 curated-image short follow-up handling. The Windows release workflow builds the complete `Bin + data` package and intentionally excludes local `params.db`.
