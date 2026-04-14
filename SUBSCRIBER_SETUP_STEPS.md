# 🚀 Ghost Node: 2-Minute Setup Guide

Welcome to the **APIHunterV2** decentralized network. This guide will help you deploy your private worker node to Render.

---

### 📥 Step 1: Get Your Secrets
Open your Telegram Bot and copy these two values:
1.  Run **`/node_token`** -> Copy the secret token.
2.  Run **`/master_url`** -> Copy the URL.

---

### ☁️ Step 2: Deploy to Render
1.  Log in to **[Render.com](https://dashboard.render.com/)**.
2.  Click **New +** -> **Web Service**.
3.  Select **"Deploy an existing image from a registry"**.
4.  Enter the Official Image: `rahul09099/apihunter-worker:latest`.
5.  Set your Name (e.g., `my-ghost-node`).

---

### ⚙️ Step 3: Configure Environment
In the **Environment** tab of your service, add these **5 Required Variables**:

| Key | Value | Why? |
| :--- | :--- | :--- |
| `IS_WORKER_MODE` | `true` | Turns on Ghost Mode. |
| `MASTER_API_URL` | (Your Master URL) | Connects to the main bot. |
| `NODE_TOKEN` | (Your Node Token) | Authenticates your node. |
| `PORT` | `10000` | Prevents Render port scan timeout. |
| `RENDER_EXTERNAL_URL` | (Leave default) | Render sets this. Used for Keep-Alive. |

---

### 🚀 Step 4: Verify & Profit
Wait for Render to show **"Live"** (it takes ~1 minute).
1.  Go back to the Bot -> Run **`/node_status`**.
2.  You should see your Node showing as **🟢 Online (Worker)**.
3.  **Speed Up (Optional)**: Run **`/add_token <your_gh_token>`** to make your node scrape 10x faster using your own personal tokens!

---

### 🛡️ Why This Setup is Best:
*   **No Deployment Hassle**: Just an image link and a few variables.
*   **Automated Maintenance**: The Master node will automatically ping your node to keep it from sleeping.
*   **Total Privacy**: Your node runs in your account but contributes to your personal stats.
