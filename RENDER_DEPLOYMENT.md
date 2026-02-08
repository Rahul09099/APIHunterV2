# Deploying UnsecuredAPIKeys Web API to Render

This guide will walk you through deploying the UnsecuredAPIKeys Web API to Render.

## 📋 Prerequisites

1. **GitHub Account** - Required for deploying to Render
2. **Render Account** - Sign up at [https://render.com](https://render.com) (free tier available)
3. **Git** - Installed on your local machine

---

## 🚀 Step-by-Step Deployment

### Step 1: Push Your Code to GitHub

1. **Initialize Git repository** (if not already done):
   ```powershell
   cd "c:\Users\rk170\Desktop\UnsecuredAPIKeys (Done)"
   git init
   git add .
   git commit -m "Initial commit - Web API for Render deployment"
   ```

2. **Create a new repository on GitHub**:
   - Go to [https://github.com/new](https://github.com/new)
   - Name it `UnsecuredAPIKeys` or any name you prefer
   - Make it **Private** (recommended for security)
   - **Do not** initialize with README, .gitignore, or license

3. **Push to GitHub**:
   ```powershell
   git remote add origin https://github.com/YOUR_USERNAME/YOUR_REPO_NAME.git
   git branch -M main
   git push -u origin main
   ```

---

### Step 2: Deploy to Render

1. **Log in to Render**:
   - Go to [https://dashboard.render.com](https://dashboard.render.com)
   - Sign in or create a new account

2. **Create a New Web Service**:
   - Click **"New +"** button in the top right
   - Select **"Web Service"**

3. **Connect Your Repository**:
   - Click **"Connect GitHub"** (or GitLab if you used that)
   - Authorize Render to access your repositories
   - Find and select your `UnsecuredAPIKeys` repository

4. **Configure the Service**:
   - **Name**: `unsecuredapikeys-api` (or any name)
   - **Region**: Choose closest to you (e.g., Oregon, Frankfurt)
   - **Branch**: `main`
   - **Runtime**: **Docker**
   - **Instance Type**: **Free** (or Starter for better performance)

5. **Environment Variables**:
   Render will automatically detect the `render.yaml`, but you can also add these manually:
   - `ASPNETCORE_ENVIRONMENT` = `Production`
   - `DATABASE_PATH` = `/app/data/unsecuredapikeys.db`
   - `TELEGRAM_BOT_TOKEN` = `your_bot_token_here`
   - `TELEGRAM_ADMIN_CHAT_ID` = `your_chat_id_here`

6. **Advanced Settings** (Optional but Recommended):
   - **Health Check Path**: `/health`
   - **Auto-Deploy**: Enable (automatically deploys on git push)

7. **Add Persistent Disk** (Important for Database):
   - Scroll to **"Disks"** section
   - Click **"Add Disk"**
   - **Name**: `database`
   - **Mount Path**: `/app/data`
   - **Size**: `1 GB` (Free tier allows up to 1GB)

8. **Create Web Service**:
   - Click **"Create Web Service"**
   - Render will start building and deploying your application

---

### Step 3: Monitor Deployment

1. **Watch the Build Logs**:
   - Render will show live build logs
   - The build process takes 5-10 minutes on first deployment
   - Look for: `==> Build successful`
   - Then: `==> Starting service`

2. **Access Your API**:
   - Once deployed, Render provides a URL like:
     ```
     https://unsecuredapikeys-api.onrender.com
     ```
   - Visit this URL to see the Swagger UI (API documentation)

---

## 🧪 Testing Your Deployed API

### Using Swagger UI (Easiest)

1. Open your Render URL in a browser: `https://your-app.onrender.com`
2. You'll see the Swagger UI with all available endpoints
3. Click **"Try it out"** on any endpoint to test it

### Using cURL

```bash
# Check health
curl https://your-app.onrender.com/health

# Get status
curl https://your-app.onrender.com/api/status

# Start scraper
curl -X POST https://your-app.onrender.com/api/scraper/start

# Add GitHub token
curl -X POST https://your-app.onrender.com/api/config/github-token \
  -H "Content-Type: application/json" \
  -d '{"token": "ghp_your_github_token_here"}'

# Start verifier for OpenAI keys
curl -X POST "https://your-app.onrender.com/api/verifier/start?apiTypes=OpenAI"

# Get valid keys
curl https://your-app.onrender.com/api/status/valid-keys

# Export keys as JSON
curl https://your-app.onrender.com/api/config/export-keys

# Export keys as CSV
curl https://your-app.onrender.com/api/config/export-keys?format=csv
```

---

## 📡 API Endpoints Overview

### **Scraper Endpoints**
- `POST /api/scraper/start` - Start scraping GitHub
- `POST /api/scraper/stop/{jobId}` - Stop a scraper job
- `GET /api/scraper/status/{jobId}` - Get job status
- `GET /api/scraper/jobs` - List all scraper jobs

### **Verifier Endpoints**
- `POST /api/verifier/start?apiTypes=OpenAI,Anthropic` - Start verification
- `POST /api/verifier/stop/{jobId}` - Stop a verifier job
- `GET /api/verifier/status/{jobId}` - Get job status
- `GET /api/verifier/jobs` - List all verifier jobs
- `GET /api/verifier/api-types` - List available API types

### **Status Endpoints**
- `GET /api/status` - Overall statistics
- `GET /api/status/detailed` - Detailed statistics by category
- `GET /api/status/api-type/{apiType}` - Stats for specific API type
- `GET /api/status/recent-keys?limit=100` - Recent keys
- `GET /api/status/valid-keys` - Valid keys count
- `GET /api/status/github-tokens` - GitHub tokens status
- `GET /api/status/search-queries` - Search queries

### **Configuration Endpoints**
- `POST /api/config/github-token` - Add GitHub token
- `DELETE /api/config/github-token/{id}` - Delete token
- `POST /api/config/search-query` - Add search query
- `DELETE /api/config/search-query/{id}` - Delete query
- `PATCH /api/config/search-query/{id}/toggle` - Enable/disable query
- `GET /api/config/export-keys?format=json` - Export valid keys
- `POST /api/config/reset-database` - Reset database (requires confirmation)

### **Health Check**
- `GET /health` - Health check endpoint

---

## 🔧 Configuration

### Adding GitHub Tokens

You **must** add at least one GitHub token before scraping:

```bash
curl -X POST https://your-app.onrender.com/api/config/github-token \
  -H "Content-Type: application/json" \
  -d '{"token": "ghp_YourGitHubPersonalAccessToken"}'
```

**How to get a GitHub token:**
1. Go to [GitHub Settings > Developer Settings > Personal Access Tokens](https://github.com/settings/tokens)
2. Click "Generate new token (classic)"
3. Give it a name and select **no scopes** (public access only)
4. Copy the token and add it via the API

---

## 💾 Database Persistence

Your database is stored on Render's persistent disk at `/app/data/unsecuredapikeys.db`.

**Important Notes:**
- Free tier: 1GB disk included
- Data persists across deployments
- Backup: Use the export API to download keys regularly

---

## 🛠️ Troubleshooting

### Build Fails

- **Check Dockerfile syntax**: Ensure no typos
- **Check logs**: Render shows detailed build logs
- **Dependencies**: Make sure all .csproj files are correct

### Service Won't Start

- **Check health endpoint**: Make sure `/health` is accessible
- **Check environment variables**: Verify all required vars are set
- **Check logs**: Click "Logs" tab in Render dashboard

### Database Issues

- **Reset database**: Use the reset endpoint with confirmation
- **Check disk**: Ensure persistent disk is mounted at `/app/data`

### Scraper Not Finding Keys

- **Add GitHub tokens**: At least one token is required
- **Check rate limits**: GitHub has API limits
- **Check logs**: Monitor scraper job status

---

## 📊 Monitoring

### View Logs
```bash
# In Render Dashboard:
1. Go to your service
2. Click "Logs" tab
3. Live logs will stream
```

### Monitor Jobs
```bash
# Get all active jobs
curl https://your-app.onrender.com/api/scraper/jobs

# Check specific job status
curl https://your-app.onrender.com/api/scraper/status/{jobId}
```

---

## 🔄 Updating Your Deployment

When you make changes to your code:

```powershell
git add .
git commit -m "Your change description"
git push origin main
```

Render will automatically rebuild and redeploy (if auto-deploy is enabled).

---

## 💰 Render Free Tier Limits

- **750 hours/month** of runtime (enough for 24/7 operation)
- **1GB** persistent disk
- Services **spin down after 15 minutes** of inactivity
- **Cold start**: Takes ~30 seconds to wake up

**To keep service always-on**: Upgrade to Starter ($7/month) plan.

---

## 🔒 Security Recommendations

1. **Keep repository private** on GitHub
2. **Use environment variables** for sensitive data
3. **Add authentication** if exposing publicly (future enhancement)
4. **Regularly export and backup** your valid keys
5. **Monitor access logs** on Render dashboard

---

## 🎉 Typical Workflow

1. **Add GitHub tokens** via API
2. **Start scraper** to find API keys
3. **Monitor progress** via status endpoints
4. **Start verifier** to validate found keys
5. **Export valid keys** for use
6. **Schedule regular runs** or keep scraper running continuously

---

## 📞 Support

- **Render Documentation**: [https://render.com/docs](https://render.com/docs)
- **API Documentation**: Available at your Render URL (Swagger UI)
- **GitHub Issues**: Create an issue in your repository

---

## 🚀 Next Steps

- [ ] Deploy to Render
- [ ] Add your GitHub tokens
- [ ] Test scraper and verifier
- [ ] Set up monitoring
- [ ] Configure backup strategy
- [ ] (Optional) Add a frontend UI

**Your API is now ready to use!** Access the Swagger UI at your Render URL to explore all endpoints.

---

## 🤖 Telegram Bot Control

You can control the scraper and verifier directly from Telegram. This is the recommended way to manage your service when hosted on Render.

### Setting up the Bot

1. **Create a Bot**: Message [@BotFather](https://t.me/botfather) on Telegram and follow steps to create a new bot.
2. **Get Token**: Copy the API Token provided by BotFather.
3. **Get Your Chat ID**: Message [@userinfobot](https://t.me/userinfobot) to get your numeric Chat ID.
4. **Configure Render**: Add `TELEGRAM_BOT_TOKEN` and `TELEGRAM_ADMIN_CHAT_ID` to your Render service environment variables.

### Available Commands

- `/status` - Check overall system status and active jobs.
- `/stats` - View detailed statistics by API category.
- `/start_scraper` - Interactive prompt to choose a provider group and mode (Lite/Deep).
- `/start_verifier` - Start verifying all found keys.
- `/valid_keys` - View count of verified valid keys.
- `/export` - Send a CSV/JSON file of all valid keys directly to your Telegram chat.
- `/help` - Show all available commands.
