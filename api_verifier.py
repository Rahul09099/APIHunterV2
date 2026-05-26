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
import base64

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
                root = bal_resp.json()
                data = root.get('data', root)
                
                balance_infos = data.get('balance_infos', [])
                if balance_infos:
                    info = balance_infos[0]
                    total = info.get('total_balance', '0')
                    currency = info.get('currency', 'USD')
                    granted = info.get('granted_balance', '0')
                    topped_up = info.get('topped_up_balance', '0')
                    
                    balance = f"{total} {currency} (Grant: {granted}, Paid: {topped_up})"
                    tier = "Paid Account" if float(topped_up) > 0 else "Free/Grant Account"
                    
                    is_available = data.get('is_available', True)
                    status = ValidationStatus.VALID if is_available and float(total) > 0 else ValidationStatus.QUOTA_EXHAUSTED
                    
                    metadata = {
                        "total_balance": total,
                        "granted_balance": granted,
                        "topped_up_balance": topped_up,
                        "currency": currency,
                        "is_available": is_available
                    }
                    
                    return ValidationResult(status, balance=balance, tier=tier, models=models, metadata=metadata, http_status=bal_resp.status_code, raw_response=bal_resp.text)
                
                return ValidationResult(ValidationStatus.VALID, detail="Key valid but no balance info", models=models, http_status=bal_resp.status_code, raw_response=bal_resp.text)
            
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
    regex_patterns = [r"\b(?<!sk_)[a-fA-F0-9]{32}\b"]

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
                root = resp.json()
                data = root.get('data', root)
                
                tier = data.get('plan', 'Free')
                name = data.get('name')
                detail = f"Account: {name}" if name else ""
                
                # Balance extraction
                usd = data.get('equivalent_in_usd')
                credits = data.get('remaining_credits')
                
                balance = None
                if usd is not None:
                    balance = f"${usd} USD"
                elif credits is not None:
                    balance = f"{credits} Credits"
                
                # Wallet details
                wallet = data.get('wallet', {})
                wallet_details = []
                metadata = {"wallet": {}}
                
                fields = {
                    "mj_remain": "MJ",
                    "llm_remain": "LLM",
                    "suno_remain": "Suno",
                    "luma_remain": "Luma",
                    "gpts_remain": "GPTs",
                    "point_remain": "Points"
                }
                
                for key, label in fields.items():
                    if key in wallet:
                        val = wallet[key]
                        wallet_details.append(f"{label}: {val}")
                        metadata["wallet"][key] = val
                
                if wallet_details:
                    wallet_str = ", ".join(wallet_details)
                    if balance:
                        balance = f"{balance} ({wallet_str})"
                    else:
                        balance = wallet_str
                
                metadata.update({
                    "plan": tier,
                    "name": name,
                    "equivalent_in_usd": usd,
                    "remaining_credits": credits
                })
                
                return ValidationResult(
                    ValidationStatus.VALID, 
                    balance=balance, 
                    tier=tier, 
                    detail=detail,
                    metadata=metadata,
                    http_status=resp.status_code, 
                    raw_response=resp.text
                )
            
            # Check for common quota/limit messages in error body
            if any(ind in resp.text.lower() for ind in self.QUOTA_INDICATORS):
                return ValidationResult(ValidationStatus.QUOTA_EXHAUSTED, f"Valid key but access issue: {self._truncate(resp.text)}", http_status=resp.status_code, raw_response=resp.text)
                
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

