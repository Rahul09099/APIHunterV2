/**
 * APIHUNTER V2 - CONTROL CENTER CLIENT ENGINE
 */

const STATE = {
    token: localStorage.getItem('X-Node-Token') || 'default_admin_token_2026',
    isAdmin: false,
    activeTab: 'dashboard-tab',
    keys: [],
    filteredKeys: [],
    queries: [],
    tokens: [],
    workers: [],
    currentPage: 1,
    pageSize: 15,
    refreshIntervalId: null,
    apiTypes: []
};

// UI Element Cache
const UI = {
    tokenInput: document.getElementById('node-token-input'),
    saveTokenBtn: document.getElementById('save-token-btn'),
    navItems: document.querySelectorAll('.nav-item'),
    tabContents: document.querySelectorAll('.tab-content'),
    pageTitleText: document.getElementById('page-title-text'),
    
    // Stats Cards
    activeWorkersVal: document.getElementById('stat-active-workers'),
    totalKeysVal: document.getElementById('stat-total-keys'),
    activeQueriesVal: document.getElementById('stat-active-queries'),
    githubTokensVal: document.getElementById('stat-github-tokens'),
    lastSignalVal: document.getElementById('stat-last-signal'),
    
    // Charts (Bars)
    barValidCredits: document.getElementById('bar-valid-credits'),
    barValidNoCredits: document.getElementById('bar-valid-nocredits'),
    barUnverified: document.getElementById('bar-unverified'),
    barInvalid: document.getElementById('bar-invalid'),
    
    countValidCredits: document.getElementById('count-valid-credits'),
    countValidNoCredits: document.getElementById('count-valid-nocredits'),
    countUnverified: document.getElementById('count-unverified'),
    countInvalid: document.getElementById('count-invalid'),
    
    // Job Runner Elements
    startScraperBtn: document.getElementById('start-scraper-btn'),
    startVerifierBtn: document.getElementById('start-verifier-btn'),
    verifierTypesInput: document.getElementById('verifier-types-select'),
    verifierReverifyCheck: document.getElementById('verifier-reverify-check'),
    jobsTbody: document.getElementById('jobs-tbody'),
    
    // Keys Explorer Elements
    keySearchInput: document.getElementById('key-search'),
    filterStatusSelect: document.getElementById('filter-status'),
    filterTypeSelect: document.getElementById('filter-type'),
    resetFiltersBtn: document.getElementById('reset-filters-btn'),
    keysTbody: document.getElementById('keys-tbody'),
    keysTotalCountText: document.getElementById('keys-total-count'),
    prevPageBtn: document.getElementById('prev-page-btn'),
    nextPageBtn: document.getElementById('next-page-btn'),
    pageNumDisplay: document.getElementById('page-num-display'),
    
    // Workers Elements
    workersTbody: document.getElementById('workers-tbody'),
    
    // Config Elements
    newQueryInput: document.getElementById('new-query-input'),
    addQueryBtn: document.getElementById('add-query-btn'),
    queriesList: document.getElementById('queries-list'),
    
    newTokenInput: document.getElementById('new-token-input'),
    addTokenBtn: document.getElementById('add-token-btn'),
    tokensList: document.getElementById('tokens-list'),
    
    exportJsonBtn: document.getElementById('export-json-btn'),
    exportCsvBtn: document.getElementById('export-csv-btn'),
    
    resetConfirmInput: document.getElementById('reset-confirm-input'),
    resetDbBtn: document.getElementById('reset-db-btn'),
    
    // Modal Details Inspector
    keyModal: document.getElementById('key-modal'),
    modalCloseBtn: document.getElementById('modal-close-btn'),
    modalFullKey: document.getElementById('modal-full-key'),
    modalCopyBtn: document.getElementById('modal-copy-btn'),
    modalKeyType: document.getElementById('modal-key-type'),
    modalKeyStatus: document.getElementById('modal-key-status'),
    modalKeyBalance: document.getElementById('modal-key-balance'),
    modalKeyTier: document.getElementById('modal-key-tier'),
    modalKeyResponse: document.getElementById('modal-key-response')
};

// Initialize Application
document.addEventListener('DOMContentLoaded', () => {
    // Populate saved token
    if (STATE.token) {
        UI.tokenInput.value = STATE.token;
    }
    
    setupEventListeners();
    validateSession();
    fetchApiTypes();
    
    // Start auto polling loop (every 10 seconds)
    STATE.refreshIntervalId = setInterval(runPollingCycle, 10000);
});

