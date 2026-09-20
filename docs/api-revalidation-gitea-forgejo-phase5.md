# Phase 5 Gitea / Forgejo API Revalidation Record

**Revalidation Date:** 2026-09-20
**Specification Reference:** Multi-Search-Provider Platform, Phase 5 (AC-16.1–AC-16.2, AC-16.41–AC-16.44)

---

## 1. Official Documentation References
- **Gitea API 1.24 – Search Code:** [Gitea API – Search Repository Code](https://docs.gitea.com/api/1.24/#tag/repository/operation/searchCode) (`GET /api/v1/search/code?q=&page=&limit=`)
- **Gitea API 1.24 – Get Raw File:** [Gitea API – Get Raw File](https://docs.gitea.com/api/1.24/#tag/repository/operation/getRawFile) (`GET /api/v1/repos/{owner}/{repo}/raw/{filepath}?ref=`)
- **Gitea API 1.24 – Version:** [Gitea API – Get Version](https://docs.gitea.com/api/1.24/#tag/miscellaneous/operation/getVersion) (`GET /api/v1/version`)
- **Forgejo API Usage:** [Forgejo – API Usage](https://forgejo.org/docs/latest/admin/api-usage/) (Gitea-compatible `GET /api/v1/search/code`, `GET /api/v1/repos/{owner}/{repo}/raw/{filepath}`, `GET /api/v1/version` with `forgejo` flavor marker)
- **Authentication:** Token via `Authorization: token <pat>` for both flavors.

## 2. Endpoints & Capability Matrix
| Operation | Method & Route | Supported | Notes |
|---|---|---|---|
| **Flavor/Version Detection** | `GET /api/v1/version` (+ `ApiFlavor`/`ServerVersion` settings) | Yes | Must pass before discovery/validation (AC-16.44). |
| **Credential Validation** | `GET /api/v1/user` | Yes | 401 = AuthInvalid. |
| **Code Search** | `GET /api/v1/search/code?q=&page=&limit=` | Yes | Paginated; continuation `{page, query}` bound to adapter version. |
| **Content** | `GET /api/v1/repos/{owner}/{repo}/raw/{path}?ref=` | Yes | SHA-256 version per AC-10.24. |
| **Self-hosted Safety** | Approved `{scheme}://{host}:{port}{basePath}` + per-op DNS | Yes | Endpoint Policy gates every operation (AC-16.44, AC-12.1–12.27). |

## 3. Flavor / Version Rules
- Gitea: `ApiFlavor=gitea`, `ServerVersion>=1.20` (design ref 1.24). Older/unknown → `RequestInvalid/UnsupportedVersion`, no capabilities exposed (AC-16.42–16.43).
- Forgejo: `ApiFlavor=forgejo`, `ServerVersion>=1.0`. Older/unknown → `RequestInvalid/UnsupportedVersion`.
- Only flavor/version-validated capabilities are exposed (intersection of declared + discovered, AC-2.16).
- Public search stays disabled (neither adapter declares `GlobalPublicSearch`) unless a validated server later supports it and recorded consent is active (Task 22.2).

## 4. Verdict
Gitea and Forgejo self-hosted search, auth, content, pagination, outcomes, and flavor/version-gated capabilities are supported through official versioned APIs. Adapters `gitea-search-v1` and `forgejo-search-v1` implement exactly this contract with no scraping or undocumented routes.
