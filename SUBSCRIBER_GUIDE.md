# 👻 Ghost Node: Subscriber Deployment Guide

Thank you for subscribing to **APIHunterV2**. This guide will help you set up your own private "Ghost Node" on your Render account. 

### Why set up a Ghost Node?
- **Speed**: Your scrapes run on your own dedicated instance.
- **Privacy**: Your discovery tasks are isolated.
- **Independence**: You use your own GitHub tokens to avoid rate limits from other users.

---

### Step 1: Get Your Credentials
Open the Telegram bot and run these two commands to get your secret connection info:
1. `/node_token` - Copy the long secret code.
2. `/master_url` - Copy the API address.

---

### Step 2: Deploy to Render
1. Log in to your **[Render Dashboard](https://dashboard.render.com/)**.
2. Click **New +** and select **Web Service**.
3. Choose **"Deploy an existing image from a registry"**.
4. Enter the Image URL provided by the Admin: `rahul09099/apihunter-worker:latest`.
5. Click **Next**.

---

### Step 3: Configure Environment Variables
In the "Environment" tab of your Render service, add the following variables:

| Key | Value | Description |
| :--- | :--- | :--- |
| `IS_WORKER_MODE` | `true` | Required. |
| `MASTER_API_URL` | (Your Master URL) | From `/master_url` command. |
| `NODE_TOKEN` | (Your Node Token) | From `/node_token` command. |
| `PORT` | `10000` | **Required.** Fixes port scan timeouts. |
| **`WORKER_GITHUB_TOKENS`** | `ghp_xxx,ghp_yyy` | **Optional.** Paste your own GitHub tokens here (comma-separated) for 10x faster scraping! |

> [!TIP]
> Using your own tokens in `WORKER_GITHUB_TOKENS` ensures your node is never rate-limited and runs at maximum speed.

---

---

### Step 4: Manage Your Dashboard (Subscriber Section)
As a subscriber, you have your own private dashboard via the bot! Use these commands to optimize your own Ghost Node.

#### 🔑 Personal Token Management
Your node uses tokens to bypass scraping limits. You can add your own private GitHub tokens to make your specific node run **10x faster**:
1. **`/add_token <your_github_token>`** - Add your own token to your node.
2. **`/tokens`** - List all tokens you have added.
3. **`/delete_token <id>`** - Delete your own token if it expires.

> [!NOTE]
> Tokens you add are private. Other subscribers cannot see or use them.

---

#### 🚀 One-Click Telegram Redeployment
When the Admin updates the software, you can trigger your node to update itself directly from Telegram in 5 seconds:
1. **Get Your Deploy Hook**:
   - Go to your Render Dashboard.
   - Open your Ghost Node web service.
   - Click the **Settings** tab on the left.
   - Scroll down to **Deploy Hook** and copy the private URL.
2. **Register it in the Telegram Bot**:
   - Run: **`/set_deploy_hook <your_copied_url>`**
3. **Redeploy Anytime**:
   - Run: **`/redeploy_node`** (Render will pull the newest image and deploy it immediately).
   - Run: **`/remove_deploy_hook`** if you wish to clear it.

---

### Step 5: Verify Your Connection
Once Render shows "Live", go back to the Telegram Bot:
- Run **`/node_status`** (if enabled) or **`/status`**.
- You should see your Node showing as **🟢 Online** instantly!
- Any keys found by your node are automatically credited to you.

---

**⚠️ Troubleshooting**: If your node shows as "Offline", double-check that your `MASTER_API_URL` and `NODE_TOKEN` are exactly as provided by the bot.
