# Phase 0 Search Provider API Revalidation Record

**Revalidation Date:** 2026-09-07  
**Specification Reference:** Multi-Search-Provider Platform, Phase 0 (AC-1.1–AC-1.3, AC-16.1–AC-16.3)

---

## 1. GitHub REST API Revalidation

### Official Documentation References
- **Code Search API:** [GitHub REST API — Search Code](https://docs.github.com/en/rest/search/search?apiVersion=2022-11-28#search-code)
- **Repository Contents API:** [GitHub REST API — Get Repository Content](https://docs.github.com/en/rest/repos/contents#get-repository-content)
- **Rate Limits & Secondary Limits:** [GitHub REST API — Rate Limits](https://docs.github.com/en/rest/using-the-rest-api/rate-limits-for-the-rest-api)

### Endpoints & Capability Matrix
| Operation | Method & Route | Supported | Rate Limit / Quota | Notes |
|---|---|---|---|---|
| **Capability Discovery** | `GET /meta` | Yes | 60 req/hr unauthenticated, 5,000 req/hr authenticated | Validates endpoint reachability and official API versions. |
| **Credential Validation** | `GET /user` | Yes | Core API quota (5,000 req/hr) | Verifies token validity without disclosing token material. |
| **Code Search** | `GET /search/code` | Yes | 30 req/min authenticated, 10 req/min unauthenticated | Paginated (`page`, `per_page`, max 100). |
| **Content Retrieval** | `GET /repos/{owner}/{repo}/contents/{path}` | Yes | Core API quota | Returns deterministic git blob `sha` and base64 encoded content. |

### Rate Limit & Error Headers
- `X-RateLimit-Limit`: Maximum requests allowed in current window.
- `X-RateLimit-Remaining`: Requests remaining in current window.
- `X-RateLimit-Reset`: UTC epoch timestamp indicating window reset time.
- `Retry-After`: Delay in seconds (issued on secondary rate limits / HTTP 429).
- Primary limit: HTTP 403 with `X-RateLimit-Remaining: 0`.
- Secondary limit: HTTP 403 or 429 with allowlisted message ("secondary rate limit", "abuse detection mechanism").
- Invalid credentials: HTTP 401 Unauthorized with "Bad credentials".

---

## 2. GitLab REST API Revalidation

### Official Documentation References
- **Blob / Code Search API:** [GitLab REST API — Search API (scope: blobs)](https://docs.gitlab.com/ee/api/search.html#scope-blobs)
- **Repository Files API:** [GitLab REST API — Repository Files](https://docs.gitlab.com/ee/api/repository_files.html)
- **Rate Limits:** [GitLab REST API — Rate Limits](https://docs.gitlab.com/ee/user/gitlab_com/index.html#gitlabcom-specific-rate-limits)

### Endpoints & Capability Matrix
| Operation | Method & Route | Supported | Rate Limit / Quota | Notes |
|---|---|---|---|---|
| **Capability Discovery** | `GET /api/v4/version` | Yes | Instance rate limit | Validates version, flavor, and base path. |
| **Credential Validation** | `GET /api/v4/user` | Yes | Instance rate limit | Verifies personal access token validity. |
| **Blob Search** | `GET /api/v4/search?scope=blobs` | Yes | Paginated search limit | Paginated (`page`, `per_page`, `X-Total`, `X-Total-Pages`, `X-Next-Page`). |
| **Raw Content** | `GET /api/v4/projects/{id}/repository/files/{file_path}/raw?ref={ref}` | Yes | Instance rate limit | Retrieves raw file bytes directly with branch/commit ref. |

---

## 3. Verdict
All Phase 0 requirements for both GitHub and GitLab are confirmed supported through official public REST APIs.
No undocumented, private, or scraping endpoints are required or permitted.