// Event Listeners Routing
function setupEventListeners() {
    // Token Save Action
    UI.saveTokenBtn.addEventListener('click', () => {
        const val = UI.tokenInput.value.trim();
        if (val) {
            STATE.token = val;
            localStorage.setItem('X-Node-Token', val);
            showNotification('Token saved. Verifying authorization...', 'info');
            validateSession();
        }
    });

    // Tab Switch Coordinator
    UI.navItems.forEach(item => {
        item.addEventListener('click', () => {
            const targetTab = item.getAttribute('data-tab');
            switchTab(targetTab);
        });
    });

    // Key modal close actions
    UI.modalCloseBtn.addEventListener('click', () => UI.keyModal.classList.add('hidden'));
    UI.keyModal.addEventListener('click', (e) => {
        if (e.target === UI.keyModal) {
            UI.keyModal.classList.add('hidden');
        }
    });
    
    // Copy button inside details modal
    UI.modalCopyBtn.addEventListener('click', () => {
        navigator.clipboard.writeText(UI.modalFullKey.innerText);
        showNotification('Full key copied to clipboard!', 'success');
    });

    // Keys Filtering actions
    UI.keySearchInput.addEventListener('input', applyKeysFilters);
    UI.filterStatusSelect.addEventListener('change', applyKeysFilters);
    UI.filterTypeSelect.addEventListener('change', applyKeysFilters);
    UI.resetFiltersBtn.addEventListener('click', () => {
        UI.keySearchInput.value = '';
        UI.filterStatusSelect.value = 'All';
        UI.filterTypeSelect.value = 'All';
        applyKeysFilters();
    });

    // Key pagination controllers
    UI.prevPageBtn.addEventListener('click', () => {
        if (STATE.currentPage > 1) {
            STATE.currentPage--;
            renderKeysTable();
        }
    });
    UI.nextPageBtn.addEventListener('click', () => {
        const maxPage = Math.ceil(STATE.filteredKeys.length / STATE.pageSize);
        if (STATE.currentPage < maxPage) {
            STATE.currentPage++;
            renderKeysTable();
        }
    });

    // Job Buttons triggers
    UI.startScraperBtn.addEventListener('click', triggerScraperJob);
    UI.startVerifierBtn.addEventListener('click', triggerVerifierJob);

    // Config: Add query
    UI.addQueryBtn.addEventListener('click', addSearchQuery);
    UI.newQueryInput.addEventListener('keypress', (e) => {
        if (e.key === 'Enter') addSearchQuery();
    });

    // Config: Add token
    UI.addTokenBtn.addEventListener('click', addGitHubToken);
    UI.newTokenInput.addEventListener('keypress', (e) => {
        if (e.key === 'Enter') addGitHubToken();
    });

    // Export keys redirections
    UI.exportJsonBtn.addEventListener('click', () => {
        window.open(`/api/Config/export-keys?format=json&nodeToken=${encodeURIComponent(STATE.token)}`, '_blank');
    });
    UI.exportCsvBtn.addEventListener('click', () => {
        window.open(`/api/Config/export-keys?format=csv&nodeToken=${encodeURIComponent(STATE.token)}`, '_blank');
    });

    // Database Reset controls
    UI.resetConfirmInput.addEventListener('input', () => {
        const val = UI.resetConfirmInput.value.trim();
        UI.resetDbBtn.disabled = (val !== 'CONFIRM_RESET');
    });
    UI.resetDbBtn.addEventListener('click', resetDatabase);
}

// Switch view tabs
function switchTab(tabId) {
    // UI states
    UI.navItems.forEach(i => i.classList.remove('active'));
    UI.tabContents.forEach(c => c.classList.remove('active'));
    
    const targetItem = document.querySelector(`.nav-item[data-tab="${tabId}"]`);
    const targetContent = document.getElementById(tabId);
    
    if (targetItem && targetContent) {
        targetItem.classList.add('active');
        targetContent.classList.add('active');
        STATE.activeTab = tabId;
        
        // Update Title text
        const titles = {
            'dashboard-tab': 'Dashboard Overview',
            'keys-tab': 'Keys Inspector',
            'workers-tab': 'Workers Network Monitor',
            'config-tab': 'Settings & Controls'
        };
        UI.pageTitleText.innerText = titles[tabId] || 'Control Center';
        
        // Trigger specific load logic
        runTabLoadingLogic(tabId);
    }
}

// Load data specifically needed for selected tab
function runTabLoadingLogic(tabId) {
    if (!STATE.token) return;
    
    switch (tabId) {
        case 'dashboard-tab':
            fetchDashboardStats();
            fetchBackgroundJobs();
            break;
        case 'keys-tab':
            fetchRecentKeys();
            break;
        case 'workers-tab':
            fetchWorkerNodes();
            break;
        case 'config-tab':
            fetchQueries();
            fetchGitHubTokens();
            break;
    }
}

// Core loop refreshing active views
function runPollingCycle() {
    runTabLoadingLogic(STATE.activeTab);
}

