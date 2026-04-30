"""
API Key Verifier - Standalone Python Tool
=========================================
Ported from APIHunterV2 (C#) to Python for external verification.

Supported Providers:
- OpenAI, DeepSeek, Anthropic, Google (Gemini), ElevenLabs, PiAPI, Groq, 
- Mistral AI, Perplexity, RunwayML, A2E AI, OpenRouter, Together AI, 
- Cohere, Voyage AI, X.AI (Grok), HuggingFace, Replicate, Stability AI.

Usage:
------
1. Install requirements:
   pip install requests

2. Basic verification:
   python api_verifier.py input.json

3. Save ONLY valid keys (removes Invalid, Unauthorized, and QuotaExhausted):
   python api_verifier.py input.json --valid-only

4. Custom output and threads:
   python api_verifier.py input.json -o results.json -t 15 --valid-only

Arguments:
----------
input           : Path to the JSON file containing API keys.
-o, --output    : Output file path (default: verified_results.json).
-t, --threads   : Number of concurrent threads (default: 5).
--valid-only    : If set, only keys with active balance/quota are saved.
"""

import requests
import json
import re
import argparse
import time
import threading
from datetime import datetime
from concurrent.futures import ThreadPoolExecutor, as_completed
from abc import ABC, abstractmethod

# --- Common Utilities ---

print_lock = threading.Lock()

def locked_print(*args, **kwargs):
    with print_lock:
        print(*args, **kwargs)

class ValidationStatus:
    VALID = "Valid"
    INVALID = "Invalid"
    QUOTA_EXHAUSTED = "QuotaExhausted"
    RATE_LIMITED = "RateLimited"
    ERROR = "Error"
    UNAUTHORIZED = "Unauthorized"

class ValidationResult:
    def __init__(self, status, detail="", balance=None, tier=None, models=None, raw_response=None, metadata=None, http_status=None):
        self.status = status
        self.detail = detail
        self.balance = balance
        self.tier = tier
        self.models = models or []
        self.raw_response = raw_response
        self.metadata = metadata or {}
        self.http_status = http_status

    def to_dict(self):
        return {
            "status": self.status,
            "http_status": self.http_status,
            "detail": self.detail,
            "balance": self.balance,
            "tier": self.tier,
            "models": self.models,
            "metadata": self.metadata,
            "raw_response": self.raw_response
        }

# --- Base Provider ---

class BaseProvider(ABC):
    def __init__(self):
        self.timeout = 30

    @property
    @abstractmethod
    def provider_name(self):
        pass

    @property
    @abstractmethod
    def regex_patterns(self):
        pass

    def clean_key(self, api_key):
        api_key = api_key.strip()
        if api_key.lower().startswith("bearer "):
            api_key = api_key[7:].strip()
        elif api_key.lower().startswith("x-api-key:"):
            api_key = api_key[10:].strip()
        return api_key

    def matches(self, api_key):
        for pattern in self.regex_patterns:
            if re.search(pattern, api_key):
                return True
        return False

    @abstractmethod
    def validate(self, api_key):
        pass

    def _truncate(self, text, max_len=200):
        if not text: return ""
        return text[:max_len] + "..." if len(text) > max_len else text

    def _is_success(self, status_code):
        return 200 <= status_code < 300

    def _check_indicators(self, text, indicators):
        if not text: return False
        text_lower = text.lower()
        return any(ind.lower() in text_lower for ind in indicators)

    QUOTA_INDICATORS = ["quota", "balance", "insufficient", "credit", "limit", "billing", "exhausted"]
    AUTH_INDICATORS = ["invalid", "unauthorized", "authentication", "forbidden", "expired", "revoked"]

# --- Provider Implementations ---

class OpenAIProvider(BaseProvider):
    provider_name = "OpenAI"
    regex_patterns = [r"sk-[A-Za-z0-9\-]{20,}", r"sk-proj-[A-Za-z0-9\-]{20,}"]

    def validate(self, api_key):
        api_key = self.clean_key(api_key)
        headers = {"Authorization": f"Bearer {api_key}"}
        try:
            resp = requests.get("https://api.openai.com/v1/models", headers=headers, timeout=self.timeout)
            if resp.status_code == 401 or resp.status_code == 403:
                return ValidationResult(ValidationStatus.UNAUTHORIZED, "Invalid API Key", http_status=resp.status_code)
            if not self._is_success(resp.status_code):
                return ValidationResult(ValidationStatus.ERROR, f"Models fail: {resp.status_code}", http_status=resp.status_code, raw_response=resp.text)

            models_data = resp.json()
            models = [m['id'] for m in models_data.get('data', [])]
            preferred = ["gpt-4o-mini", "gpt-3.5-turbo", "gpt-4"]
            model_to_use = next((m for m in models if any(p in m for p in preferred)), models[0] if models else "gpt-3.5-turbo")
            
            chat_payload = {"model": model_to_use, "messages": [{"role": "user", "content": "hi"}], "max_tokens": 1}
            chat_resp = requests.post("https://api.openai.com/v1/chat/completions", headers=headers, json=chat_payload, timeout=self.timeout)
            
            if self._is_success(chat_resp.status_code):
                return ValidationResult(ValidationStatus.VALID, "Active key", models=models, tier="Unknown (OpenAI)", http_status=chat_resp.status_code, raw_response=chat_resp.text)
            if chat_resp.status_code == 429 or "insufficient_quota" in chat_resp.text:
                return ValidationResult(ValidationStatus.QUOTA_EXHAUSTED, "Valid but no quota", models=models, http_status=chat_resp.status_code, raw_response=chat_resp.text)
            return ValidationResult(ValidationStatus.ERROR, f"Chat fail: {chat_resp.status_code}", models=models, http_status=chat_resp.status_code, raw_response=chat_resp.text)
        except Exception as e:
            return ValidationResult(ValidationStatus.ERROR, f"Network error: {str(e)}")

