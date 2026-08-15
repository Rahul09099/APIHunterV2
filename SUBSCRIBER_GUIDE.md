# 👻 Ghost Node: Subscriber Deployment Guide

Thank you for subscribing to **APIHunterV2**. This guide will help you set up your own private "Ghost Node" on your cloud hosting account (Render, Railway, Koyeb, Fly.io, or VPS). 

### Why set up a Ghost Node?
- **Speed**: Your scrapes run on your own dedicated instance.
- **Privacy**: Your discovery tasks are isolated.
- **Independence**: You use your own GitHub tokens to avoid rate limits from other users.

---

### Step 1: Get Your Credentials
Open the Telegram bot and run these two commands to get your secret connection info:
1. **`/node_token`** - Outputs your private authentication key.
2. **`/master_url`** - Outputs the central Master Bot endpoint URL.

---

### Step 2: Deploy to Your Cloud Platform

#### Option A: Render (Recommended & Easiest)
1. Log in to your **[Render Dashboard](https://dashboard.render.com/)**.
2. Click **New +** and select **Web Service**.
3. Choose **"Deploy an existing image from a registry"**.
4. Enter the Image URL provided by the Admin: `rahul09099/apihunter-worker:latest`.
5. In the "Environment" tab, add the environment variables listed in Step 3.

#### Option B: Railway / Koyeb / Fly.io / VPS
- **Railway**: Deploy using Docker Image `rahul09099/apihunter-worker:latest` and set the Environment variables.
- **Koyeb**: Create a service with Docker Image `rahul09099/apihunter-worker:latest`.
- **VPS / Docker Compose**:
  ```bash
  docker run -d --name ghost-worker --restart unless-stopped \
    -e IS_WORKER_MODE=true \
    -e MASTER_API_URL="<your_master_url>" \
    -e NODE_TOKEN="<your_node_token>" \
    -p 10000:10000 \
    rahul09099/apihunter-worker:latest
  ```

---

### Step 3: Configure Environment Variables

| Key | Value | Description | How to Get |
| :--- | :--- | :--- | :--- |
| `IS_WORKER_MODE` | `true` | Required. Enables worker mode. | Type `true` |
| `MASTER_API_URL` | (Your Master URL) | Endpoint of the master bot. | Run `/master_url` in Telegram |
| `NODE_TOKEN` | (Your Node Token) | Authenticates your node. | Run `/node_token` in Telegram |
| `PORT` | `10000` | **Required.** Fixes port scan timeouts on cloud platforms. | Type `10000` |
| **`WORKER_GITHUB_TOKENS`** | `ghp_xxx,ghp_yyy` | **Optional.** Paste your own GitHub tokens here for 10x faster scraping! | Paste comma-separated tokens |

> [!TIP]
> Using your own tokens in `WORKER_GITHUB_TOKENS` (or via `/add_token` in Telegram) ensures your node is never rate-limited and runs at maximum speed.

---

### Step 4: Configure One-Click Auto-Updates (Deploy Hook)

When the Admin updates the software, you can trigger your node to update itself directly from Telegram in 5 seconds.

#### If using Render:
1. **Get Your Deploy Hook**:
   - Go to your Render Dashboard.
   - Open your Ghost Node web service.
   - Click the **Settings** tab on the left.
   - Scroll down to **Deploy Hook** and copy the private URL (e.g. `https://api.render.com/deploy/srv-xxx?key=yyy`).
2. **Register it in the Telegram Bot**:
   - Run: **`/set_deploy_hook <your_copied_url>`**
3. **Redeploy Anytime**:
   - Run: **`/redeploy_node`** (Render will pull the newest image and deploy it immediately).

#### If using Railway / Koyeb / Custom Webhook:
- Copy your service's deploy trigger URL / webhook and register it with `/set_deploy_hook <url>`.

#### If using VPS / Docker with Watchtower:
- Run [Watchtower](https://containrrr.dev/watchtower/) alongside your worker container to automatically poll and update the container whenever a new Docker image is pushed by the Admin — no webhooks needed!
  ```bash
  docker run -d --name watchtower -v /var/run/docker.sock:/var/run/docker.sock containrrr/watchtower --interval 300 ghost-worker
  ```

---

### Step 5: Essential Commands for Subscribers

#### 📡 `/master_url` (How to access the Master address)
- **What it does**: Retrieves the live address where your worker connects.
- **When to use**: Whenever setting up a new worker instance or verifying connectivity.
- **Example output**:
  ```text
  📡 MASTER API ENDPOINT
  ⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯
  Connect your worker node to this address:
  https://api-hunter-master.onrender.com
  ```

#### 🛰️ `/node_status` (How to check your live node health)
- **What it does**: Verifies if your Ghost Node is actively checking in and reporting keys.
- **When to use**: After deploying to Render or whenever you want to confirm your worker is healthy.
- **Example output**:
  ```text
  🛰️ YOUR NODE STATUS
  ⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯
  Node: @your_username (123456789)
  Status: 🟢 Online
  Deploy Hook: ✅ Configured
  Last Heartbeat: 8/15/2026 4:04 PM
  ⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯⎯
  ```

#### 🔑 Other Useful Commands
- **`/node_token`** - View your private worker authentication key.
- **`/tokens`** - List private GitHub tokens you added to your node.
- **`/add_token <ghp_token>`** - Add private GitHub tokens for 10x faster scraping.
- **`/redeploy_node`** - Trigger an instant update for your worker instance.
- **`/my_sub`** - View your subscription expiry date and account info.

---

### 🛡️ Troubleshooting
- **Status is 🔴 Offline**:
  1. Verify in Render logs that `MASTER_API_URL` (from `/master_url`) and `NODE_TOKEN` (from `/node_token`) match exactly.
  2. Ensure `PORT=10000` is set in your Render Environment.
  3. Ensure `IS_WORKER_MODE=true` is set.
- **Deploy Hook shows ❌ Not Set**:
  Copy your URL from Render Settings and run `/set_deploy_hook <url>`.
