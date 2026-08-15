# 👑 APIHunterV2: Admin Ghost Management Guide

This guide is for the **Super Admin** to manage the decentralized network and monitor global performance.

---

### 💎 1. Visual Network Dashboard
Your system features a real-time glassmorphic dashboard.
- **Access Link**: `https://YOUR_BOT_URL/dashboard`
- **What it monitors**: 
  - **Active Ghost Nodes**: Live count of workers currently reporting heartbeats.
  - **Keys Discovered**: Total unique API keys found across the entire network.
  - **Search Spectrum**: Count of active search queries being processed.
  - **Last Signal**: Timestamp of the last heartbeat received from any node.

---

### 📡 2. Automated Keep-Alive Service
Your Master Node automatically maintains the health of the entire network.
- **How it works**: Every **14 minutes**, the Master Node sends an "inbound" health ping to every active worker URL.
- **The Benefit**: This prevents workers on free hosting tiers from sleeping. Your network stays **Online 24/7** without manual intervention.

---

### 🤖 3. Master Bot Commands (Admin Only)
| Command | Action |
| :--- | :--- |
| `/add_sub <id> <days>` | Register a new subscriber/worker node. |
| `/remove_sub <id>` | Instantly revoke access (Node will stop syncing). |
| `/list_subs` | View all subscribers, open their user dashboards, and see their Deploy Hook status. |
| `/node_status` | See a detailed view of every node (Online/Offline, Heartbeat, and `Deploy Hook: ✅/❌`). |
| `/redeploy_all` | Trigger mass updates for all nodes with registered Deploy Hooks in parallel. |
| `/export json` | Download all discovered keys in a clean JSON format. |

---

### 🔍 4. How Admin Checks If a Subscriber Has Configured Their Deploy Hook
Admin has two quick ways to check:
1. **Network View (`/node_status`)**:
   Shows every subscriber node along with:
   - **Status:** `🟢 Online` / `🔴 Offline`
   - **Deploy Hook:** `✅ Configured` or `❌ Not Set`
2. **User Dashboard (`/list_subs` $\rightarrow$ Select User)**:
   Shows the subscriber's detailed dossier, including:
   - Subscription expiry
   - Discovered keys count
   - Configured GitHub tokens
   - **Deploy Hook:** `✅ Configured` or `❌ Not Set`

---

### ⚡ 5. Network Mass Redeployment (One-Click Auto-Updates)
When you build and push a new worker Docker image (`rahul09099/apihunter-worker:latest` via `publish_worker.bat`), you can instantly update the entire decentralized network from Telegram:
- Run **`/redeploy_all`** (or **`/deploy_workers`**): The Master Bot will execute parallel HTTP requests to all registered subscriber Deploy Hooks (Render, Railway, Koyeb, or custom webhooks).
- The bot displays a live completion report showing which nodes were triggered successfully.
- For nodes hosted on VPS/Docker using **Watchtower**, they update automatically without needing a webhook.

---

### 🛠️ 6. Master Node Configuration
| Env Variable | Value | Description |
| :--- | :--- | :--- |
| `POSTGRES_CONNECTION_STRING` | (Link) | Connect your Supabase/Postgres DB here. |
| `SWAGGER_ENABLED` | `true` | Enables API documentation at the root URL. |
| `IS_WORKER_MODE` | `false` | Must be false for the Master Bot. |

---

### 🛡️ Troubleshooting
- **Node is Offline**: Ensure the subscriber has valid days and their `NODE_TOKEN` is correct.
- **Deploy Hook shows ❌ Not Set**: Remind the subscriber to copy their Render Deploy Hook from Settings and run `/set_deploy_hook <url>`.