// Request Helper wrapping auth header
async function apiCall(endpoint, method = 'GET', body = null) {
    const headers = {
        'Accept': 'application/json'
    };
    
    if (STATE.token) {
        headers['X-Node-Token'] = STATE.token;
    }
    
    const config = {
        method,
        headers
    };
    
    if (body) {
        headers['Content-Type'] = 'application/json';
        config.body = JSON.stringify(body);
    }
    
    const response = await fetch(endpoint, config);
    
    if (response.status === 401) {
        throw new Error('UNAUTHORIZED');
    }
    
    if (!response.ok) {
        const text = await response.text();
        let errMsg = text;
        try {
            const json = JSON.parse(text);
            errMsg = json.message || text;
        } catch(e) {}
        throw new Error(errMsg);
    }
    
    const contentType = response.headers.get('content-type');
    if (contentType && contentType.includes('application/json')) {
        return await response.json();
    }
    return await response.text();
}

// Validate Token & Check Admin Role
async function validateSession() {
    if (!STATE.token) {
        setAuthorizedUI(false);
        showNotification('Please enter a Node Token in the top right to get started.', 'warning');
        return;
    }
    
    try {
        // Try getting github tokens as user authentication check
        const response = await apiCall('/api/Status/github-tokens');
        // If successful, user is authorized
        setAuthorizedUI(true);
        
        // Check if user is Admin by querying admin configuration endpoint (will fail if not admin)
        try {
            await apiCall('/api/Status/search-queries');
            STATE.isAdmin = true;
            document.querySelectorAll('.admin-only').forEach(el => el.classList.remove('hidden'));
        } catch(err) {
            STATE.isAdmin = false;
            document.querySelectorAll('.admin-only').forEach(el => el.classList.add('hidden'));
            if (STATE.activeTab === 'config-tab') {
                switchTab('dashboard-tab');
            }
        }
        
        // Fetch current active tab data
        runTabLoadingLogic(STATE.activeTab);
        
    } catch (err) {
        console.error('Session validation error:', err);
        setAuthorizedUI(false);
        if (err.message === 'UNAUTHORIZED') {
            showNotification('Invalid Node Token. Access Denied.', 'danger');
        } else {
            showNotification('Unable to connect to Master API server.', 'danger');
        }
    }
}

// Update UI lock state based on authorization
function setAuthorizedUI(isAuth) {
    if (isAuth) {
        UI.tokenInput.classList.remove('input-danger');
        UI.tokenInput.style.borderColor = 'var(--accent-emerald)';
        document.querySelector('.connection-status').innerHTML = `
            <span class="pulse-dot green"></span>
            <span class="status-text">Authenticated</span>
        `;
    } else {
        UI.tokenInput.classList.add('input-danger');
        UI.tokenInput.style.borderColor = 'var(--accent-red)';
        document.querySelector('.connection-status').innerHTML = `
            <span class="pulse-dot red"></span>
            <span class="status-text">Unauthorized</span>
        `;
        // Hide Admin tabs
        document.querySelectorAll('.admin-only').forEach(el => el.classList.add('hidden'));
    }
}

// Fetch Master API Types List
async function fetchApiTypes() {
    try {
        const types = await apiCall('/api/Verifier/api-types');
        STATE.apiTypes = types;
        
        // Populate filter type dropdown
        UI.filterTypeSelect.innerHTML = '<option value="All">All API Types</option>';
        types.forEach(type => {
            const opt = document.createElement('option');
            opt.value = type.name;
            opt.innerText = `${type.name} (${type.category})`;
            UI.filterTypeSelect.appendChild(opt);
        });
    } catch(err) {
        console.error('Failed to load api-types:', err);
    }
}

// Tab: Dashboard Stats Fetch
async function fetchDashboardStats() {
    try {
        // Stats Overview (Public data but contains key totals)
        const nodeStats = await apiCall('/api/v1/Nodes/stats');
        UI.activeWorkersVal.innerText = nodeStats.activeNodes;
        UI.totalKeysVal.innerText = nodeStats.totalKeys;
        UI.activeQueriesVal.innerText = nodeStats.activeQueries;
        
        if (nodeStats.lastDiscoveryAt) {
            const date = new Date(nodeStats.lastDiscoveryAt);
            UI.lastSignalVal.innerText = date.toLocaleString();
        } else {
            UI.lastSignalVal.innerText = 'NO ACTIVITY RECORDED';
        }
        
        // Detailed Status
        const stats = await apiCall('/api/Status');
        UI.githubTokensVal.innerText = stats.gitHubTokensCount;
        
        // Render bar chart percentages
        const total = stats.totalKeys || 0;
        
        const validCredsCount = stats.validKeys || 0;
        const validNoCredsCount = stats.quotaExhaustedKeys || 0;
        const unverifiedCount = stats.unverifiedKeys || 0;
        const invalidCount = stats.invalidKeys || 0;
        
        const validCredsPct = total > 0 ? (validCredsCount / total * 100).toFixed(1) : 0;
        const validNoCredsPct = total > 0 ? (validNoCredsCount / total * 100).toFixed(1) : 0;
        const unverifiedPct = total > 0 ? (unverifiedCount / total * 100).toFixed(1) : 0;
        const invalidPct = total > 0 ? (invalidCount / total * 100).toFixed(1) : 0;
        
        UI.barValidCredits.style.width = `${validCredsPct}%`;
        UI.countValidCredits.innerText = `${validCredsCount} (${validCredsPct}%)`;
        
        UI.barValidNoCredits.style.width = `${validNoCredsPct}%`;
        UI.countValidNoCredits.innerText = `${validNoCredsCount} (${validNoCredsPct}%)`;
        
        UI.barUnverified.style.width = `${unverifiedPct}%`;
        UI.countUnverified.innerText = `${unverifiedCount} (${unverifiedPct}%)`;
        
        UI.barInvalid.style.width = `${invalidPct}%`;
        UI.countInvalid.innerText = `${invalidCount} (${invalidPct}%)`;
        
    } catch (err) {
        console.error('Failed to load dashboard statistics:', err);
    }
}

