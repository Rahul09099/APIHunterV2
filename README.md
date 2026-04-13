# UnsecuredAPIKeys

A powerful, modular, and resumable tool to scrape GitHub for exposed API keys and verify their validity against real providers.

![License](https://img.shields.io/badge/license-MIT-blue.svg)
![Platform](https://img.shields.io/badge/platform-Windows%20%7C%20Linux-lightgrey.svg)
![Status](https://img.shields.io/badge/status-Active-green.svg)

## 🌟 Key Features

*   **Multi-Provider Support**: Detects and verifies keys for OpenAI, Anthropic, Google (Gemini), DeepSeek, Mistral, Pollo AI, Runway ML, Kling AI (Pro), and many more.
    *   **Resumable Deep Search**: 
        *   Bypasses GitHub's 1000-result limit using smart partitioning (Language + File Extension).
        *   **Tracks progress automatically**: Stop and resume scrapes at any time.
        *   Visual progress tables and status reporting.
*   **Lite Search**: Fast, targeted search for quick discovery.
*   **Multiple GitHub Tokens**: Configure multiple tokens for rotation and load balancing to avoid rate limits.
*   **Live Verification**: Validates keys against actual API endpoints to determine if they are `Valid`, `Unauthorized`, or `QuotaExhausted`.
*   **Google Leaked Key Detection**: Specifically identifies Google keys that are valid but flagged as "leaked" by Google.

## 🚀 Quick Start

### 1. Download
Download the latest release for your platform (Windows x64 recommended).

### 2. Run
```powershell
.\unsecuredapikeys.exe
```

### 3. Configure
Upon first run, select **4. Configure Settings** to:
1.  **Manage GitHub Tokens**: Add at least one GitHub Personal Access Token (Classic).
2.  **Manage Search Queries**: (Optional) Customize search patterns.

### 4. Scrape
Select **1. Start Scraper** -> Choose a Provider Group -> **Deep Search**.

## 📦 Supported Providers

| Provider | Detection Pattern | Verification |
| :--- | :--- | :--- |
| **OpenAI** | `sk-...` | ✅ Live |
| **Anthropic** | `sk-ant-...` | ✅ Live |
| **Google AI** | `AIza...` | ✅ Live |
| **DeepSeek** | `sk-...` | ✅ Live |
| **KlingAI** | Custom | ✅ Live |
| **Cohere** | Custom | ✅ Live |
| **ElevenLabs** | Custom | ✅ Live |
| **StabilityAI** | `sk-...` | ✅ Live |
| **Replicate** | `r8_...` | ✅ Live |
| **HuggingFace** | `hf_...` | ✅ Live |
| **Fireworks** | `fw_...` | ✅ Live |
| **TogetherAI** | Custom | ✅ Live |
| **xAI (Grok)** | Custom | ✅ Live |
| **Pollo AI** | `pollo_...` | ✅ Live |
| **Runway ML** | `key_...` | ✅ Live |
| **Kling AI** | AK:SK Pairs | ✅ Live (JWT) |

## 📚 Documentation

*   [**Developer Guide**](DEVELOPER_GUIDE.md): How to add new providers, build from source, and understand the architecture.
*   [**Workflow & Deployment**](WORKFLOW.md): Detailed workflows for usage, deployment, and best practices.

## ⚙️ Configuration

Start the application and verify your settings based on the interactive menu. configuration is stored in `unsecuredapikeys.db` (SQLite) in your local app data folder.

## ⚠️ Disclaimer

**For Educational and Security Research Purposes Only.**
This tool is intended to help developers and security researchers identify exposed secrets. Do not use this tool to exploit found credentials. Responsible disclosure is required.
