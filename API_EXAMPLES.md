# UnsecuredAPIKeys Web API - Quick Start Examples

This file contains quick examples for using the deployed API.

## Prerequisites
- API deployed on Render (e.g., `https://your-app.onrender.com`)
- Valid GitHub Personal Access Token

## 1. Health Check

```bash
curl https://your-app.onrender.com/health
```

**Response:**
```json
{
  "status": "healthy",
  "timestamp": "2026-02-01T18:00:00Z"
}
```

---

## 2. Add GitHub Token (Required First Step)

```bash
curl -X POST https://your-app.onrender.com/api/config/github-token \
  -H "Content-Type: application/json" \
  -d '{"token": "ghp_YOUR_GITHUB_TOKEN_HERE"}'
```

**Response:**
```json
{
  "message": "GitHub token added successfully",
  "tokenId": 1
}
```

---

## 3. Start Scraper

```bash
curl -X POST https://your-app.onrender.com/api/scraper/start
```

**Response:**
```json
{
  "message": "Scraper started successfully",
  "jobId": "a1b2c3d4-e5f6-7890-abcd-ef1234567890",
  "status": "Running"
}
```

---

## 4. Check Scraper Status

```bash
# Replace {jobId} with actual job ID from previous step
curl https://your-app.onrender.com/api/scraper/status/a1b2c3d4-e5f6-7890-abcd-ef1234567890
```

**Response:**
```json
{
  "jobId": "a1b2c3d4-e5f6-7890-abcd-ef1234567890",
  "jobType": "Scraper",
  "status": "Running",
  "startedAt": "2026-02-01T18:05:00Z",
  "completedAt": null,
  "error": null
}
```

---

## 5. Get Overall Status

```bash
curl https://your-app.onrender.com/api/status
```

**Response:**
```json
{
  "totalKeys": 150,
  "validKeys": 5,
  "invalidKeys": 100,
  "unverifiedKeys": 45,
  "quotaExhaustedKeys": 0,
  "gitHubTokensCount": 1,
  "timestamp": "2026-02-01T18:10:00Z"
}
```

---

## 6. Start Verifier (for all unverified keys)

```bash
curl -X POST https://your-app.onrender.com/api/verifier/start
```

**For specific API types:**
```bash
curl -X POST "https://your-app.onrender.com/api/verifier/start?apiTypes=OpenAI,Anthropic"
```

**Response:**
```json
{
  "message": "Verifier started successfully",
  "jobId": "b2c3d4e5-f6a7-8901-bcde-f23456789012",
  "apiTypes": "OpenAI, Anthropic",
  "status": "Running"
}
```

---

## 7. Get Valid Keys

```bash
curl https://your-app.onrender.com/api/status/valid-keys
```

**Response:**
```json
{
  "totalValid": 5,
  "byApiType": [
    {"apiType": "OpenAI", "count": 3},
    {"apiType": "Anthropic", "count": 2}
  ],
  "timestamp": "2026-02-01T18:15:00Z"
}
```

---

## 8. Export Valid Keys (JSON)

```bash
curl https://your-app.onrender.com/api/config/export-keys
```

**Response:**
```json
[
  {
    "apiType": "OpenAI",
    "apiKey": "sk-1234567890abcdef",
    "lastVerifiedAt": "2026-02-01T18:10:00Z",
    "createdAt": "2026-02-01T17:00:00Z"
  },
  ...
]
```

---

## 9. Export Valid Keys (CSV)

```bash
curl https://your-app.onrender.com/api/config/export-keys?format=csv -o valid_keys.csv
```

**Downloads CSV file:**
```csv
ApiType,ApiKey,LastVerifiedAt,CreatedAt
OpenAI,"sk-1234567890abcdef",2026-02-01T18:10:00Z,2026-02-01T17:00:00Z
...
```

---

## 10. Stop a Job

```bash
curl -X POST https://your-app.onrender.com/api/scraper/stop/a1b2c3d4-e5f6-7890-abcd-ef1234567890
```

**Response:**
```json
{
  "message": "Scraper stop requested",
  "jobId": "a1b2c3d4-e5f6-7890-abcd-ef1234567890"
}
```

---

## 11. Get Recent Keys

```bash
curl https://your-app.onrender.com/api/status/recent-keys?limit=50
```

**Response:**
```json
[
  {
    "id": 123,
    "apiType": "OpenAI",
    "verificationStatus": "Valid",
    "createdAt": "2026-02-01T18:00:00Z",
    "lastVerifiedAt": "2026-02-01T18:10:00Z",
    "keyPreview": "sk-1234567890abcdef..."
  },
  ...
]
```

---

## 12. Get Available API Types

```bash
curl https://your-app.onrender.com/api/verifier/api-types
```

**Response:**
```json
[
  {"name": "OpenAI", "value": 1, "category": "AI"},
  {"name": "Anthropic", "value": 2, "category": "AI"},
  {"name": "GoogleAI", "value": 3, "category": "AI"},
  ...
]
```