class DeepSeekProvider(BaseProvider):
    provider_name = "DeepSeek"
    regex_patterns = [r"sk-[A-Za-z0-9]{32,}"]

    def validate(self, api_key):
        api_key = self.clean_key(api_key)
        headers = {"Authorization": f"Bearer {api_key}"}
        try:
            models = []
            m_resp = requests.get("https://api.deepseek.com/models", headers=headers, timeout=self.timeout)
            if self._is_success(m_resp.status_code):
                models = [m['id'] for m in m_resp.json().get('data', [])]

            bal_resp = requests.get("https://api.deepseek.com/user/balance", headers=headers, timeout=self.timeout)
            if bal_resp.status_code == 401:
                return ValidationResult(ValidationStatus.UNAUTHORIZED, "Invalid Key", http_status=bal_resp.status_code)
            
            if self._is_success(bal_resp.status_code):
                data = bal_resp.json()
                info = data.get('balance_infos', [{}])[0] if 'balance_infos' in data else data.get('data', {}).get('balance_infos', [{}])[0]
                total = info.get('total_balance', '0')
                currency = info.get('currency', 'USD')
                is_available = data.get('is_available', data.get('data', {}).get('is_available', True))
                status = ValidationStatus.VALID if is_available and float(total) > 0 else ValidationStatus.QUOTA_EXHAUSTED
                return ValidationResult(status, balance=f"{total} {currency}", tier="Paid" if float(info.get('topped_up_balance', 0)) > 0 else "Free", models=models, http_status=bal_resp.status_code, raw_response=bal_resp.text)
            return ValidationResult(ValidationStatus.ERROR, f"Status {bal_resp.status_code}", http_status=bal_resp.status_code, raw_response=bal_resp.text)
        except Exception as e:
            return ValidationResult(ValidationStatus.ERROR, str(e))

class AnthropicProvider(BaseProvider):
    provider_name = "Anthropic"
    regex_patterns = [r"sk-ant-api\d{2}-[a-zA-Z0-9\-_]{40,120}", r"sk-ant-[a-zA-Z0-9\-_]{20,120}"]

    def validate(self, api_key):
        api_key = self.clean_key(api_key)
        headers = {"x-api-key": api_key, "anthropic-version": "2023-06-01", "content-type": "application/json"}
        payload = {"model": "claude-3-haiku-20240307", "max_tokens": 1, "messages": [{"role": "user", "content": "1"}]}
        try:
            resp = requests.post("https://api.anthropic.com/v1/messages", headers=headers, json=payload, timeout=self.timeout)
            if resp.status_code == 401:
                return ValidationResult(ValidationStatus.UNAUTHORIZED, "Invalid Key", http_status=resp.status_code)
            if self._is_success(resp.status_code):
                return ValidationResult(ValidationStatus.VALID, "Active key", http_status=resp.status_code, raw_response=resp.text)
            if resp.status_code == 429 or resp.status_code == 402:
                return ValidationResult(ValidationStatus.QUOTA_EXHAUSTED, "Valid but no quota/credits", http_status=resp.status_code, raw_response=resp.text)
            return ValidationResult(ValidationStatus.ERROR, f"Status {resp.status_code}", http_status=resp.status_code, raw_response=resp.text)
        except Exception as e:
            return ValidationResult(ValidationStatus.ERROR, str(e))

class GoogleProvider(BaseProvider):
    provider_name = "Google"
    regex_patterns = [r"AIza[0-9A-Za-z\-_]{35,40}"]

    def validate(self, api_key):
        api_key = self.clean_key(api_key)
        try:
            url = f"https://generativelanguage.googleapis.com/v1/models?key={api_key}"
            resp = requests.get(url, timeout=self.timeout)
            if "leaked" in resp.text.lower():
                return ValidationResult(ValidationStatus.UNAUTHORIZED, "Key reported as leaked", http_status=resp.status_code, raw_response=resp.text)
            if resp.status_code == 401 or resp.status_code == 403:
                return ValidationResult(ValidationStatus.UNAUTHORIZED, "Invalid/Forbidden", http_status=resp.status_code, raw_response=resp.text)
            if self._is_success(resp.status_code):
                models = [m['name'] for m in resp.json().get('models', [])]
                if models:
                    model = next((m for m in models if "gemini" in m.lower()), models[0])
                    gen_url = f"https://generativelanguage.googleapis.com/v1/{model}:generateContent?key={api_key}"
                    gen_resp = requests.post(gen_url, json={"contents": [{"parts": [{"text": "hi"}]}]}, timeout=self.timeout)
                    if self._is_success(gen_resp.status_code):
                        return ValidationResult(ValidationStatus.VALID, "Active key", models=models, http_status=gen_resp.status_code, raw_response=gen_resp.text)
                    if gen_resp.status_code == 429:
                        return ValidationResult(ValidationStatus.QUOTA_EXHAUSTED, "Quota exceeded", models=models, http_status=gen_resp.status_code, raw_response=gen_resp.text)
                return ValidationResult(ValidationStatus.VALID, "Valid key, model check skipped", models=models, http_status=resp.status_code, raw_response=resp.text)
            return ValidationResult(ValidationStatus.ERROR, f"Status {resp.status_code}", http_status=resp.status_code, raw_response=resp.text)
        except Exception as e:
            return ValidationResult(ValidationStatus.ERROR, str(e))