// Tab: Background Jobs Fetch
async function fetchBackgroundJobs() {
    try {
        const scraperJobs = await apiCall('/api/Scraper/jobs');
        const verifierJobs = await apiCall('/api/Verifier/jobs');
        
        const allJobs = [...scraperJobs, ...verifierJobs];
        
        // Filter jobs to running and recent ones
        UI.jobsTbody.innerHTML = '';
        
        if (allJobs.length === 0) {
            UI.jobsTbody.innerHTML = `
                <tr>
                    <td colspan="7" class="text-center text-muted">No active background jobs running.</td>
                </tr>
            `;
            return;
        }
        
        allJobs.forEach(job => {
            const tr = document.createElement('tr');
            
            // Format status class
            let badgeClass = 'badge-muted';
            if (job.status === 'Running') badgeClass = 'badge-cyan animate-pulse';
            else if (job.status === 'Completed') badgeClass = 'badge-emerald';
            else if (job.status === 'Cancelled') badgeClass = 'badge-amber';
            else if (job.status === 'Failed') badgeClass = 'badge-red';
            
            // Calculate runtime
            const started = new Date(job.startedAt);
            const ended = job.completedAt ? new Date(job.completedAt) : new Date();
            const diffMs = ended - started;
            const diffSec = Math.floor(diffMs / 1000);
            const m = Math.floor(diffSec / 60);
            const s = diffSec % 60;
            const durationStr = `${m}m ${s}s`;
            
            // Stop button if running
            let actionBtn = '';
            if (job.status === 'Running') {
                actionBtn = `<button class="btn btn-sm btn-danger py-1 px-2" onclick="stopJob('${job.jobId}', '${job.jobType}')">Stop</button>`;
            } else {
                actionBtn = `<span class="text-muted">-</span>`;
            }
            
            tr.innerHTML = `
                <td><code class="key-preview" style="max-width: 100px;">${job.jobId.substring(0, 8)}...</code></td>
                <td><span class="badge ${job.jobType === 'Scraper' ? 'badge-blue' : 'badge-emerald'}">${job.jobType}</span></td>
                <td><span class="badge ${badgeClass}">${job.status}</span></td>
                <td>${started.toLocaleString()}</td>
                <td>${durationStr}</td>
                <td><code>${job.ownerTelegramId || 'System'}</code></td>
                <td>${actionBtn}</td>
            `;
            
            UI.jobsTbody.appendChild(tr);
        });
        
    } catch(err) {
        console.error('Failed to load active jobs:', err);
    }
}

// Stop a running job
async function stopJob(jobId, jobType) {
    const route = jobType === 'Scraper' ? `/api/Scraper/stop/${jobId}` : `/api/Verifier/stop/${jobId}`;
    try {
        const response = await apiCall(route, 'POST');
        showNotification(response.message || 'Job stop requested.', 'success');
        fetchBackgroundJobs();
    } catch(err) {
        showNotification(`Failed to stop job: ${err.message}`, 'danger');
    }
}
window.stopJob = stopJob; // Expose to HTML inline listener

// Trigger Scraper Job
async function triggerScraperJob() {
    UI.startScraperBtn.disabled = true;
    UI.startScraperBtn.querySelector('.btn-spinner').classList.remove('hidden');
    
    try {
        const response = await apiCall('/api/Scraper/start', 'POST');
        showNotification(`Scraper Job started: ID ${response.jobId.substring(0, 8)}`, 'success');
        fetchBackgroundJobs();
    } catch(err) {
        showNotification(`Failed to start scraper: ${err.message}`, 'danger');
    } finally {
        UI.startScraperBtn.disabled = false;
        UI.startScraperBtn.querySelector('.btn-spinner').classList.add('hidden');
    }
}

