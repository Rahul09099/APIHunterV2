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

### 📡 Network Monitoring
You have multiple ways to monitor the health and throughput of your Ghost Node network.

> [!TIP]
> **Complete Management Guide**: See the new **[ADMIN_GHOST_GUIDE.md](ADMIN_GHOST_GUIDE.md)** for detailed dashboard and Keep-Alive instructions.

#### 💎 Premium Visual Dashboard
For a high-end visual overview of the entire network:
- **URL**: `https://your-bot.onrender.com/dashboard`
- **Features**: Live worker counts, discovery totals, and real-time "Pulse" monitoring. 

#### 📡 Health Endpoints
- **Master Health**: `https://your-bot.onrender.com/health` (Returns JSON status)
- **JSON Stats**: `https://your-bot.onrender.com/api/v1/nodes/stats` (Raw data for external tools)

---

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
