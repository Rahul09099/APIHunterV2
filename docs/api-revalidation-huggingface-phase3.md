# Phase 3 Hugging Face Hub API Revalidation Record

**Revalidation Date:** 2026-09-20
**Specification Reference:** Multi-Search-Provider Platform, Phase 3 (AC-16.1–AC-16.2, AC-16.35–AC-16.37)

---

## 1. Official Documentation References
- **Hub API – List Models:** [Hugging Face Hub API – List Models](https://huggingface.co/docs/hub/api#get-apimodels)
- **Hub API – List Datasets:** [Hugging Face Hub API – List Datasets](https://huggingface.co/docs/hub/api#get-apidatasets)
- **Hub API – File Content / Raw:** [Hugging Face Hub – Download Files](https://huggingface.co/docs/hub/repositories-getting-started#download-files)
- **Authentication:** [Hugging Face – User Access Tokens](https://huggingface.co/docs/hub/security-tokens)

## 2. Endpoints & Capability Matrix
| Operation | Method & Route | Supported | Notes |
|---|---|---|---|
| **Credential Validation** | `GET /api/whoami-v2` with `Authorization: Bearer <token>` | Yes | 401 = AuthInvalid. |
| **Authorized Enumeration** | `GET /api/models?search=&limit=&skip=` and `GET /api/datasets?search=&limit=&skip=` | Yes | Paginated; `Link` not required – offset continuation. |
| **Content** | `GET /{repo}/raw/{rev}/{path}` | Yes | Raw bytes; SHA-256 version per AC-10.24. |
| **Normalized Identity** | `(instance, collection/repo, rev, path)` | Yes | Deterministic dedup; no URL-sole identity. |
| **Public** | Same Hub APIs without credential | Yes | Dual-gated (capability + consent + Slot). |

## 3. Constraints
- Only supported Hub APIs are used (AC-16.35). Website page scraping and undocumented routes are prohibited (AC-16.36).
- Any supported global-public operation remains behind the dual gate (AC-16.37, AC-12.28–AC-12.36).

## 4. Verdict
Hugging Face Hub model/dataset/Space enumeration, auth, content, pagination, identity, outcomes, and public dual-gating are supported through official APIs. Adapter `huggingface-hub-v1` implements exactly this contract.