// Trigger Verifier Job
async function triggerVerifierJob() {
    UI.startVerifierBtn.disabled = true;
    UI.startVerifierBtn.querySelector('.btn-spinner').classList.remove('hidden');
    
    const apiTypes = UI.verifierTypesInput.value.trim();
    const reverify = UI.verifierReverifyCheck.checked;
    
    const queryParams = new URLSearchParams();
    if (apiTypes) queryParams.append('apiTypes', apiTypes);
    queryParams.append('reverify', reverify);
    
    try {
        const response = await apiCall(`/api/Verifier/start?${queryParams.toString()}`, 'POST');
        showNotification(`Verifier Job started: ID ${response.jobId.substring(0, 8)}`, 'success');
        fetchBackgroundJobs();
    } catch(err) {
        showNotification(`Failed to start verifier: ${err.message}`, 'danger');
    } finally {
        UI.startVerifierBtn.disabled = false;
        UI.startVerifierBtn.querySelector('.btn-spinner').classList.add('hidden');
    }
}

// Tab: Keys Explorer Fetch
async function fetchRecentKeys() {
    UI.keysTbody.innerHTML = `
        <tr>
            <td colspan="8" class="text-center"><div class="btn-spinner" style="margin: 0 auto;"></div></td>
        </tr>
    `;
    
    try {
        const data = await apiCall('/api/Status/recent-keys?limit=300');
        STATE.keys = data;
        applyKeysFilters();
    } catch(err) {
        console.error('Failed to fetch recent keys:', err);
        UI.keysTbody.innerHTML = `
            <tr>
                <td colspan="8" class="text-center text-danger">Error loading keys: ${err.message}</td>
            </tr>
        `;
    }
}

// Apply searches & filters on cached keys
function applyKeysFilters() {
    const searchVal = UI.keySearchInput.value.toLowerCase().trim();
    const statusVal = UI.filterStatusSelect.value;
    const typeVal = UI.filterTypeSelect.value;
    
    STATE.filteredKeys = STATE.keys.filter(key => {
        // Status matching
        if (statusVal !== 'All') {
            if (statusVal === 'Valid' && key.status !== 'Valid') return false;
            if (statusVal === 'ValidNoCredits' && key.status !== 'ValidNoCredits') return false;
            if (statusVal === 'Unverified' && key.status !== 'Unverified') return false;
            if (statusVal === 'Invalid' && key.status !== 'Invalid') return false;
            if (statusVal === 'Error' && key.status !== 'Error') return false;
        }
        
        // Type matching
        if (typeVal !== 'All' && key.apiType !== typeVal) return false;
        
        // Keyword text matching
        if (searchVal) {
            const inPreview = key.keyPreview && key.keyPreview.toLowerCase().includes(searchVal);
            const inType = key.apiType && key.apiType.toLowerCase().includes(searchVal);
            const inStatus = key.status && key.status.toLowerCase().includes(searchVal);
            const inTier = key.accountTier && key.accountTier.toLowerCase().includes(searchVal);
            
            if (!inPreview && !inType && !inStatus && !inTier) return false;
        }
        
        return true;
    });
    
    STATE.currentPage = 1;
    renderKeysTable();
}