---

## 13. Add Custom Search Query

```bash
curl -X POST https://your-app.onrender.com/api/config/search-query \
  -H "Content-Type: application/json" \
  -d '{"query": "sk-proj AND extension:env"}'
```

**Response:**
```json
{
  "message": "Search query added successfully",
  "queryId": 5
}
```

---

## 14. Get All Jobs

```bash
# Get all scraper jobs
curl https://your-app.onrender.com/api/scraper/jobs

# Get all verifier jobs
curl https://your-app.onrender.com/api/verifier/jobs
```

---

## Complete Workflow Example

```bash
#!/bin/bash

API_URL="https://your-app.onrender.com"

# 1. Add GitHub token
echo "Adding GitHub token..."
curl -X POST $API_URL/api/config/github-token \
  -H "Content-Type: application/json" \
  -d '{"token": "ghp_YOUR_TOKEN_HERE"}'

# 2. Start scraper
echo "Starting scraper..."
SCRAPER_JOB=$(curl -s -X POST $API_URL/api/scraper/start | jq -r '.jobId')
echo "Scraper job ID: $SCRAPER_JOB"

# 3. Wait a bit
echo "Waiting for scraper to find keys..."
sleep 60

# 4. Check status
echo "Checking status..."
curl -s $API_URL/api/status | jq

# 5. Start verifier
echo "Starting verifier..."
VERIFIER_JOB=$(curl -s -X POST $API_URL/api/verifier/start | jq -r '.jobId')
echo "Verifier job ID: $VERIFIER_JOB"

# 6. Wait for verification
echo "Waiting for verification..."
sleep 60

# 7. Get valid keys
echo "Fetching valid keys..."
curl -s $API_URL/api/status/valid-keys | jq

# 8. Export to CSV
echo "Exporting to CSV..."
curl -s "$API_URL/api/config/export-keys?format=csv" -o valid_keys.csv
echo "Exported to valid_keys.csv"

# 9. Stop jobs
echo "Stopping jobs..."
curl -X POST $API_URL/api/scraper/stop/$SCRAPER_JOB
curl -X POST $API_URL/api/verifier/stop/$VERIFIER_JOB

echo "Done!"
```

---

## PowerShell Examples

```powershell
# Set your API URL
$ApiUrl = "https://your-app.onrender.com"

# 1. Add GitHub token
$body = @{ token = "ghp_YOUR_TOKEN_HERE" } | ConvertTo-Json
Invoke-RestMethod -Uri "$ApiUrl/api/config/github-token" -Method Post -Body $body -ContentType "application/json"

# 2. Start scraper
$scraperJob = Invoke-RestMethod -Uri "$ApiUrl/api/scraper/start" -Method Post
Write-Host "Scraper Job ID: $($scraperJob.jobId)"

# 3. Get status
Invoke-RestMethod -Uri "$ApiUrl/api/status"

# 4. Start verifier
$verifierJob = Invoke-RestMethod -Uri "$ApiUrl/api/verifier/start" -Method Post
Write-Host "Verifier Job ID: $($verifierJob.jobId)"

# 5. Export valid keys
Invoke-RestMethod -Uri "$ApiUrl/api/config/export-keys" -OutFile "valid_keys.json"

# 6. Export as CSV
Invoke-RestMethod -Uri "$ApiUrl/api/config/export-keys?format=csv" -OutFile "valid_keys.csv"
```

---

## Python Examples

```python
import requests
import time

API_URL = "https://your-app.onrender.com"

# 1. Add GitHub token
response = requests.post(
    f"{API_URL}/api/config/github-token",
    json={"token": "ghp_YOUR_TOKEN_HERE"}
)
print(response.json())

# 2. Start scraper
response = requests.post(f"{API_URL}/api/scraper/start")
scraper_job_id = response.json()["jobId"]
print(f"Scraper Job ID: {scraper_job_id}")

# 3. Wait and check status
time.sleep(60)
response = requests.get(f"{API_URL}/api/status")
print(response.json())

# 4. Start verifier for OpenAI only
response = requests.post(f"{API_URL}/api/verifier/start?apiTypes=OpenAI")
verifier_job_id = response.json()["jobId"]
print(f"Verifier Job ID: {verifier_job_id}")

# 5. Wait for verification
time.sleep(60)

# 6. Get valid keys
response = requests.get(f"{API_URL}/api/status/valid-keys")
print(response.json())

# 7. Export to file
response = requests.get(f"{API_URL}/api/config/export-keys")
with open("valid_keys.json", "w") as f:
    f.write(response.text)
```

---

## Notes

- Replace `https://your-app.onrender.com` with your actual Render URL
- Jobs run asynchronously in the background
- Use job IDs to track progress
- The scraper and verifier can run for hours/days depending on configuration
- Free tier Render services sleep after 15 minutes of inactivity (first request may be slow)

Enjoy using your API! 🚀
