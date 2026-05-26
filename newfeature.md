# New Feature: Server & Credential Detection (SSH, FTP, DB)

## 1. Objective
Expand **APIHunterV2** beyond API keys to detect and verify unsecured server credentials found in code, configuration files, and environment variables.

---

## 2. Requirements

### 🔍 Detection (Scraper)
The scraper must identify the following patterns:
- **SSH Credentials:**
    - Format: `ssh user@<IP_OR_HOST>`
    - Private Keys: `-----BEGIN RSA PRIVATE KEY-----`
- **FTP/SFTP:**
    - Format: `ftp://<USER>:<PASS>@<IP_OR_HOST>`
    - Format: `sftp://<USER>:<PASS>@<IP_OR_HOST>`
- **Database Connection Strings:**
    - **MySQL/PostgreSQL/MongoDB** (Standard formats)
    - **Redis:** `redis://[:<PASS>@]<IP>:<PORT>`
    - **ElasticSearch:** `http://<USER>:<PASS>@<IP>:9200`
    - **RabbitMQ:** `amqp://<USER>:<PASS>@<IP>:<PORT>`
- **Cloud & Container Orchestration:**
    - **Kubernetes:** `KUBERNETES_SERVICE_HOST`, `KubeConfig` files.
    - **Docker:** `DOCKER_HOST=tcp://<IP>:<PORT>`.
- **Cloud Metadata Detection:**
    - Automatically check IP ranges for **AWS**, **Azure**, **GCP**, **DigitalOcean**, **Linode**, **Vultr**, **Hetzner**, and **Oracle Cloud**.
    - **Geolocation:** Capture the **Country**, **City**, and **ISP** associated with the IP address.

### ✅ Verification (Verifier)
The verifier should perform a multi-stage check:
1.  **Network Check:** Ping the IP or attempt a TCP connection to the specific port (22 for SSH, 21 for FTP, 3306 for MySQL).
2.  **Authentication Test (Optional/Safe):** Attempt a single login attempt to verify the credentials.
3.  **Metadata Extraction:** Identify OS version, Banner information, and Database names if possible.

---

## 4. Advanced Research & Professional Techniques

### 🕵️‍♂️ Advanced Dorking (Search Queries)
To find these credentials "officially" and at scale, the scraper should use specialized search queries:
- **Terminal History:** `filename:.bash_history` or `filename:.zsh_history` (Often contains full `ssh` or `mysql` login commands).
- **Hardcoded Secrets:** `path:config filename:config.json "password"` or `filename:id_rsa` (Leaked private keys).
- **Environment Leaks:** `extension:env "DB_PASSWORD"` or `filename:docker-compose.yml`.

### 🌐 OSINT Integration (Shodan/Censys/GreyNoise)
- **Shodan/Censys:** Check IP history and SSL certificate metadata.
- **GreyNoise Integration (New):** Automatically check if an IP is a **Honeypot** (security trap) to avoid wasting time or getting flagged.

### 📉 Entropy & Context Detection
- **Shannon Entropy:** Implement a check for high-randomness strings (passwords).
- **Surrounding Context:** When an IP is found, scrape 10 lines of code above/below to find `user`, `admin`, or `root` labels.

---

## 5. Pro-Level Security Insights

### 🗺️ Source Map Analysis
Detect and parse `.js.map` files. These often contain the **original, un-minified source code** of a web app, where developers frequently leave hardcoded server endpoints.

### 🗄️ SQL Dump & Backup Scanning
Add detection for `.sql`, `.bak`, and `.dump` files. These are "treasure chests" that often contain:
- `INSERT INTO admin_users ...`
- Database configuration blocks with raw IPs and passwords.

### 🛡️ Safety: Anti-Lockout Logic
Implement a "safe verification" mode that ensures only ONE login attempt is made per server to avoid account lockouts or triggering IDPS alerts.

---

## 8. Security & Ethics Note
This feature is intended for **authorized security auditing and educational research** only. Users must ensure they have permission to scan the target infrastructure.