// Render paginated keys list
function renderKeysTable() {
    UI.keysTbody.innerHTML = '';
    
    const totalCount = STATE.filteredKeys.length;
    UI.keysTotalCountText.innerText = `Found ${totalCount} records`;
    
    if (totalCount === 0) {
        UI.keysTbody.innerHTML = `
            <tr>
                <td colspan="8" class="text-center text-muted">No API keys match your criteria.</td>
            </tr>
        `;
        UI.prevPageBtn.disabled = true;
        UI.nextPageBtn.disabled = true;
        UI.pageNumDisplay.innerText = 'Page 1 of 1';
        return;
    }
    
    const maxPage = Math.ceil(totalCount / STATE.pageSize);
    if (STATE.currentPage > maxPage) STATE.currentPage = maxPage || 1;
    
    const startIdx = (STATE.currentPage - 1) * STATE.pageSize;
    const pageKeys = STATE.filteredKeys.slice(startIdx, startIdx + STATE.pageSize);
    
    // Toggle Pagination Buttons
    UI.prevPageBtn.disabled = (STATE.currentPage === 1);
    UI.nextPageBtn.disabled = (STATE.currentPage === maxPage);
    UI.pageNumDisplay.innerText = `Page ${STATE.currentPage} of ${maxPage}`;
    
    pageKeys.forEach(key => {
        const tr = document.createElement('tr');
        
        // API Type badge styling
        let typeBadgeClass = 'badge-muted';
        if (key.apiType === 'OpenAI') typeBadgeClass = 'badge-emerald';
        else if (key.apiType === 'Anthropic') typeBadgeClass = 'badge-cyan';
        else if (key.apiType === 'Google') typeBadgeClass = 'badge-blue';
        else if (key.apiType === 'DeepSeek') typeBadgeClass = 'badge-amber';
        
        // Status badge styling
        let statusBadgeClass = 'badge-muted';
        if (key.status === 'Valid') statusBadgeClass = 'badge-emerald';
        else if (key.status === 'ValidNoCredits') statusBadgeClass = 'badge-amber';
        else if (key.status === 'Invalid') statusBadgeClass = 'badge-red';
        else if (key.status === 'Unverified') statusBadgeClass = 'badge-blue';
        else if (key.status === 'Error') statusBadgeClass = 'badge-red';
        
        // Date formats
        const foundDate = new Date(key.firstFoundUTC).toLocaleDateString();
        const lastCheck = key.lastCheckedUTC ? new Date(key.lastCheckedUTC).toLocaleDateString() : 'Never';
        
        tr.innerHTML = `
            <td><span class="badge ${typeBadgeClass}">${key.apiType}</span></td>
            <td><span class="badge ${statusBadgeClass}">${key.status}</span></td>
            <td>
                <div class="key-preview-container">
                    <code class="key-preview">${key.keyPreview}</code>
                    <button class="btn btn-secondary btn-sm p-1" style="height:24px;width:24px;" onclick="copySnippet('${key.keyPreview.replace('...', '')}')">📋</button>
                </div>
            </td>
            <td><code class="text-muted">${key.balance || '-'}</code></td>
            <td><span class="text-muted">${key.accountTier || '-'}</span></td>
            <td>${foundDate}</td>
            <td>${lastCheck}</td>
            <td>
                <button class="btn btn-secondary btn-sm" onclick="inspectKeyDetails('${key.id}')">Inspect</button>
            </td>
        `;
        
        UI.keysTbody.appendChild(tr);
    });
}

// Copy a snippet text helper
function copySnippet(text) {
    navigator.clipboard.writeText(text);
    showNotification('Key snippet copied!', 'info');
}
window.copySnippet = copySnippet;

// Inspector modal trigger
async function inspectKeyDetails(keyId) {
    const key = STATE.keys.find(k => k.id == keyId);
    if (!key) return;
    
    UI.modalFullKey.innerText = 'Click copy button to copy full key details';
    
    // Retrieve full unmasked key if available via CSV/JSON exporter (or use keyPreview if protected)
    UI.modalFullKey.innerText = key.keyPreview;
    
    UI.modalKeyType.innerText = key.apiType;
    UI.modalKeyStatus.innerText = key.status;
    UI.modalKeyBalance.innerText = key.balance || 'No balance data available';
    UI.modalKeyTier.innerText = key.accountTier || 'Unknown Tier';
    
    // Process response JSON format
    let rawResponse = key.validationResponse || '{}';
    try {
        const parsed = JSON.parse(rawResponse);
        UI.modalKeyResponse.innerText = JSON.stringify(parsed, null, 4);
    } catch(e) {
        UI.modalKeyResponse.innerText = rawResponse;
    }
    
    UI.keyModal.classList.remove('hidden');
    
    // Attempt to retrieve full token value by fetching export details
    try {
        const fullData = await apiCall(`/api/Config/export-keys?format=json`);
        const matchingKey = fullData.find(k => k.id == keyId);
        if (matchingKey) {
            UI.modalFullKey.innerText = matchingKey.apiKey;
        }
    } catch(err) {
        console.warn('Failed to retrieve full unmasked key, displaying preview. Admin authentication required for exports.');
    }
}
window.inspectKeyDetails = inspectKeyDetails;

// Tab: Worker Nodes Fetch
async function fetchWorkerNodes() {
    UI.workersTbody.innerHTML = `
        <tr>
            <td colspan="6" class="text-center"><div class="btn-spinner" style="margin: 0 auto;"></div></td>
        </tr>
    `;
    
    try {
        const data = await apiCall('/api/v1/Nodes');
        STATE.workers = data;
        UI.workersTbody.innerHTML = '';
        
        if (data.length === 0) {
            UI.workersTbody.innerHTML = `
                <tr>
                    <td colspan="6" class="text-center text-muted">No worker nodes registered in the database.</td>
                </tr>
            `;
            return;
        }
        
        data.forEach(node => {
            const tr = document.createElement('tr');
            
            const lastPing = node.lastNodeHeartbeatUtc 
                ? new Date(node.lastNodeHeartbeatUtc).toLocaleString() 
                : 'Never';
                
            const activeBadgeClass = node.isActive ? 'badge-emerald' : 'badge-muted';
            const activeStatusText = node.isActive ? 'Online' : 'Offline';
            
            tr.innerHTML = `
                <td><code>${node.telegramId}</code></td>
                <td>@${node.username || 'unknown'}</td>
                <td><code class="text-muted">${node.nodeUrl || 'Direct IP'}</code></td>
                <td>${lastPing}</td>
                <td><span class="badge ${node.isAdmin ? 'badge-pink' : 'badge-blue'}">${node.isAdmin ? 'Master Admin' : 'Ghost Worker'}</span></td>
                <td>
                    <span class="badge ${activeBadgeClass}">
                        <span class="pulse-dot ${node.isActive ? 'green' : 'red'}" style="margin-right: 5px;"></span>
                        ${activeStatusText}
                    </span>
                </td>
            `;
            UI.workersTbody.appendChild(tr);
        });
        
    } catch(err) {
        console.error('Failed to load workers:', err);
        UI.workersTbody.innerHTML = `
            <tr>
                <td colspan="6" class="text-center text-danger">Error loading workers: ${err.message === 'UNAUTHORIZED' ? 'Admin token required' : err.message}</td>
            </tr>
        `;
    }
}

