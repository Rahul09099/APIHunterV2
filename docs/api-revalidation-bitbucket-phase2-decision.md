# Phase 2 Bitbucket Cloud Decision Gate Record

**Decision Date:** 2026-09-20 (premise revalidated immediately before implementation)
**Specification Reference:** Multi-Search-Provider Platform, Phase 2 (AC-1.1–AC-1.3, AC-16.1–AC-16.2, AC-16.31–AC-16.34)
**Deprecation Premise:** Atlassian Bitbucket Cloud code-search deprecation effective **November 1, 2026**

---

## 1. Official Documentation Consulted (current as of 2026-09-20)
- **Bitbucket Cloud REST API Changelog / Deprecation Notices:** [Bitbucket Cloud – Deprecation & API Changelog](https://developer.atlassian.com/cloud/bitbucket/rest/intro/#changelog)
- **Bitbucket Cloud Code Search (legacy):** [Bitbucket Cloud – Code Search](https://developer.atlassian.com/cloud/bitbucket/rest/api-group-search/#api-repositories-workspace-repo-slug-search-code-post) (marked deprecated; removal November 1, 2026)
- **Bitbucket Cloud Search Replacement Guidance:** No supported replacement authorized code-search API is documented as of this date. Workspace repository listing (`GET /2.0/repositories/{workspace}`) and file browsing (`GET /2.0/repositories/{workspace}/{repo}/src/{commit}/{path}`) remain, but they do not provide the required authorized code-search capability (query → ranked code matches + pagination + typed outcomes).
- **Atlassian Community / Status:** No GA replacement endpoint published before the November 1, 2026 removal window.

## 2. Decision
**NO-IMPLEMENTATION (skip to Phase 3).**

- There is no supported official Bitbucket Cloud API that supplies the required authorized code-search capability (AC-16.32).
- Per AC-16.33 and AC-16.34, the platform makes **no adapter, no code path, no scraping, no private/undocumented endpoint**, and proceeds directly to Phase 3 (Hugging Face Hub) with this artifact as evidence.
- The registry intentionally contains no `Bitbucket` provider kind; `SearchProviderEnum.Unknown` remains fail-closed and any Bitbucket request fails with `RequestInvalid`/no-adapter rather than falling back to GitHub (AC-8.3).
- If Atlassian later publishes a supported replacement, a new decision-gate revalidation must be recorded before any implementation begins.

## 3. Compliance
- No obsolete, private, undocumented, or website-scraping path was used or added (AC-16.34, AC-1.1–AC-1.3).
- This artifact satisfies the decision-gate documentation requirement even though Phase 2 is skipped, and was committed within the deprecation window (no later than late October 2026).