class ElevenLabsProvider(BaseProvider):
    provider_name = "ElevenLabs"
    regex_patterns = [r"[A-Za-z0-9]{32}"]

    def validate(self, api_key):
        api_key = self.clean_key(api_key)
        headers = {"xi-api-key": api_key}
        try:
            resp = requests.get("https://api.elevenlabs.io/v1/user/subscription", headers=headers, timeout=self.timeout)
            if resp.status_code == 401:
                return ValidationResult(ValidationStatus.UNAUTHORIZED, "Invalid Key", http_status=resp.status_code)
            if self._is_success(resp.status_code):
                data = resp.json()
                tier = data.get('tier', 'Free')
                used = data.get('character_count', 0)
                limit = data.get('character_limit', 0)
                rem = limit - used
                status = ValidationStatus.VALID if rem > 0 else ValidationStatus.QUOTA_EXHAUSTED
                return ValidationResult(status, balance=f"{rem} chars remaining", tier=tier, http_status=resp.status_code, raw_response=resp.text)
            return ValidationResult(ValidationStatus.ERROR, f"Status {resp.status_code}", http_status=resp.status_code, raw_response=resp.text)
        except Exception as e:
            return ValidationResult(ValidationStatus.ERROR, str(e))

class PiAPIProvider(BaseProvider):
    provider_name = "PiAPI"
    regex_patterns = [r"(?i)PIAPI[_-]?KEY.*?['\"]([a-zA-Z0-9]{20,})['\"]", r"\b[a-zA-Z0-9]{32,}\b"]

    def validate(self, api_key):
        api_key = self.clean_key(api_key)
        headers = {"x-api-key": api_key}
        try:
            resp = requests.get("https://api.piapi.ai/account/info", headers=headers, timeout=self.timeout)
            if resp.status_code == 401 or resp.status_code == 403:
                return ValidationResult(ValidationStatus.UNAUTHORIZED, "Invalid Key", http_status=resp.status_code)
            if self._is_success(resp.status_code):
                data = resp.json().get('data', resp.json())
                tier = data.get('plan', 'Free')
                balance = data.get('equivalent_in_usd', f"{data.get('remaining_credits', '0')} credits")
                if isinstance(balance, (int, float)): balance = f"${balance} USD"
                return ValidationResult(ValidationStatus.VALID, balance=balance, tier=tier, http_status=resp.status_code, raw_response=resp.text)
            return ValidationResult(ValidationStatus.ERROR, f"Status {resp.status_code}", http_status=resp.status_code, raw_response=resp.text)
        except Exception as e:
            return ValidationResult(ValidationStatus.ERROR, str(e))

class GroqProvider(BaseProvider):
    provider_name = "Groq"
    regex_patterns = [r"gsk_[A-Za-z0-9]{40,60}"]

    def validate(self, api_key):
        api_key = self.clean_key(api_key)
        headers = {"Authorization": f"Bearer {api_key}"}
        try:
            resp = requests.get("https://api.groq.com/openai/v1/models", headers=headers, timeout=self.timeout)
            if resp.status_code == 401:
                return ValidationResult(ValidationStatus.UNAUTHORIZED, "Invalid Key", http_status=resp.status_code)
            if self._is_success(resp.status_code):
                models = [m['id'] for m in resp.json().get('data', [])]
                model = next((m for m in models if "llama" in m), models[0] if models else "llama-3.1-8b-instant")
                gen_resp = requests.post("https://api.groq.com/openai/v1/chat/completions", headers=headers, json={"model": model, "messages": [{"role": "user", "content": "hi"}], "max_tokens": 1}, timeout=self.timeout)
                if self._is_success(gen_resp.status_code):
                    return ValidationResult(ValidationStatus.VALID, "Active key", models=models, http_status=gen_resp.status_code, raw_response=gen_resp.text)
                if gen_resp.status_code == 429:
                    return ValidationResult(ValidationStatus.QUOTA_EXHAUSTED, "Quota exhausted", models=models, http_status=gen_resp.status_code, raw_response=gen_resp.text)
                return ValidationResult(ValidationStatus.VALID, "Valid but completion failed", models=models, http_status=gen_resp.status_code, raw_response=gen_resp.text)
            return ValidationResult(ValidationStatus.ERROR, f"Status {resp.status_code}", http_status=resp.status_code, raw_response=resp.text)
        except Exception as e:
            return ValidationResult(ValidationStatus.ERROR, str(e))

