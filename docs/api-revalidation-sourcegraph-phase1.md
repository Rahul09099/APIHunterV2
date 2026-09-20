# Phase 1 Sourcegraph API Revalidation Record

**Revalidation Date:** 2026-09-20
**Specification Reference:** Multi-Search-Provider Platform, Phase 1 (AC-16.1–AC-16.3, AC-16.27–AC-16.30)

---

## 1. Official Documentation References
- **Streaming Search API:** [Sourcegraph API – Streaming Search](https://docs.sourcegraph.com/api/stream-search)
- **Code Search Concepts:** [Sourcegraph Code Search](https://docs.sourcegraph.com/code_search)
- **Authentication (Access Tokens):** [Sourcegraph Access Tokens](https://docs.sourcegraph.com/admin/auth/access-tokens)
- **Self-hosted Administration:** [Sourcegraph Self-hosted Install](https://docs.sourcegraph.com/admin/install)

## 2. Endpoints & Capability Matrix
| Operation | Method & Route | Supported | Notes |
|---|---|---|---|
| **Capability Discovery** | `GET /.api/user` (SaaS) or `{instance}/.api/user` | Yes | Validates endpoint + token scope without traffic. |
| **Credential Validation** | `GET /.api/user` with `Authorization: token <token>` | Yes | 401 = AuthInvalid, 403 = ForbiddenScope. |
| **Streaming Code Search** | `GET /.api/search/stream?q=<query>&v=V3&display=100&offset=<n>` | Yes | `text/event-stream`; events `matches`, `progress`, `alert`, `done`. |
| **Content Retrieval** | `GET /{repo}/-/raw/{path}@{rev}` | Yes | Raw bytes; SHA-256 content version per AC-10.24. |
| **Self-hosted** | Same routes behind approved `{scheme}://{host}:{port}{basePath}` | Yes | Endpoint Policy approval required before discovery/validation. |

## 3. Streaming Contract
- Event framing: `event: <type>` + `data: <json>` lines; terminal `done` event validates completion (AC-7.16).
- `progress` events advance Last Safe Checkpoint only (offset count), never match content (AC-7.27).
- `alert` events are advisory and never fail the operation; malformed/bounded events are skipped.
- Cancellation surfaces `Cancellation`; interruption resumes from Last Safe Checkpoint (offset) via `Continuation {offset, query}` bound to `sourcegraph-stream-v1` (AC-7.12/7.13, AC-7.26).
- Reconnect is safe: offset is progress-only and idempotent.

## 4. Public Search
Global-public search is supported by the API but stays disabled until the dual gate passes (effective `GlobalPublicSearch` + active exact-instance consent, AC-16.30, AC-12.28–12.36). Credentialed paths require Claim+Slot; public paths require Slot only.

## 5. Verdict
Sourcegraph SaaS + approved self-hosted streaming search, content, auth, rate-limit, and public-capability contracts are supported through official APIs. No scraping, private, or undocumented endpoints are used.
