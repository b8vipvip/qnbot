# Generic image evidence and conditional business advice

## Design boundary

Image knowledge is a domain-neutral visual evidence source. It is not a device, TV, account, membership, or recharge classifier. A shop may use it for any image category it configures: products, labels, documents, order/payment/logistics screenshots, UI screens, defects, usage scenes, or other business evidence.

The image knowledge record supplies:
- the image/OCR fingerprint and configured HitInfo rules;
- a neutral description and tags;
- a shop-scoped match identifier.

Knowledge V2 supplies the business meaning and the reply. Typed evidence requirements are evaluated by the conditional-advice policy, not inferred from the OCR text alone.

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

A separate cross-process delivery ledger reserves the advice key by ShopKey + seller + buyer + order ID + knowledge record ID. Confirmed delivery is recorded; uncertain delivery (including a process crash after reservation) is held for manual reconciliation instead of being sent again. The Knowledge V2 records page exposes **条件性建议发送核对** for an operator to inspect the actual Qianniu conversation and explicitly mark the record delivered or not delivered. Releasing a not-delivered reservation does not bypass any evidence check on the next attempt.

This ledger coordinates processes under the same Windows user profile; cross-device exactly-once delivery still depends on a single authoritative shop runtime and is not claimed as globally synchronized. The reconciliation window displays this limitation explicitly.

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
powershell.exe -NoProfile -File scripts\\Invoke-QnBotAcceptanceDiagnostics.ps1
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
