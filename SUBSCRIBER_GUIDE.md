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

| Key | Value |
| :--- | :--- |
| `IS_WORKER_MODE` | `true` |
| `MASTER_API_URL` | (Paste the URL from `/master_url`) |
| `NODE_TOKEN` | (Paste the token from `/node_token`) |
| `PORT` | `10000` |

*Note: You do NOT need to set a Database path or Telegram Token. The worker node handles everything automatically.*

---

### Step 4: Verify Your Connection
Once Render shows "Live", go back to the Telegram Bot:
- Run `/status`.
- You should see your Node ID appearing as **🟢 Online**.
- Any keys found by this node will automatically be saved to your private dashboard.

---

**⚠️ Troubleshooting**: If your node shows as "Offline", double-check that your `MASTER_API_URL` and `NODE_TOKEN` are exactly as provided by the bot.