class MistralProvider(BaseProvider):
    provider_name = "Mistral AI"
    regex_patterns = [r"(?i)MISTRAL[_-]?API[_-]?KEY.*?[:=].*?([A-Za-z0-9]{32})"]

    def validate(self, api_key):
        api_key = self.clean_key(api_key)
        headers = {"Authorization": f"Bearer {api_key}"}
        try:
            resp = requests.get("https://api.mistral.ai/v1/models", headers=headers, timeout=self.timeout)
            if resp.status_code == 401:
                return ValidationResult(ValidationStatus.UNAUTHORIZED, "Invalid Key", http_status=resp.status_code)
            if self._is_success(resp.status_code):
                models = [m['id'] for m in resp.json().get('data', [])]
                model = next((m for m in models if "mistral-small" in m), models[0] if models else "mistral-small-latest")
                gen_resp = requests.post("https://api.mistral.ai/v1/chat/completions", headers=headers, json={"model": model, "messages": [{"role": "user", "content": "hi"}], "max_tokens": 1}, timeout=self.timeout)
                if self._is_success(gen_resp.status_code):
                    return ValidationResult(ValidationStatus.VALID, "Active key", models=models, http_status=gen_resp.status_code, raw_response=gen_resp.text)
                if gen_resp.status_code == 429:
                    return ValidationResult(ValidationStatus.QUOTA_EXHAUSTED, "Quota exhausted", models=models, http_status=gen_resp.status_code, raw_response=gen_resp.text)
                return ValidationResult(ValidationStatus.VALID, "Valid but completion failed", models=models, http_status=gen_resp.status_code, raw_response=gen_resp.text)
            return ValidationResult(ValidationStatus.ERROR, f"Status {resp.status_code}", http_status=resp.status_code, raw_response=resp.text)
        except Exception as e:
            return ValidationResult(ValidationStatus.ERROR, str(e))

class PerplexityProvider(BaseProvider):
    provider_name = "Perplexity"
    regex_patterns = [r"pplx-[A-Za-z0-9]{48}"]

    def validate(self, api_key):
        api_key = self.clean_key(api_key)
        headers = {"Authorization": f"Bearer {api_key}"}
        try:
            resp = requests.get("https://api.perplexity.ai/models", headers=headers, timeout=self.timeout)
            if resp.status_code == 401:
                return ValidationResult(ValidationStatus.UNAUTHORIZED, "Invalid Key", http_status=resp.status_code)
            if self._is_success(resp.status_code):
                models = [m['id'] for m in resp.json().get('data', [])]
                model = models[0] if models else "llama-3.1-8b-instruct"
                gen_resp = requests.post("https://api.perplexity.ai/chat/completions", headers=headers, json={"model": model, "messages": [{"role": "user", "content": "hi"}], "max_tokens": 1}, timeout=self.timeout)
                if self._is_success(gen_resp.status_code):
                    return ValidationResult(ValidationStatus.VALID, "Active key", models=models, http_status=gen_resp.status_code, raw_response=gen_resp.text)
                if gen_resp.status_code == 429:
                    return ValidationResult(ValidationStatus.QUOTA_EXHAUSTED, "Quota exhausted", models=models, http_status=gen_resp.status_code, raw_response=gen_resp.text)
                return ValidationResult(ValidationStatus.VALID, "Valid but completion failed", models=models, http_status=gen_resp.status_code, raw_response=gen_resp.text)
            return ValidationResult(ValidationStatus.ERROR, f"Status {resp.status_code}", http_status=resp.status_code, raw_response=resp.text)
        except Exception as e:
            return ValidationResult(ValidationStatus.ERROR, str(e))

class RunwayProvider(BaseProvider):
    provider_name = "RunwayML"
    regex_patterns = [r"\bkey_[a-zA-Z0-9]{32,}\b"]

    def validate(self, api_key):
        api_key = self.clean_key(api_key)
        headers = {"Authorization": f"Bearer {api_key}", "X-Runway-Version": "2024-11-06"}
        try:
            resp = requests.get("https://api.runwayml.com/v1/organization", headers=headers, timeout=self.timeout)
            if resp.status_code == 401 or resp.status_code == 403:
                return ValidationResult(ValidationStatus.UNAUTHORIZED, "Invalid Key", http_status=resp.status_code)
            if self._is_success(resp.status_code):
                data = resp.json()
                return ValidationResult(ValidationStatus.VALID, balance=f"{data.get('creditBalance', '0')} Credits", tier=data.get('usage_tier', 'Unknown'), http_status=resp.status_code, raw_response=resp.text)
            return ValidationResult(ValidationStatus.ERROR, f"Status {resp.status_code}", http_status=resp.status_code, raw_response=resp.text)
        except Exception as e:
            return ValidationResult(ValidationStatus.ERROR, str(e))

class A2EProvider(BaseProvider):
    provider_name = "A2E AI"
    regex_patterns = [r"sk_[A-Za-z0-9]{32,}"]

    def validate(self, api_key):
        api_key = self.clean_key(api_key)
        headers = {"Authorization": f"Bearer {api_key}"}
        try:
            resp = requests.get("https://video.a2e.ai/api/v1/user/remainingCoins", headers=headers, timeout=15)
            if resp.status_code == 401 or resp.status_code == 403:
                return ValidationResult(ValidationStatus.UNAUTHORIZED, "Invalid Key", http_status=resp.status_code)
            if self._is_success(resp.status_code):
                data = resp.json()
                if data.get('code') != 200:
                    return ValidationResult(ValidationStatus.UNAUTHORIZED, data.get('msg', 'Invalid token'), http_status=resp.status_code)
                coins = data.get('data', {}).get('coins', 0)
                status = ValidationStatus.VALID if coins > 0 else ValidationStatus.QUOTA_EXHAUSTED
                return ValidationResult(status, balance=f"{coins} Coins", http_status=resp.status_code, raw_response=resp.text)
            return ValidationResult(ValidationStatus.ERROR, f"Status {resp.status_code}", http_status=resp.status_code, raw_response=resp.text)
        except Exception as e:
            return ValidationResult(ValidationStatus.ERROR, str(e))

