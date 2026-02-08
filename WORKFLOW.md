# Usage & Deployment Workflow

This document outlines the standard operational workflows for using UnsecuredAPIKeys and the deployment procedures.

## 🔄 Operational Workflow

The typical lifecycle of finding and verifying API keys follows this 4-step process:

### 1. Configuration (One-time)
Before starting, ensure you have the necessary tokens.
- **GitHub Tokens**: Add multiple tokens (`Settings -> Manage GitHub Tokens`).
- **Search Queries**: Verify the default queries cover your target providers (`Settings -> Manage Search Queries`).

### 2. Scraping (The Hunt)
Use the **Scraper Service** to find potential keys.

**Option A: Lite Search (Quick Discovery)**
- Best for checking recent leaks or specific dorks.
- Finds up to 1000 results per query (GitHub API limitation).
- Fast execution.

**Option B: Deep Search (Comprehensive)**
- Best for finding older keys or high-volume queries (e.g., "openai").
- Uses **Partitioning Strategy**: Splits search by Language (Python, JS, Go...) and File Extension (.env, .json, .yml...).
- **Resumable**: If satisfied with partial results or if interrupted, you can Resume later.
- Can find 15,000+ results per query.

### 3. Verification (The Truth)
Scraped keys are stored as `Unverified`. You must verify them.
- Go to `2. Start Verifier`.
- Select `Verify All Unverified Keys` or a specific provider.
- The tool checks each key against the real provider API.
- **Status Codes**:
    - `Valid`: Key works!
    - `Unauthorized`: Key is invalid or revoked.
    - `QuotaExhausted`: Key is valid but out of credits (common for OpenAI).
    - `Error`: Network/System error.

### 4. Export & Reporting
- Go to `3. View Statistics / Export`.
- Export Valid keys to JSON or CSV.
- Use these reports for remediation or analysis.

---

## 🚀 Deployment Workflow

### Local Deployment (Windows/Linux)
Simply run the pre-compiled binary or `dotnet run` from the source code.

### Server / VPS Deployment
To deploy this on a VPS (e.g., Ubuntu, Windows Server) for 24/7 scraping:

1.  **Build Single-File Executable**:
    Run the publish command for your target OS (see `README.md` or `DEVELOPER_GUIDE.md`).

2.  **Upload**:
    SCP or copy the `unsecuredapikeys` binary to your server.

3.  **Run in Background (Linux Example)**:
    Use `screen` or `tmux` to keep it running:
    ```bash
    screen -S scraper
    ./unsecuredapikeys
    # (Press Ctrl+A, D to detach)
    ```

    *Note: Since the tool is interactive (TUI), it requires an attached terminal session. It does not currently run as a headless daemon service.*

### Docker Deployment (Coming Soon)
A Dockerfile is planned for future releases to allow containerized deployment.

---

## 🔄 Automatic Token Rotation

The system automatically handles GitHub rate limits:
1.  If Token A hits the secondary rate limit (API abuse detection) or primary limit (5000 requests/hr).
2.  The system marks Token A as `RateLimited` with a cooldown.
3.  It automatically switches to Token B.
4.  If all tokens are exhausted, it pauses and waits.

**Best Practice**: Add at least 3-4 GitHub tokens for uninterrupted Deep Search.
