# 🚀 Ghost Node: 2-Minute Setup Guide

Welcome to the **APIHunterV2** decentralized network. This guide will help you deploy your private worker node to Render (or any cloud/VPS platform).

---

### 📥 Step 1: Get Your Secrets from Telegram Bot
Open your Telegram Bot and run these two commands to copy your secret connection values:
1.  **`/node_token`** -> Copies your private worker authentication key.
2.  **`/master_url`** -> Copies the central Master API address.

---

### ☁️ Step 2: Deploy to Render (or Railway / Koyeb / VPS)
1.  Log in to **[Render.com](https://dashboard.render.com/)**.
2.  Click **New +** -> **Web Service**.
3.  Select **"Deploy an existing image from a registry"**.
4.  Enter the Official Image: `rahul09099/apihunter-worker:latest`.
5.  Set your Name (e.g., `my-ghost-node`).

---

### ⚙️ Step 3: Configure Environment Variables
In the **Environment** tab of your service, add these **Required Variables**:

| Key | Value | Description |
| :--- | :--- | :--- |
| `IS_WORKER_MODE` | `true` | Turns on Ghost Worker Mode. |
| `MASTER_API_URL` | (From `/master_url`) | Connects your worker to the main bot. |
| `NODE_TOKEN` | (From `/node_token`) | Authenticates your worker instance. |
| `PORT` | `10000` | Prevents cloud port scan timeouts. |
| `RENDER_EXTERNAL_URL` | (Leave default) | Render sets this automatically. Used for Keep-Alive. |

---

### 🔄 Step 4: Configure Deploy Hook for One-Click Auto-Updates
Register your Deploy Hook so you (and the Admin) can update your worker to the newest version directly from Telegram:
1.  In your **Render Dashboard** -> Open your Web Service -> Click **Settings**.
2.  Scroll down to the **Deploy Hook** section and copy the URL (`https://api.render.com/deploy/srv-xxx?key=yyy`).
3.  In the Telegram bot, run: **`/set_deploy_hook <your_copied_url>`**
4.  To update your node in the future, simply run: **`/redeploy_node`**

*(If using Railway, Koyeb, or Custom Webhooks, paste your service deploy webhook into `/set_deploy_hook <url>`)*.

---

### 🏁 Step 5: Verify Your Node with `/node_status`
Wait for Render to show **"Live"** (takes ~1 minute).
1.  Go to the Telegram Bot -> Run **`/node_status`**.
2.  Your node will display:
    - **Status:** `🟢 Online` (Worker is actively heartbeating)
    - **Deploy Hook:** `✅ Configured` (One-click updates enabled)
    - **Last Heartbeat:** Timestamp of latest check-in.
3.  **Speed Up (Optional)**: Run **`/add_token <your_github_token>`** to make your node scrape 10x faster using your personal tokens!