class OpenRouterProvider(BaseProvider):
    provider_name = "OpenRouter"
    regex_patterns = [r"sk-or-v1-[A-Za-z0-9]{40,80}"]

    def validate(self, api_key):
        api_key = self.clean_key(api_key)
        headers = {"Authorization": f"Bearer {api_key}"}
        try:
            resp = requests.get("https://openrouter.ai/api/v1/auth/key", headers=headers, timeout=self.timeout)
            if resp.status_code == 401 or resp.status_code == 403:
                return ValidationResult(ValidationStatus.UNAUTHORIZED, "Invalid Key", http_status=resp.status_code)
            if self._is_success(resp.status_code):
                data = resp.json().get('data', {})
                rem = data.get('limit_remaining')
                balance = f"${rem:.4f} credits" if rem is not None else "Unlimited"
                status = ValidationStatus.VALID if rem is None or rem > 0 else ValidationStatus.QUOTA_EXHAUSTED
                return ValidationResult(status, balance=balance, tier=data.get('label'), http_status=resp.status_code, raw_response=resp.text)
            return ValidationResult(ValidationStatus.ERROR, f"Status {resp.status_code}", http_status=resp.status_code, raw_response=resp.text)
        except Exception as e:
            return ValidationResult(ValidationStatus.ERROR, str(e))

class TogetherAIProvider(BaseProvider):
    provider_name = "Together AI"
    regex_patterns = [r"\b[0-9a-f]{64}\b", r"together[_-]?ai[_-]?[A-Za-z0-9]{32,}"]

    def validate(self, api_key):
        api_key = self.clean_key(api_key)
        headers = {"Authorization": f"Bearer {api_key}"}
        try:
            resp = requests.get("https://api.together.xyz/v1/credits", headers=headers, timeout=self.timeout)
            if resp.status_code == 401 or resp.status_code == 403:
                return ValidationResult(ValidationStatus.UNAUTHORIZED, "Invalid Key", http_status=resp.status_code)
            if self._is_success(resp.status_code):
                credits = resp.json().get('credits', 0)
                status = ValidationStatus.VALID if credits > 0 else ValidationStatus.QUOTA_EXHAUSTED
                return ValidationResult(status, balance=f"{credits} Credits", http_status=resp.status_code, raw_response=resp.text)
            return ValidationResult(ValidationStatus.ERROR, f"Status {resp.status_code}", http_status=resp.status_code, raw_response=resp.text)
        except Exception as e:
            return ValidationResult(ValidationStatus.ERROR, str(e))

class CohereProvider(BaseProvider):
    provider_name = "Cohere"
    regex_patterns = [r"\b[A-Za-z0-9]{40}\b", r"cohere[_-]?[A-Za-z0-9]{32,}", r"COHERE_API_KEY"]

    def validate(self, api_key):
        api_key = self.clean_key(api_key)
        headers = {"Authorization": f"Bearer {api_key}"}
        try:
            # 1. Get account IDs
            check_resp = requests.post("https://api.cohere.com/v1/check-api-key", headers=headers, timeout=self.timeout)
            org_id, owner_id = None, None
            if self._is_success(check_resp.status_code):
                data = check_resp.json()
                org_id = data.get('organization_id')
                owner_id = data.get('owner_id')
            elif check_resp.status_code in [401, 403]:
                return ValidationResult(ValidationStatus.UNAUTHORIZED, "Invalid Key", http_status=check_resp.status_code)

            # 2. Check chat capability and extract limits from headers
            payload = {"model": "command-r-08-2024", "messages": [{"role": "user", "content": "hi"}], "max_tokens": 1}
            resp = requests.post("https://api.cohere.com/v2/chat", headers=headers, json=payload, timeout=self.timeout)
            
            # Extract balance/tier from headers
            h = resp.headers
            rem = h.get('x-trial-endpoint-call-remaining') or h.get('x-ratelimit-remaining')
            limit = h.get('x-endpoint-monthly-call-limit')
            is_trial = 'x-trial-endpoint-call-limit' in h or "Trial key" in resp.text
            
            tier = f"Trial (Limit: {limit})" if is_trial else f"Paid (Limit: {limit})"
            balance = f"{rem} calls remaining" if rem else None
            detail = f"Org: {org_id}, Owner: {owner_id}" if org_id else ""
            
            if self._is_success(resp.status_code):
                return ValidationResult(ValidationStatus.VALID, tier=tier, balance=balance, detail=detail, http_status=resp.status_code, raw_response=resp.text)
            
            if resp.status_code == 429 or any(x in resp.text.lower() for x in ["quota", "billing", "limit", "insufficient"]):
                 return ValidationResult(ValidationStatus.QUOTA_EXHAUSTED, f"Quota/Trial reached. {detail}", tier=tier, balance="0 calls remaining", http_status=resp.status_code, raw_response=resp.text)
            
            return ValidationResult(ValidationStatus.ERROR, f"Status {resp.status_code}. {detail}", tier=tier, http_status=resp.status_code, raw_response=resp.text)
        except Exception as e:
            return ValidationResult(ValidationStatus.ERROR, str(e))