// Tab: Search Queries config list
async function fetchQueries() {
    UI.queriesList.innerHTML = '<div class="text-muted">Loading queries...</div>';
    
    try {
        const data = await apiCall('/api/Status/search-queries');
        STATE.queries = data;
        UI.queriesList.innerHTML = '';
        
        if (data.length === 0) {
            UI.queriesList.innerHTML = '<div class="text-center text-muted p-3">No search queries configured.</div>';
            return;
        }
        
        data.forEach(q => {
            const div = document.createElement('div');
            div.className = 'config-item-row animate-fade-in';
            div.innerHTML = `
                <div class="config-item-name">${escapeHtml(q.query)}</div>
                <div class="config-item-controls">
                    <label class="switch">
                        <input type="checkbox" ${q.isEnabled ? 'checked' : ''} onchange="toggleQuery('${q.id}')">
                        <span class="slider"></span>
                    </label>
                    <button class="btn btn-sm btn-danger py-1 px-2" onclick="deleteQuery('${q.id}')">Delete</button>
                </div>
            `;
            UI.queriesList.appendChild(div);
        });
        
    } catch(err) {
        console.error('Failed to fetch queries:', err);
        UI.queriesList.innerHTML = `<div class="text-danger p-3">Error: ${err.message}</div>`;
    }
}

// Config: Add Search Query
async function addSearchQuery() {
    const val = UI.newQueryInput.value.trim();
    if (!val) return;
    
    UI.addQueryBtn.disabled = true;
    
    try {
        await apiCall('/api/Config/search-query', 'POST', { query: val });
        UI.newQueryInput.value = '';
        showNotification('Search query added successfully.', 'success');
        fetchQueries();
    } catch(err) {
        showNotification(`Failed to add query: ${err.message}`, 'danger');
    } finally {
        UI.addQueryBtn.disabled = false;
    }
}

// Config: Toggle Search Query Status
async function toggleQuery(id) {
    try {
        const response = await apiCall(`/api/Config/search-query/${id}/toggle`, 'PATCH');
        showNotification(response.message || 'Search query toggled.', 'success');
        fetchQueries();
    } catch(err) {
        showNotification(`Failed to toggle query: ${err.message}`, 'danger');
    }
}
window.toggleQuery = toggleQuery;

// Config: Delete Search Query
async function deleteQuery(id) {
    if (!confirm('Are you sure you want to delete this search query? Workers will stop scanning this pattern.')) return;
    
    try {
        const response = await apiCall(`/api/Config/search-query/${id}`, 'DELETE');
        showNotification(response.message || 'Search query deleted.', 'success');
        fetchQueries();
    } catch(err) {
        showNotification(`Failed to delete query: ${err.message}`, 'danger');
    }
}
window.deleteQuery = deleteQuery;

// Tab: GitHub API Tokens config list
async function fetchGitHubTokens() {
    UI.tokensList.innerHTML = '<div class="text-muted">Loading tokens...</div>';
    
    try {
        const data = await apiCall('/api/Status/github-tokens');
        STATE.tokens = data;
        UI.tokensList.innerHTML = '';
        
        if (data.length === 0) {
            UI.tokensList.innerHTML = '<div class="text-center text-muted p-3">No GitHub tokens registered. Scrapers might not function.</div>';
            return;
        }
        
        data.forEach(t => {
            const div = document.createElement('div');
            div.className = 'config-item-row animate-fade-in';
            
            const lastUsedStr = t.lastUsedUTC 
                ? `Last used: ${new Date(t.lastUsedUTC).toLocaleString()}` 
                : 'Never used';
                
            div.innerHTML = `
                <div>
                    <div class="config-item-name">${t.tokenPreview}</div>
                    <small class="text-muted" style="font-size:11px;">${lastUsedStr}</small>
                </div>
                <div class="config-item-controls">
                    <button class="btn btn-sm btn-danger py-1 px-2" onclick="deleteGitHubToken('${t.id}')">Delete</button>
                </div>
            `;
            UI.tokensList.appendChild(div);
        });
        
    } catch(err) {
        console.error('Failed to fetch tokens:', err);
        UI.tokensList.innerHTML = `<div class="text-danger p-3">Error: ${err.message}</div>`;
    }
}

