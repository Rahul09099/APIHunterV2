# APIHunterV2: Distributed Scavenging Network (v3.0)

Welcome to the documentation for the enterprise-grade **Distributed Private Pool** system. This architecture allows you to scale your API key discovery across hundreds of independent servers while maintaining absolute control.

---

## 🏗️ System Architecture

### 1. The Master Node (Your Central Hub)
The Master Node is the "Brain" of the operation. It hosts your Telegram Bot, your primary database, and the **Master API**.
- **Responsibilities**: Authentication, Subscription management, UI (Telegram), and long-term data storage.
- **Hosting**: Render (Web Service).

### 2. The Worker Nodes ("Ghost Nodes")
Worker nodes are the "Muscle." They are deployed by your subscribers on their own accounts.
- **Responsibilities**: Performing high-intensity GitHub scraping and reporting discoveries back to the Master.
- **Pros**: Zero memory usage on your Master node, infinite scalability, and source code protection (Docker-based).
- **Control**: They are "tethered" to the Master. If a sub runs out, the node stops working automatically.

---

## 💎 Private Pool & Token Isolation

APIHunterV2 uses a **"Bring Your Own Infrastructure"** model:
- **Keys**: Every key found is tagged with the user's ID. Subscribers see their discoveries; you see everyone's.
- **Tokens**: Subscribers add their own GitHub tokens. A subscriber's scraper **only** uses their tokens. This prevents different users from competing for the same API rate limits.

---

## 🕹️ Telegram Commands Reference

### 👤 User Commands (Subscribers)
| Command | Description |
| :--- | :--- |
| `/id` | Get your Telegram ID (send this to Admin for sub). |
| `/my_sub` | Check your subscription expiry date. |
| `/status` | View global system health and your personal stats. |
| `/stats` | Personal scoreboard of valid keys found. |
| `/tokens` | Manage your own GitHub tokens. |
| `/add_token` | Add a new GitHub token to your private pool. |
| `/start_scraper`| Launch a new hunt using your tokens. |
| `/export` | Download your discovered keys as an Excel/JSON file. |
| `/node_token` | Get your secret key to host your own Ghost Node. |
| `/master_url` | Get the address to connect your Ghost Node to. |

### 🛠️ Admin Commands (You)
| Command | Description |
| :--- | :--- |
| `/add_sub <id> <days>` | Grant or extend a user's subscription. |
| `/remove_sub <id>` | Immediately revoke a user's access. |
| `/list_subs` | See all registered users and their status. |
| `/set_admin <id>` | Promote a user to Admin (God Mode). |
| `/node_status` | View a dashboard of all Online/Offline worker nodes. |
| `/queries` | Manage the system's global discovery targets. |

---

## 🚀 Deployment Guide: Setting up a Worker Node

Give these steps to your subscribers when they want to host their own node:

1. **Get Credentials**: Run `/node_token` and `/master_url` in the bot.
2. **Deploy to Render**:
   - Create a new **Web Service**.
   - Select "Deploy from Docker Image".
   - Use your Image URL (e.g., `ghcr.io/yourname/apihunter-worker:latest`).
3. **Set Environment Variables**:
   - `IS_WORKER_MODE`: `true`
   - `MASTER_API_URL`: (Paste from `/master_url`)
   - `NODE_TOKEN`: (Paste from `/node_token`)
   - `PORT`: `10000`
4. **Launch**: Once deployed, the node will appear as **Online** in the bot, and all keys it finds will automatically appear in the subscriber's private pool on your Master.

---

## 🛡️ Security & Optimization

- **Source Protection**: Always distribute the **Docker Image**, never the `.cs` source files. This prevents subscribers from stealing your hunting logic.
- **RAM Management**: The system is optimized with automatic memory clearing after every search cycle to prevent `OutOfMemory` errors on small hosting tiers.