class VoyageAIProvider(BaseProvider):
    provider_name = "Voyage AI"
    regex_patterns = [r"pa-[A-Za-z0-9]{40,60}"]

    def validate(self, api_key):
        api_key = self.clean_key(api_key)
        headers = {"Authorization": f"Bearer {api_key}"}
        try:
            payload = {"input": ["test"], "model": "voyage-3-lite"}
            resp = requests.post("https://api.voyageai.com/v1/embeddings", headers=headers, json=payload, timeout=self.timeout)
            if resp.status_code == 401 or resp.status_code == 403:
                return ValidationResult(ValidationStatus.UNAUTHORIZED, "Invalid Key", http_status=resp.status_code)
            if self._is_success(resp.status_code):
                return ValidationResult(ValidationStatus.VALID, detail="Embeddings working", http_status=resp.status_code, raw_response=resp.text)
            if resp.status_code == 429 or any(x in resp.text.lower() for x in self.QUOTA_INDICATORS):
                return ValidationResult(ValidationStatus.QUOTA_EXHAUSTED, "Quota exhausted", http_status=resp.status_code, raw_response=resp.text)
            return ValidationResult(ValidationStatus.ERROR, f"Status {resp.status_code}", http_status=resp.status_code, raw_response=resp.text)
        except Exception as e:
            return ValidationResult(ValidationStatus.ERROR, str(e))

class XAIProvider(BaseProvider):
    provider_name = "X.AI"
    regex_patterns = [r"xai-[A-Za-z0-9]{32,}", r"grok[_-]?[A-Za-z0-9]{32,}"]

    def validate(self, api_key):
        api_key = self.clean_key(api_key)
        headers = {"Authorization": f"Bearer {api_key}"}
        try:
            resp = requests.get("https://api.x.ai/v1/teams", headers=headers, timeout=self.timeout)
            if resp.status_code == 401 or resp.status_code == 403:
                return ValidationResult(ValidationStatus.UNAUTHORIZED, "Invalid Key", http_status=resp.status_code)
            if self._is_success(resp.status_code):
                teams = resp.json().get('teams', [])
                if teams:
                    tid = teams[0].get('id')
                    b_resp = requests.get(f"https://api.x.ai/v1/billing/teams/{tid}/prepaid/balance", headers=headers, timeout=self.timeout)
                    if self._is_success(b_resp.status_code):
                        bal = b_resp.json().get('balance', 0)
                        return ValidationResult(ValidationStatus.VALID, balance=f"{bal} Credits", tier=teams[0].get('name'), http_status=b_resp.status_code, raw_response=b_resp.text)
                return ValidationResult(ValidationStatus.VALID, "Active account", http_status=resp.status_code, raw_response=resp.text)
            return ValidationResult(ValidationStatus.ERROR, f"Status {resp.status_code}", http_status=resp.status_code, raw_response=resp.text)
        except Exception as e:
            return ValidationResult(ValidationStatus.ERROR, str(e))

class HuggingFaceProvider(BaseProvider):
    provider_name = "HuggingFace"
    regex_patterns = [r"hf_[A-Za-z0-9]{32,}"]

    def validate(self, api_key):
        api_key = self.clean_key(api_key)
        headers = {"Authorization": f"Bearer {api_key}"}
        try:
            resp = requests.get("https://huggingface.co/api/whoami-v2", headers=headers, timeout=self.timeout)
            if resp.status_code == 401:
                return ValidationResult(ValidationStatus.UNAUTHORIZED, "Invalid Token", http_status=resp.status_code)
            if self._is_success(resp.status_code):
                data = resp.json()
                bal = data.get('creditBalance')
                return ValidationResult(ValidationStatus.VALID, balance=f"{bal} Credits" if bal else None, tier=data.get('name'), http_status=resp.status_code, raw_response=resp.text)
            return ValidationResult(ValidationStatus.ERROR, f"Status {resp.status_code}", http_status=resp.status_code, raw_response=resp.text)
        except Exception as e:
            return ValidationResult(ValidationStatus.ERROR, str(e))

class ReplicateProvider(BaseProvider):
    provider_name = "Replicate"
    regex_patterns = [r"r8_[A-Za-z0-9]{32,}"]

    def validate(self, api_key):
        api_key = self.clean_key(api_key)
        headers = {"Authorization": f"Bearer {api_key}", "User-Agent": "UnsecuredAPIKeys-Lite/1.0"}
        try:
            resp = requests.get("https://api.replicate.com/v1/account", headers=headers, timeout=self.timeout)
            if resp.status_code == 401:
                return ValidationResult(ValidationStatus.UNAUTHORIZED, "Invalid Token", http_status=resp.status_code)
            if resp.status_code == 402:
                return ValidationResult(ValidationStatus.QUOTA_EXHAUSTED, "Payment Required", http_status=resp.status_code, raw_response=resp.text)
            if self._is_success(resp.status_code):
                data = resp.json()
                return ValidationResult(ValidationStatus.VALID, tier=data.get('username'), detail=f"Valid {data.get('type')} account", http_status=resp.status_code, raw_response=resp.text)
            return ValidationResult(ValidationStatus.ERROR, f"Status {resp.status_code}", http_status=resp.status_code, raw_response=resp.text)
        except Exception as e:
            return ValidationResult(ValidationStatus.ERROR, str(e))