// Config: Add GitHub Token
async function addGitHubToken() {
    const val = UI.newTokenInput.value.trim();
    if (!val) return;
    
    UI.addTokenBtn.disabled = true;
    
    try {
        await apiCall('/api/Config/github-token', 'POST', { token: val });
        UI.newTokenInput.value = '';
        showNotification('GitHub scraping token added successfully.', 'success');
        fetchGitHubTokens();
    } catch(err) {
        showNotification(`Failed to add token: ${err.message}`, 'danger');
    } finally {
        UI.addTokenBtn.disabled = false;
    }
}

// Config: Delete GitHub Token
async function deleteGitHubToken(id) {
    if (!confirm('Are you sure you want to delete this GitHub API token? Scrapers will lose quota.')) return;
    
    try {
        const response = await apiCall(`/api/Config/github-token/${id}`, 'DELETE');
        showNotification(response.message || 'GitHub token deleted successfully.', 'success');
        fetchGitHubTokens();
    } catch(err) {
        showNotification(`Failed to delete token: ${err.message}`, 'danger');
    }
}
window.deleteGitHubToken = deleteGitHubToken;

// Config: System Reset Database
async function resetDatabase() {
    const confirmText = UI.resetConfirmInput.value.trim();
    if (confirmText !== 'CONFIRM_RESET') return;
    
    if (!confirm('🚨 WARNING! This will completely format the SQLite database, erasing all configurations, keys, tokens, nodes, and job logs. This action CANNOT BE UNDONE. Are you absolutely sure?')) return;
    
    UI.resetDbBtn.disabled = true;
    UI.resetDbBtn.innerText = 'Resetting Database...';
    
    try {
        const response = await apiCall('/api/Config/reset-database', 'POST', { confirmation: confirmText });
        showNotification(response.message || 'Database formatted successfully.', 'success');
        
        UI.resetConfirmInput.value = '';
        UI.resetDbBtn.disabled = true;
        UI.resetDbBtn.innerText = 'Reset Database';
        
        switchTab('dashboard-tab');
        validateSession();
    } catch(err) {
        showNotification(`Failed to format database: ${err.message}`, 'danger');
        UI.resetDbBtn.disabled = false;
        UI.resetDbBtn.innerText = 'Reset Database';
    }
}

// Custom Premium Toast Notifications
function showNotification(msg, type = 'info') {
    // Check if notification container exists
    let container = document.getElementById('toast-container');
    if (!container) {
        container = document.createElement('div');
        container.id = 'toast-container';
        // Add container styling directly to simplify CSS file changes
        Object.assign(container.style, {
            position: 'fixed',
            bottom: '25px',
            right: '25px',
            zIndex: '9999',
            display: 'flex',
            flexDirection: 'column',
            gap: '10px'
        });
        document.body.appendChild(container);
    }
    
    const toast = document.createElement('div');
    toast.className = 'glass animate-slide-up';
    
    // Apply styling to individual toast
    Object.assign(toast.style, {
        padding: '14px 24px',
        borderRadius: '12px',
        fontSize: '14px',
        fontWeight: '500',
        color: '#fff',
        minWidth: '280px',
        maxWidth: '400px',
        display: 'flex',
        alignItems: 'center',
        justifyContent: 'space-between',
        borderLeftWidth: '4px',
        boxShadow: '0 10px 25px rgba(0,0,0,0.3)'
    });
    
    // Choose side-border color based on type
    const colors = {
        'info': 'var(--accent-cyan)',
        'success': 'var(--accent-emerald)',
        'warning': 'var(--accent-amber)',
        'danger': 'var(--accent-red)'
    };
    toast.style.borderLeftColor = colors[type] || colors['info'];
    
    toast.innerHTML = `
        <span>${msg}</span>
        <button style="background:none;border:none;color:#94a3b8;font-size:16px;cursor:pointer;margin-left:15px;" onclick="this.parentElement.remove()">&times;</button>
    `;
    
    container.appendChild(toast);
    
    // Auto remove after 5 seconds
    setTimeout(() => {
        toast.style.opacity = '0';
        toast.style.transform = 'translateY(10px)';
        toast.style.transition = 'all 0.4s';
        setTimeout(() => toast.remove(), 400);
    }, 5000);
}

// Escape HTML utility to prevent XSS in query names
function escapeHtml(str) {
    return str
        .replace(/&/g, "&amp;")
        .replace(/</g, "&lt;")
        .replace(/>/g, "&gt;")
        .replace(/"/g, "&quot;")
        .replace(/'/g, "&#039;");
}