class CerebrasProvider(BaseProvider):
    provider_name = "Cerebras"
    regex_patterns = [r"\bcsk-[A-Za-z0-9]{40,80}\b"]

    def validate(self, api_key):
        api_key = self.clean_key(api_key)
        headers = {"Authorization": f"Bearer {api_key}"}
        try:
            # 1. Get models
            resp = requests.get("https://api.cerebras.ai/v1/models", headers=headers, timeout=self.timeout)
            if resp.status_code in [401, 403]:
                return ValidationResult(ValidationStatus.UNAUTHORIZED, "Invalid Key", http_status=resp.status_code)
            if not self._is_success(resp.status_code):
                return ValidationResult(ValidationStatus.ERROR, f"Models fail: {resp.status_code}", http_status=resp.status_code)

            models = [m['id'] for m in resp.json().get('data', [])]
            
            # 2. Test completion
            payload = {
                "model": "llama3.1-8b",
                "messages": [{"role": "user", "content": "hi"}],
                "max_tokens": 1
            }
            chat_resp = requests.post("https://api.cerebras.ai/v1/chat/completions", headers=headers, json=payload, timeout=self.timeout)
            
            if self._is_success(chat_resp.status_code):
                return ValidationResult(ValidationStatus.VALID, "Active key", models=models, http_status=chat_resp.status_code)
            
            if chat_resp.status_code == 429 or "quota" in chat_resp.text.lower():
                return ValidationResult(ValidationStatus.QUOTA_EXHAUSTED, "Valid but no quota", models=models, http_status=chat_resp.status_code)
                
            return ValidationResult(ValidationStatus.VALID, "Valid but completion failed", models=models, http_status=chat_resp.status_code)
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
    regex_patterns = [r"pplx-[A-Za-z0-9]{40,64}"]

    def validate(self, api_key):
        api_key = self.clean_key(api_key)
        headers = {"Authorization": f"Bearer {api_key}"}
        try:
            # 1. Get models (using /v1/models)
            models = []
            m_resp = requests.get("https://api.perplexity.ai/v1/models", headers=headers, timeout=self.timeout)
            if self._is_success(m_resp.status_code):
                models = [m['id'] for m in m_resp.json().get('data', [])]
            elif m_resp.status_code in [401, 403]:
                return ValidationResult(ValidationStatus.UNAUTHORIZED, "Invalid Key", http_status=m_resp.status_code)

            # 2. Test completion
            model = "sonar" # Default known good
            if models:
                # Prefer models that don't have a prefix like "openai/" or "anthropic/" if we are calling Perplexity directly
                preferred = ["sonar", "sonar-pro", "llama-3.1-8b-instruct"]
                for p in preferred:
                    if any(p == m or f"perplexity/{p}" == m for m in models):
                        model = p
                        break
                else:
                    model = models[0]
            
            payload = {"model": model, "messages": [{"role": "user", "content": "hi"}], "max_tokens": 1}
            gen_resp = requests.post("https://api.perplexity.ai/chat/completions", headers=headers, json=payload, timeout=self.timeout)
            
            # Rate limit extraction (even if not always present in success)
            h = gen_resp.headers
            limit = h.get('x-ratelimit-limit') or h.get('x-ratelimit-limit-requests')
            rem = h.get('x-ratelimit-remaining') or h.get('x-ratelimit-remaining-requests')
            
            metadata = {}
            if limit: metadata["limit"] = limit
            if rem: metadata["remaining"] = rem
            
            # Tier inference from limit if possible
            tier = None
            if limit:
                try:
                    l_val = int(limit)
                    if l_val <= 50: tier = "Tier 0"
                    elif l_val <= 150: tier = "Tier 1"
                    elif l_val <= 500: tier = "Tier 2"
                    elif l_val <= 1000: tier = "Tier 3"
                    else: tier = "Tier 4+"
                except: pass

            if self._is_success(gen_resp.status_code):
                data = gen_resp.json()
                usage = data.get('usage', {})
                cost = usage.get('cost', {})
                if cost:
                    metadata["last_request_cost"] = cost.get('total_cost')
                
                return ValidationResult(
                    ValidationStatus.VALID, 
                    "Active key", 
                    models=models, 
                    tier=tier,
                    metadata=metadata,
                    http_status=gen_resp.status_code, 
                    raw_response=gen_resp.text
                )
            
            if gen_resp.status_code == 429:
                return ValidationResult(ValidationStatus.QUOTA_EXHAUSTED, "Quota exhausted", models=models, tier=tier, metadata=metadata, http_status=gen_resp.status_code, raw_response=gen_resp.text)
            
            # If models worked but chat failed for other reasons
            if models:
                return ValidationResult(ValidationStatus.VALID, f"Valid key but chat failed ({gen_resp.status_code})", models=models, tier=tier, metadata=metadata, http_status=gen_resp.status_code, raw_response=gen_resp.text)
                
            return ValidationResult(ValidationStatus.ERROR, f"Status {gen_resp.status_code}", http_status=gen_resp.status_code, raw_response=gen_resp.text)
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
        
        metadata = {}
        # Try to decode JWT if possible (sk_header.payload.signature)
        try:
            token_part = api_key[3:] if api_key.startswith("sk_") else api_key
            if "." in token_part:
                parts = token_part.split(".")
                if len(parts) >= 2:
                    payload_b64 = parts[1]
                    missing_padding = len(payload_b64) % 4
                    if missing_padding:
                        payload_b64 += "=" * (4 - missing_padding)
                    payload_json = base64.b64decode(payload_b64).decode("utf-8", errors="ignore")
                    payload = json.loads(payload_json)
                    
                    if "email" in payload: metadata["email"] = payload["email"]
                    if "id" in payload: metadata["user_id"] = payload["id"]
                    if "name" in payload: metadata["name"] = payload["name"]
                    if "role" in payload: metadata["role"] = payload["role"]
        except:
            pass

        try:
            resp = requests.get("https://video.a2e.ai/api/v1/user/remainingCoins", headers=headers, timeout=15)
            
            # A2E sometimes returns 200 even for invalid keys, check internal code
            if self._is_success(resp.status_code):
                try:
                    data = resp.json()
                    internal_code = data.get("code")
                    
                    if internal_code == 401 or internal_code == 403:
                        return ValidationResult(ValidationStatus.UNAUTHORIZED, data.get("msg", "Invalid Key"), http_status=resp.status_code)
                    
                    if internal_code == 200:
                        # Extract coins robustly
                        coins_data = data.get("data", {})
                        coins = 0
                        if isinstance(coins_data, dict):
                            coins = coins_data.get("coins", 0)
                        elif isinstance(coins_data, (int, float)):
                            coins = coins_data
                        
                        # Infer tier from credits if possible (Free: 30, Pro: 60, Ultra: 90)
                        tier = None
                        if coins == 30: tier = "Free (Daily Bonus)"
                        elif coins == 60: tier = "Pro (Daily Bonus)"
                        elif coins == 90: tier = "Ultra (Daily Bonus)"
                        
                        status = ValidationStatus.VALID if coins > 0 else ValidationStatus.QUOTA_EXHAUSTED
                        return ValidationResult(
                            status, 
                            balance=f"{coins} Coins", 
                            account_tier=tier,
                            metadata=metadata if metadata else None,
                            http_status=resp.status_code, 
                            raw_response=resp.text
                        )
                except:
                    # If JSON parsing fails but status was 200, assume valid but restricted info
                    return ValidationResult(ValidationStatus.VALID, "Valid Key (details unavailable)", http_status=resp.status_code)

            if resp.status_code == 401 or resp.status_code == 403:
                return ValidationResult(ValidationStatus.UNAUTHORIZED, "Invalid Key", http_status=resp.status_code)
            
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
                root = resp.json()
                data = root.get('data', {})
                
                usage = data.get('usage', 0)
                is_free = data.get('is_free_tier', False)
                rem = data.get('limit_remaining')
                limit = data.get('limit')
                label = data.get('label')
                
                # Balance formatting
                if is_free:
                    balance = f"Free Tier Access (Usage: ${usage:.4f})"
                    status = ValidationStatus.VALID
                elif rem is not None:
                    limit_info = f" / ${limit:.4f}" if limit is not None else ""
                    balance = f"${rem:.4f}{limit_info} remaining"
                    status = ValidationStatus.VALID if rem > 0 else ValidationStatus.QUOTA_EXHAUSTED
                else:
                    balance = f"No key limit (Used: ${usage:.4f})"
                    status = ValidationStatus.VALID
                
                # Tier formatting
                tier_name = "Free Tier" if is_free else "Paid Tier"
                if label and label not in api_key:
                    tier = f"{tier_name} (Label: {label})"
                else:
                    tier = tier_name
                
                return ValidationResult(status, balance=balance, tier=tier, metadata=data, http_status=resp.status_code, raw_response=resp.text)
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
            # Trial keys use x-trial-endpoint-call-remaining, fallback to ratelimit
            rem = h.get('x-trial-endpoint-call-remaining') or h.get('x-ratelimit-remaining')
            # Extract limit: trial keys use x-trial-endpoint-call-limit, paid might use monthly
            limit = h.get('x-trial-endpoint-call-limit') or h.get('x-endpoint-monthly-call-limit') or h.get('x-ratelimit-limit')
            
            is_trial = 'x-trial-endpoint-call-limit' in h or "Trial key" in resp.text
            
            tier = f"Trial (Limit: {limit})" if is_trial else f"Paid (Limit: {limit})"
            # If both rem and limit exist, show '39/40' format
            if rem and limit:
                balance = f"{rem} / {limit} calls remaining"
            else:
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
            A2EProvider(), ElevenLabsProvider(), PiAPIProvider(), GroqProvider(), MistralProvider(),
            PerplexityProvider(), RunwayProvider(), OpenRouterProvider(),
            TogetherAIProvider(), CohereProvider(), VoyageAIProvider(), XAIProvider(),
            HuggingFaceProvider(), ReplicateProvider(), StabilityAIProvider(), PolloAIProvider(),
            SendGridProvider(), SlackProvider(), CerebrasProvider()
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
