# 👑 APIHunterV2: Admin Ghost Management Guide

This guide is for the **Super Admin** to manage the decentralized network and monitor global performance.

---

### 💎 1. Visual Network Dashboard
Your system now features a premium, real-time glassmorphic dashboard.
- **Access Link**: `https://YOUR_BOT_URL/dashboard`
- **What it monitors**: 
  - **Active Ghost Nodes**: Live count of workers currently reporting heartbeats.
  - **Keys Discovered**: Total unique API keys found across the entire network.
  - **Search Spectrum**: Count of active search queries being processed.
  - **Last Signal**: Timestamp of the last heartbeat received from any node.

---

### 📡 2. Automated Keep-Alive Service
Your Master Node now automatically maintains the health of the entire network.
- **How it works**: Every **14 minutes**, the Master Node sends an "inbound" health ping to every active worker URL.
- **The Benefit**: This prevents the workers (hosted on Render Free Tier) from ever going to sleep. Your network stays **Online 24/7** without manual intervention.
- **Configuration**: Ensure your Master service is a "Web Service" so it can host this background task.

---

### 🤖 3. Master Bot Commands (Admin Only)
Use these commands in your Telegram Bot to control the network:
| Command | Action |
| :--- | :--- |
| `/add_sub <id> <days>` | Register a new subscriber/worker node. |
| `/remove_sub <id>` | Instantly revoke access (Node will stop syncing). |
| `/list_subs` | View all subscribers and their node status. |
| `/node_status` | See a detailed view of every node (Online/Offline). |
| `/export json` | Download all discovered keys in a clean JSON format. |

---

### 🛠️ 4. Master Node Configuration
| Env Variable | Value | Description |
| :--- | :--- | :--- |
| `POSTGRES_CONNECTION_STRING` | (Link) | Connect your Supabase/Postgres DB here. |
| `SWAGGER_ENABLED` | `true` | Enables API documentation at the root URL. |
| `IS_WORKER_MODE` | `false` | Must be false for the Master Bot. |

---

### 🛡️ Troubleshooting
- **Node is Offline**: Ensure the subscriber has valid days and their `NODE_TOKEN` is correct.
- **Dashboard Empty**: Verify `SWAGGER_ENABLED` or check that workers are actually heartbeating to the correct `MASTER_API_URL`.
