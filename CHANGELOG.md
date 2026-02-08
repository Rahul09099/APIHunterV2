# Changelog

All notable changes to UnsecuredAPIKeys Open Source.

## [1.0.0] - 2025-12-09 - Lite Version Release

This release transforms the project from a full-featured web platform into a streamlined CLI tool for local use.

### Why This Change?

The original open-source release included the full platform architecture (WebAPI, UI, PostgreSQL, 15+ providers). This created barriers for users who just wanted to:
- Learn about API key security
- Run simple searches locally
- Understand how key discovery works

The lite version removes these barriers while the full platform remains available at [www.UnsecuredAPIKeys.com](https://www.UnsecuredAPIKeys.com).

---

### Added

#### New CLI Application (`UnsecuredAPIKeys.CLI/`)
- **Menu-driven interface** using Spectre.Console for rich terminal UI
- **ScraperService** - Searches GitHub for exposed API keys continuously
- **VerifierService** - Maintains exactly 50 valid keys with automatic re-verification
  - **Fallback validation**: Tries multiple providers when assigned provider fails
  - **Auto-reclassification**: Updates key's ApiType if different provider validates it
- **DatabaseService** - Handles SQLite initialization, statistics, and exports
- **Constants.cs** - Centralized limits (`MAX_VALID_KEYS = 50`) and app info
- **appsettings.example.json** - Configuration template for self-hosting

#### Documentation
- **CHANGELOG.md** - This file
- **Badges** in README (GitHub Stars, .NET 10, License)
- **Stars thank you** message for community support
- **Self-hosting sections**: Database management, Search Queries, Rate Limiting, Troubleshooting
- **Platform support** documented (Windows, macOS, Linux)

#### Default Search Queries (Auto-seeded)
- OpenAI: `sk-proj-`, `sk-or-v1-`, `OPENAI_API_KEY`, `openai.api_key`
- Anthropic: `sk-ant-api`, `ANTHROPIC_API_KEY`, `anthropic_api_key`
- Google: `AIzaSy`, `GOOGLE_API_KEY`, `gemini_api_key`

---

### Changed

#### Database: PostgreSQL → SQLite
- **Before**: Required PostgreSQL server, connection strings, migrations
- **After**: Single `unsecuredapikeys.db` file, auto-created on first run
- No migrations needed - uses `EnsureCreated()` for simplicity
- Package change: `Npgsql.EntityFrameworkCore.PostgreSQL` → `Microsoft.EntityFrameworkCore.Sqlite`

#### Providers: 15+ → 3
- **Kept**: OpenAI, Anthropic, Google AI
- **Removed**: Cohere, DeepSeek, ElevenLabs, Groq, HuggingFace, MistralAI, OpenRouter, PerplexityAI, Replicate, StabilityAI, TogetherAI

#### Search Providers: 3 → 1
- **Kept**: GitHub (via Octokit)
- **Removed**: GitLab, SourceGraph

#### Architecture: Web Platform → CLI Tool
- **Before**: WebAPI + Next.js UI + Separate Bots + PostgreSQL
- **After**: Single CLI application + SQLite

#### Valid Key Limit
- **Before**: Configurable/unlimited
- **After**: Hard cap of 50 keys (enforced in `LiteLimits.MAX_VALID_KEYS`)

---

### Removed

#### Projects Deleted
| Project | Description |
|---------|-------------|
| `UnsecuredAPIKeys.WebAPI/` | REST API, SignalR hub, controllers |
| `UnsecuredAPIKeys.UI/` | Next.js frontend, React components |
| `UnsecuredAPIKeys.Bots.Scraper/` | Standalone scraper service |
| `UnsecuredAPIKeys.Bots.Verifier/` | Standalone verifier service |
| `UnsecuredAPIKeys.Common/` | Shared utilities (was empty) |

#### AI Providers Removed (11)
- CohereProvider.cs
- DeepSeekProvider.cs
- ElevenLabsProvider.cs
- GroqProvider.cs
- HuggingFaceProvider.cs
- MistralAIProvider.cs
- OpenRouterProvider.cs
- PerplexityAIProvider.cs
- ReplicateProvider.cs
- StabilityAIProvider.cs
- TogetherAIProvider.cs

#### Search Providers Removed (2)
- GitLabSearchProvider.cs
- SourceGraphSearchProvider.cs

#### Services Removed
- GitHubIssueService.cs (auto issue creation)
- SnitchLeaderboardService.cs (community leaderboard)
- UserModerationService.cs (user bans)

#### Database Models Removed (15)
| Model | Purpose |
|-------|---------|
| DiscordUser | Discord OAuth integration |
| UserSession | Session management |
| UserBan | User moderation |
| DonationTracking | PayPal donations |
| DonationSupporter | Donor recognition |
| IssueSubmissionTracking | GitHub issue automation |
| IssueVerification | Issue verification flow |
| SnitchLeaderboard | Community rankings |
| VerificationBatch | Batch job tracking |
| VerificationBatchResult | Batch results |
| KeyInvalidation | Key lifecycle tracking |
| KeyRotation | Key rotation events |
| PatternEffectiveness | Search pattern analytics |
| Proxy | Proxy rotation support |
| RateLimitLog | API rate limit tracking |

#### Database Migrations Removed (30+ files)
All PostgreSQL migrations deleted - SQLite uses runtime schema creation.

#### Configuration Files Removed
- `.dockerignore`
- `.vite/` directories
- `Deploy-VerificationBot.ps1`
- `package.json`, `package-lock.json`
- `analyze-unmatched-keys.ps1`
- `check-unmatched-keys.ps1`
- All `appsettings.json` files (kept `.example` versions)

#### Features Not in Lite Version
- Web UI dashboard
- Real-time SignalR updates
- Discord OAuth login
- PayPal donation integration
- GitHub issue auto-creation
- Snitch leaderboard
- User bans/moderation
- Proxy rotation
- Rate limit tracking tables
- Multi-search-provider support
- Batch verification locking

---

### Security

- **Enforced .gitignore** rules for:
  - `*.db`, `*.sqlite`, `*.sqlite3` (database files)
  - `.env`, `.env.*` (environment files)
  - `.claude/` (AI assistant files)
  - `appsettings.json` (local config with tokens)
- **Warning comments** added throughout code about not publishing results publicly
- **Security Warning** section prominent in README

---

### Migration Guide

#### For Users of the Old Version

The lite version is a **complete rewrite**. There is no migration path - it's designed for fresh local use.

If you need the original Web UI + WebAPI architecture:
- Check the [`legacy_ui`](https://github.com/TSCarterJr/UnsecuredAPIKeys-OpenSource/tree/legacy_ui) branch (no longer maintained)

If you need the full platform features:
- Visit [www.UnsecuredAPIKeys.com](https://www.UnsecuredAPIKeys.com)

#### For Contributors

Open PRs against the old architecture are now outdated:
- PR #5 (Docker Compose + Postgres) - No longer applicable
- PR #8 (Next.js dependency bump) - UI removed
- PR #9 (start.ps1) - Empty PR

New contributions should target the CLI architecture.

---

### Lite vs Full Comparison

| Feature | Lite (This Repo) | Full Version |
|---------|------------------|--------------|
| Search Providers | GitHub | GitHub, GitLab, SourceGraph |
| API Providers | 3 (OpenAI, Anthropic, Google) | 15+ |
| Valid Key Cap | 50 | Higher limits |
| Interface | CLI | Web UI + API |
| Database | SQLite (local file) | PostgreSQL |
| Real-time Updates | No | SignalR |
| Community Features | No | Leaderboard, Discord |
| Self-hosted | Yes | Yes (complex) |
| Beginner Friendly | Yes | No |

---

### Technical Details

#### Dependencies (CLI)
```xml
<PackageReference Include="Spectre.Console" Version="0.49.1" />
<PackageReference Include="Microsoft.EntityFrameworkCore.Sqlite" Version="9.0.0" />
<PackageReference Include="Octokit" Version="13.0.1" />
```

#### Rate Limits (Built-in)
| Operation | Delay |
|-----------|-------|
| Between searches | 5,000ms |
| Between verifications | 1,000ms |
| Verification batch size | 10 keys |

#### File Structure
```
UnsecuredAPIKeys-OpenSource/
├── UnsecuredAPIKeys.CLI/
│   ├── Program.cs              # Entry point, menu UI
│   ├── Constants.cs            # LiteLimits, AppInfo
│   ├── Services/
│   │   ├── ScraperService.cs   # GitHub search
│   │   ├── VerifierService.cs  # Key validation
│   │   └── DatabaseService.cs  # SQLite ops
│   └── appsettings.example.json
├── UnsecuredAPIKeys.Data/
│   ├── DBContext.cs            # EF Core context
│   ├── Models/                 # 5 essential models
│   └── Common/CommonEnums.cs   # Simplified enums
├── UnsecuredAPIKeys.Providers/
│   ├── AI Providers/           # 3 providers
│   ├── Search Providers/       # GitHub only
│   └── _Base/, _Interfaces/    # Framework
├── CHANGELOG.md
├── CLAUDE.md
├── LICENSE
└── README.md
```

---

## [1.0.0] - 2024-07-21 - Initial Open Source Release

Initial release of the full UnsecuredAPIKeys platform as open source.

### Included
- WebAPI with REST endpoints and SignalR
- Next.js frontend with HeroUI components
- PostgreSQL database with EF Core migrations
- 15+ API key validation providers
- 3 search providers (GitHub, GitLab, SourceGraph)
- Discord OAuth integration
- PayPal donation integration
- Automated scraper and verifier bots
- Snitch leaderboard
- User moderation system

---

**Full Version**: [www.UnsecuredAPIKeys.com](https://www.UnsecuredAPIKeys.com)
