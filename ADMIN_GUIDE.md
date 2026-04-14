# 👑 APIHunterV2: Admin Operation Guide

This guide covers the advanced management of the APIHunterV2 Master Node and its decentralized Ghost Node network.

---

### 🛡️ Master API Security
To protect your internal endpoints, Swagger UI is **disabled by default** in production.

#### Enabling Swagger (API Documentation)
If you need to view the API structure or test endpoints via the browser:
1. Go to your **Render Dashboard**.
2. Select your **Master Bot** service.
3. Add the following **Environment Variable**:
   - `SWAGGER_ENABLED` = `true`
4. Once the service restarts, Swagger will be available at your root URL (e.g., `https://your-bot.onrender.com/`).

> [!WARNING]
> Keep Swagger disabled unless you are actively debugging. Public exposure of your API structure can be a security risk.

---

### 🛰️ Ghost Node Management
Your system uses a Master-Worker architecture. As Admin, you oversee the entire "Ghost Node" network.

#### Monitoring the Network
- Use **`/node_status`** in the Telegram Bot.
- **🟢 Online**: Node is active and sent a heartbeat within the last 10 minutes.
- **🔴 Offline**: Node has lost connection or stopped running.
- **Immediate Response**: New workers now report as "Online" within seconds of deployment.

#### Provisioning New Nodes
1. Run **`/add_sub <telegramId> <days>`** to grant access.
2. The bot will automatically send the user instructions and their **Node Token**.
3. Direct them to the **SUBSCRIBER_GUIDE.md** for technical setup.

---

### 👥 User & Access Control
- **`/add_sub <id> <days>`**: Grants/Extends scraping access.
- **`/remove_sub <id>`**: Instantly revokes all access (Worker will fail to sync).
- **`/list_subs`**: View all registered users and their expiry dates.
- **`/set_admin <id> true`**: Add another admin to help manage the bot.

---

### 💾 Database Management
The Master node supports the following configurations:
- **PostgreSQL (Highly Recommended)**: Use `POSTGRES_CONNECTION_STRING`.
- **SQLite (Fallback)**: Uses `unsecuredapikeys.db` if no Postgres string is found.

#### Maintenance
- **`/export json`**: Export all valid keys found by the network.
- **`/reset_database CONFIRM_RESET`**: Completely wipes and re-initializes the database.

---
