# Public documentation sanitization policy

This repository is public. Documentation must describe behavior using generic examples instead of merchant-specific records.

- Replace merchant, product, buyer, device-brand and private transaction examples with neutral terms such as **Example Store**, **Product A**, **Device X**, and **Buyer 1**.
- Never commit actual deployment hostnames, merchant-owned URLs, access tokens, order identifiers, accounts, or buyer messages.
- Use `<CONTROL_PLANE_HOST>`, `<REPO_CLONE_URL>`, `<MERCHANT_GUIDE_URL>` and `<PRODUCT_ID>` as placeholders.
- Production business facts and approved guides belong in per-shop runtime settings or private configuration, not source-controlled public documentation.
- Issue descriptions, pull-request summaries and sample logs require the same review before publication.
- Changes to public docs should run the privacy test in `tests/test_public_docs_privacy.py`.

History may still preserve previous commits or discussion revisions. A follow-up access and repository-history review is needed when removal from historical snapshots is required.
