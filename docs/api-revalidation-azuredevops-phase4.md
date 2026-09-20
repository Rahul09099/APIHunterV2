# Phase 4 Azure DevOps API Revalidation Record

**Revalidation Date:** 2026-09-20
**Specification Reference:** Multi-Search-Provider Platform, Phase 4 (AC-16.1–AC-16.2, AC-16.38–AC-16.40)

---

## 1. Official Documentation References
- **Code Search Results:** [Azure DevOps REST API 7.1 – Search – Code Search Results](https://learn.microsoft.com/en-us/rest/api/azure/devops/search/code-search-results?view=azure-devops-rest-7.1)
- **Git Items (Content):** [Azure DevOps REST API 7.1 – Git – Items – Get](https://learn.microsoft.com/en-us/rest/api/azure/devops/git/items/get?view=azure-devops-rest-7.1)
- **Connection Data (Credential Validation):** [Azure DevOps REST API 7.1 – Connection Data](https://learn.microsoft.com/en-us/rest/api/azure/devops/core/connection-data?view=azure-devops-rest-7.1)
- **Authentication (PAT):** [Azure DevOps – Personal Access Tokens](https://learn.microsoft.com/en-us/azure/devops/organizations/accounts/use-personal-access-tokens-to-authenticate)

## 2. Endpoints & Capability Matrix
| Operation | Method & Route | Supported | Notes |
|---|---|---|---|
| **Credential Validation** | `GET _apis/connectionData?api-version=7.1` (Basic `:PAT`) | Yes | 401 = AuthInvalid. |
| **Scoped Code Search** | `POST {org}/{project}/_apis/search/codesearchresults?api-version=7.1` body `{searchText, skip, top}` | Yes | Explicit org/project scope; `skip/top` pagination. |
| **Content** | `GET {org}/{project}/_apis/git/repositories/{repo}/items?path=&version=&api-version=7.1` | Yes | SHA-256 version per AC-10.24. |
| **Normalized Identity** | `(instance, project/repo, version, path)` | Yes | Project/repo/version preserved as provenance. |
| **Global Public** | Not offered | No | AC-16.40: no global-public capability is assumed or exposed. |

## 3. Constraints
- Services + Server variants are addressed through explicit `Organization` (+ optional `Project`) settings and Endpoint Policy validation (AC-16.38).
- Project/repository/version are normalized into the common model (AC-16.39).
- No global-public path exists; the adapter declares no `GlobalPublicSearch` capability.

## 4. Verdict
Azure DevOps Services/Server scoped search, auth, content, pagination, outcomes, and identity are supported through official 7.1 APIs. Adapter `azuredevops-search-v1` implements exactly this contract.