class StabilityAIProvider(BaseProvider):
    provider_name = "Stability AI"
    regex_patterns = [r"sk-[A-Za-z0-9]{32,}", r"stability[_-]?ai[_-]?[A-Za-z0-9]{32,}"]

    def validate(self, api_key):
        api_key = self.clean_key(api_key)
        headers = {"Authorization": f"Bearer {api_key}"}
        try:
            resp = requests.get("https://api.stability.ai/v1/user/balance", headers=headers, timeout=15)
            if resp.status_code == 401 or resp.status_code == 403:
                return ValidationResult(ValidationStatus.UNAUTHORIZED, "Invalid Key", http_status=resp.status_code)
            if self._is_success(resp.status_code):
                credits = resp.json().get('credits', 0)
                status = ValidationStatus.VALID if credits > 0 else ValidationStatus.QUOTA_EXHAUSTED
                return ValidationResult(status, balance=f"{credits} Credits", http_status=resp.status_code, raw_response=resp.text)
            if any(x in resp.text.lower() for x in ["quota", "billing", "credits", "insufficient"]):
                return ValidationResult(ValidationStatus.QUOTA_EXHAUSTED, "Quota issue", http_status=resp.status_code, raw_response=resp.text)
            return ValidationResult(ValidationStatus.ERROR, f"Status {resp.status_code}", http_status=resp.status_code, raw_response=resp.text)
        except Exception as e:
            return ValidationResult(ValidationStatus.ERROR, str(e))

class PolloAIProvider(BaseProvider):
    provider_name = "PolloAI"
    regex_patterns = [r"pollo_[a-zA-Z0-9]{24,}"]

    def validate(self, api_key):
        api_key = self.clean_key(api_key)
        headers = {"x-api-key": api_key}
        try:
            resp = requests.get("https://pollo.ai/api/platform/credit/balance", headers=headers, timeout=self.timeout)
            if resp.status_code == 401 or resp.status_code == 403:
                return ValidationResult(ValidationStatus.UNAUTHORIZED, "Invalid Key", http_status=resp.status_code)
            
            if self._is_success(resp.status_code):
                data = resp.json().get('data', {})
                avail = data.get('availableCredits')
                total = data.get('totalCredits')
                balance = f"{avail} / {total} Credits" if avail is not None and total is not None else f"{data.get('balance', '0')} Credits"
                
                status = ValidationStatus.VALID if (avail and float(avail) > 0) or (data.get('balance') and float(data.get('balance')) > 0) else ValidationStatus.QUOTA_EXHAUSTED
                return ValidationResult(status, balance=balance, http_status=resp.status_code, raw_response=resp.text)
            
            return ValidationResult(ValidationStatus.ERROR, f"Status {resp.status_code}", http_status=resp.status_code, raw_response=resp.text)
        except Exception as e:
            return ValidationResult(ValidationStatus.ERROR, str(e))

class SendGridProvider(BaseProvider):
    provider_name = "SendGrid"
    regex_patterns = [r"SG\.[A-Za-z0-9_-]{22}\.[A-Za-z0-9_-]{43}"]

    def validate(self, api_key):
        api_key = self.clean_key(api_key)
        headers = {"Authorization": f"Bearer {api_key}"}
        try:
            # 1. Try credits endpoint
            resp = requests.get("https://api.sendgrid.com/v3/user/credits", headers=headers, timeout=self.timeout)
            if self._is_success(resp.status_code):
                data = resp.json()
                rem = data.get('remain', 0)
                total = data.get('total', 0)
                status = ValidationStatus.VALID if rem > 0 else ValidationStatus.QUOTA_EXHAUSTED
                return ValidationResult(status, balance=f"{rem} / {total} Credits", http_status=resp.status_code, raw_response=resp.text)
            
            # 2. Fallback to scopes check
            s_resp = requests.get("https://api.sendgrid.com/v3/scopes", headers=headers, timeout=self.timeout)
            if self._is_success(s_resp.status_code):
                tier = "Can Send Mail" if "mail.send" in s_resp.text else "Restricted"
                return ValidationResult(ValidationStatus.VALID, tier=tier, detail="Scopes check passed", http_status=s_resp.status_code, raw_response=s_resp.text)
            
            if s_resp.status_code in [401, 403]:
                return ValidationResult(ValidationStatus.UNAUTHORIZED, "Invalid Token", http_status=s_resp.status_code)
                
            return ValidationResult(ValidationStatus.ERROR, f"Status {resp.status_code}", http_status=resp.status_code, raw_response=resp.text)
        except Exception as e:
            return ValidationResult(ValidationStatus.ERROR, str(e))

class SlackProvider(BaseProvider):
    provider_name = "Slack"
    regex_patterns = [r"xox[baprs]-[A-Za-z0-9-]{10,}"]

    def validate(self, api_key):
        api_key = self.clean_key(api_key)
        headers = {"Authorization": f"Bearer {api_key}"}
        try:
            # 1. Auth Test
            resp = requests.post("https://slack.com/api/auth.test", headers=headers, timeout=self.timeout)
            if not self._is_success(resp.status_code):
                 if resp.status_code in [401, 403]:
                     return ValidationResult(ValidationStatus.UNAUTHORIZED, "Invalid Token", http_status=resp.status_code)
                 return ValidationResult(ValidationStatus.ERROR, f"Status {resp.status_code}", http_status=resp.status_code, raw_response=resp.text)
            
            data = resp.json()
            if not data.get('ok'):
                error = data.get('error', 'unknown_error')
                return ValidationResult(ValidationStatus.UNAUTHORIZED, f"Slack error: {error}", http_status=resp.status_code, raw_response=resp.text)
            
            team = data.get('team', 'Unknown Team')
            user = data.get('user', 'Unknown User')
            
            # 2. Try billing info
            plan = "Unknown"
            b_resp = requests.get("https://slack.com/api/team.billing.info", headers=headers, timeout=self.timeout)
            if self._is_success(b_resp.status_code):
                plan = b_resp.json().get('plan', 'Unknown')

            return ValidationResult(ValidationStatus.VALID, tier=team, balance=f"Plan: {plan}", detail=f"User: {user}", http_status=resp.status_code, raw_response=resp.text)
        except Exception as e:
            return ValidationResult(ValidationStatus.ERROR, str(e))

# --- Engine ---

class VerifierEngine:
    def __init__(self):
        self.providers = [
            OpenAIProvider(), DeepSeekProvider(), AnthropicProvider(), GoogleProvider(),
            ElevenLabsProvider(), PiAPIProvider(), GroqProvider(), MistralProvider(),
            PerplexityProvider(), RunwayProvider(), A2EProvider(), OpenRouterProvider(),
            TogetherAIProvider(), CohereProvider(), VoyageAIProvider(), XAIProvider(),
            HuggingFaceProvider(), ReplicateProvider(), StabilityAIProvider(), PolloAIProvider(),
            SendGridProvider(), SlackProvider()
        ]

    def identify_provider(self, key_text):
        for p in self.providers:
            if p.matches(key_text): return p
        return None

    def _get_case_insensitive(self, item, keys):
        for k in keys:
            if k in item: return item[k]
            for rk in item.keys():
                if rk.lower() == k.lower(): return item[rk]
        return None

    def verify_item(self, item):
        api_key = self._get_case_insensitive(item, ['apiKey', 'key', 'ApiKey'])
        assigned_type = str(self._get_case_insensitive(item, ['apiTypeName', 'type', 'ApiTypeName', 'Provider']) or "").lower()
        if not api_key: return {**item, "verification": ValidationResult(ValidationStatus.ERROR, "No key found").to_dict()}

        # Handle aliases
        aliases = {
            "anthropicclaude": "anthropic",
            "stabilityai": "stability ai",
            "googleai": "google",
            "mistralai": "mistral ai",
            "voyageai": "voyage ai",
            "togetherai": "together ai",
            "runway": "runwayml",
            "xai": "x.ai",
            "a2e": "a2e ai"
        }
        search_type = aliases.get(assigned_type, assigned_type)

        provider = next((p for p in self.providers if p.provider_name.lower() == search_type), None)
        if not provider: provider = self.identify_provider(api_key)
        if not provider: return {**item, "verification": ValidationResult(ValidationStatus.ERROR, "Unknown provider").to_dict()}

        locked_print(f"[*] Verifying {provider.provider_name} key: {api_key[:15]}...")
        result = provider.validate(api_key)
        
        color = "\033[92m" if result.status == ValidationStatus.VALID else "\033[91m" if result.status in [ValidationStatus.UNAUTHORIZED] else "\033[93m"
        reset = "\033[0m"
        
        lines = [f"    {color}[+] Result: {result.status}{reset}"]
        if result.balance: lines.append(f"    [>] Balance: {result.balance}")
        if result.tier: lines.append(f"    [>] Tier: {result.tier}")
        if result.models: lines.append(f"    [>] Models: {len(result.models)} found")
        if result.detail: lines.append(f"    [>] Detail: {result.detail}")
        
        locked_print("\n".join(lines))
        
        return {**item, "verification": result.to_dict()}

    def run(self, input_file, output_file, threads=5, valid_only=False):
        try:
            with open(input_file, 'r', encoding='utf-8') as f: data = json.load(f)
        except Exception as e:
            print(f"[!] Error: {e}"); return

        print(f"[*] Loaded {len(data)} keys. Using {threads} threads...")
        results = []
        with ThreadPoolExecutor(max_workers=threads) as executor:
            futures = [executor.submit(self.verify_item, item) for item in data]
            for future in as_completed(futures):
                res = future.result()
                if valid_only:
                    if res.get('verification', {}).get('status') == ValidationStatus.VALID:
                        results.append(res)
                else:
                    results.append(res)

        try:
            with open(output_file, 'w', encoding='utf-8') as f: json.dump(results, f, indent=2)
            print(f"[!] Complete. Saved: {len(results)} items to {output_file}")
        except Exception as e:
            print(f"[!] Save error: {e}")

if __name__ == "__main__":
    parser = argparse.ArgumentParser(description="External API Key Verifier")
    parser.add_argument("input", help="Path to input JSON file")
    parser.add_argument("-o", "--output", default="verified_results.json", help="Path to output JSON file")
    parser.add_argument("-t", "--threads", type=int, default=5, help="Number of concurrent threads")
    parser.add_argument("--valid-only", action="store_true", help="Only save valid keys")
    args = parser.parse_args()
    VerifierEngine().run(args.input, args.output, args.threads, args.valid_only)
