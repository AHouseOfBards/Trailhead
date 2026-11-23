// Trailhead's setup page. jellyfin-web loads this as a module from <div data-controller="__plugin/trailhead.js"> and
// calls the default export with `new` and the page element (viewManager.js). The logic is in named exports so Node can
// run it without a browser (tests/web/).
//
// This file is served without a login (DashboardController.GetDashboardConfigurationPage has no [Authorize]), so it
// holds no state and no secrets. Everything comes from the /Trailhead routes, which are admin only.
//
// It uses ApiClient directly and not window.Campground: Trailhead injects no script, so that runtime can be missing.
// Text is set with textContent only. The key goes from the field to one request, and the field is cleared after.

// Dynu's pages, checked live (design/trailhead/research/dynu-live-check.md). The first three open in the popup.
export const DYNU = Object.freeze({
    signUp: 'https://www.dynu.com/en-US/ControlPanel/CreateAccount',
    apiCredentials: 'https://www.dynu.com/en-US/ControlPanel/APICredentials',
    addName: 'https://www.dynu.com/en-US/ControlPanel/AddDDNS',
    names: 'https://www.dynu.com/en-US/ControlPanel/DDNS'
});

export const POPUP_NAME = 'trailhead-dynu';
export const CHECK_DELAY_MS = 600;
export const POPUP_POLL_MS = 1000;
export const MAX_ALTERNATIVES = 3;

// Every element the script looks up by id. tests/web/page.test.js checks that each is in the HTML.
export const IDS = Object.freeze([
    'th-loading', 'th-status', 'th-done-key', 'th-change-key', 'th-done-name', 'th-done-name-text', 'th-change-name',
    'th-notice', 'th-error',
    'th-step-dynu', 'th-heading-dynu', 'th-reenter', 'th-dynu-create', 'th-dynu-have', 'th-dynu-blocked', 'th-dynu-link',
    'th-dynu-wait', 'th-key-form', 'th-key', 'th-key-save', 'th-key-cancel',
    'th-step-name', 'th-heading-name', 'th-rename-note', 'th-name-form', 'th-label', 'th-parent', 'th-check',
    'th-continue', 'th-name-cancel', 'th-result', 'th-result-text', 'th-alts', 'th-alt-buttons', 'th-full',
    'th-full-name', 'th-copy', 'th-add-open', 'th-recheck', 'th-panel', 'th-panel-open', 'th-panel-blocked', 'th-panel-link'
]);

// Used only when the strings couldn't be loaded: the page's own markup already holds English for the fixed text, and
// this covers the sentences the script writes. One sentence for every key, so a raw key never reaches the screen.
// tests/web/page.test.js checks each of these is the same text as en.json.
export const FALLBACK = Object.freeze({
    'page.generic': 'Something went wrong. Reload the page and try again.'
});

const ERROR_CODES = ['dynu_key_invalid', 'dynu_key_rejected', 'dynu_key_missing', 'dynu_unavailable', 'rate_limited',
    'bad_request', 'request_too_large', 'save_failed', 'internal'];

// ---------------------------------------------------------------- pure helpers

function lowerFirst(s) {
    return s.charAt(0).toLowerCase() + s.slice(1);
}

/** The string key for a name answer's Kind (Taken to name.kind.taken). */
export function kindKey(kind) {
    return 'name.kind.' + lowerFirst(String(kind || ''));
}

/** The string key for an invalid name's Problem (TooShort to label.tooShort). */
export function problemKey(problem) {
    return 'label.' + lowerFirst(String(problem || ''));
}

/**
 * The string key for a failed call. The code in X-Campground-Error says what happened; a code this page doesn't know
 * is read by its status, as docs/api.md asks.
 */
export function errorKey(code, status) {
    if (ERROR_CODES.indexOf(code) !== -1) {
        return 'error.' + code;
    }
    if (status === 429) { return 'error.rate_limited'; }
    if (status === 503) { return 'error.dynu_unavailable'; }
    if (status === 400) { return 'error.bad_request'; }
    if (!status) { return 'error.network'; }
    return 'error.internal';
}

/** True when a failed name call means the saved key is no good, so the page goes back to the key step. */
export function isKeyProblem(err) {
    return !!err && err.status === 409 && (err.code === 'dynu_key_rejected' || err.code === 'dynu_key_missing');
}

/** A failed key save that leaves the key worth keeping in the field: Dynu or Jellyfin was out of reach, or busy. */
export function isRetryable(err) {
    return !err || !err.status || err.status === 429 || err.status === 503 || err.status >= 500;
}

/** Wraps whatever a failed ApiClient call rejects with (a Response, or an Error) as { status, code }. */
export function toApiError(err) {
    let code = '';
    try {
        code = (err && err.headers && typeof err.headers.get === 'function' && err.headers.get('X-Campground-Error')) || '';
    } catch (e) {
        code = '';
    }
    return { status: (err && typeof err.status === 'number') ? err.status : 0, code: String(code) };
}

/** Replaces {name} placeholders. One it has no value for is left as it is. */
export function format(template, args) {
    return String(template).replace(/\{([A-Za-z0-9_]+)\}/g, function (all, name) {
        return args && Object.prototype.hasOwnProperty.call(args, name) ? String(args[name]) : all;
    });
}

/**
 * The language Jellyfin shows this administrator, read the way jellyfin-web reads it: the per-user setting, then
 * <html lang>, then data-culture, then the browser's. All of it is read from what's passed in, so a test can feed it.
 */
export function adminLanguage(env) {
    const read = function (fn) {
        try { return fn() || ''; } catch (e) { return ''; }
    };
    let lang = '';
    const userId = read(function () { return env.userId; });
    if (userId) {
        lang = read(function () { return env.storage.getItem(userId + '-language'); });
    } else {
        lang = read(function () { return env.htmlLang; });
    }
    if (!lang) {
        lang = read(function () { return env.dataCulture; }) || read(function () { return env.navigatorLanguage; });
    }
    return String(lang).replace('_', '-').toLowerCase();
}

/**
 * The language tag to ask the strings route for, or 'en'. The tag goes into a URL, so only a tag of the shape the
 * route accepts is passed on; the server cuts pt-br back to pt, then English.
 */
export function languageToAsk(lang) {
    const tag = String(lang || '').trim().toLowerCase();
    return tag.length <= 35 && /^[a-z]{2,3}(-[a-z0-9]{2,8})*$/.test(tag) ? tag : 'en';
}

/**
 * The popup's window features: 520 x 720, smaller on a small screen, centred on the Jellyfin window. window.open
 * needs popup=yes to open a window and not a tab.
 */
export function popupFeatures(win) {
    const screenObj = win.screen || {};
    const availW = screenObj.availWidth || win.innerWidth || 1024;
    const availH = screenObj.availHeight || win.innerHeight || 768;
    const width = Math.max(320, Math.min(520, availW - 40));
    const height = Math.max(400, Math.min(720, availH - 40));
    const outerW = win.outerWidth || availW;
    const outerH = win.outerHeight || availH;
    const left = Math.max(0, Math.round((win.screenX || 0) + (outerW - width) / 2));
    const top = Math.max(0, Math.round((win.screenY || 0) + (outerH - height) / 2));
    return 'popup=yes,width=' + width + ',height=' + height + ',left=' + left + ',top=' + top;
}

/**
 * Opens Dynu in the popup. It has to run inside the click that asked for it, or the browser blocks it. noopener
 * isn't used, because then window.open returns null and a blocked popup looks the same as an allowed one; the
 * opener is cleared on the new window instead, so Dynu's page can't move this one. Returns null when blocked.
 */
export function openPopup(win, url) {
    let popup = null;
    try {
        popup = win.open(url, POPUP_NAME, popupFeatures(win));
    } catch (e) {
        popup = null;
    }
    if (!popup) {
        return null;
    }
    try {
        popup.opener = null;
    } catch (e) {
        // A window that won't take it is still ours to watch.
    }
    return popup;
}

/**
 * Which step shows, and what sits above it. state is the answer of GET /Trailhead/setup. ui is what the admin
 * pressed: Change key or Change name.
 */
export function viewFor(state, ui) {
    const s = state || {};
    const flags = ui || {};
    const keySaved = s.DynuKey === 'Saved';
    const hostname = s.Hostname || '';
    let step = 'done';
    if (!keySaved || flags.changingKey) {
        step = 'dynu';
    } else if (!hostname || flags.changingName) {
        step = 'name';
    }
    return {
        step: step,
        keyLine: keySaved && step !== 'dynu',
        nameLine: step === 'done' ? hostname : '',
        reenter: s.DynuKey === 'Reenter',
        renameNote: step === 'name' && !!hostname,
        canCancelKey: step === 'dynu' && keySaved,
        canCancelName: step === 'name' && !!hostname
    };
}

/** The status line: a string key and its arguments. when is already formatted for the reader. */
export function statusFor(state, formatTime) {
    const s = state || {};
    if (!s.Hostname) {
        return { key: 'status.notSetUp', args: {}, tone: 'warn' };
    }
    const r = s.Renewal || {};
    const when = r.At ? (formatTime ? formatTime(r.At) : String(r.At)) : '';
    if (r.State === 'BuildFailed') { return { key: 'status.buildFailed', args: {}, tone: 'bad' }; }
    if (r.State === 'Ran') {
        if (r.Outcome === 'Issued') { return { key: 'status.issued', args: { when: when }, tone: 'good' }; }
        if (r.Outcome === 'UpToDate') { return { key: 'status.upToDate', args: { when: when }, tone: 'good' }; }
        if (r.Outcome === 'Blocked') { return { key: 'status.blocked', args: {}, tone: 'warn' }; }
        if (r.Outcome === 'Failed') { return { key: 'status.failed', args: { when: when }, tone: 'bad' }; }
    }
    return { key: 'status.nameReady', args: { name: s.Hostname }, tone: 'good' };
}

/** The parent in a full name: alex.freeddns.org gives freeddns.org. */
export function parentOf(fullName) {
    const s = String(fullName || '');
    const dot = s.indexOf('.');
    return dot > 0 ? s.slice(dot + 1) : '';
}

// ---------------------------------------------------------------- the controller

/**
 * env: { win, doc, apiClient, storage, formatTime }. Everything the page touches comes through it, so a test can
 * stand in for the browser. Returns an object with load() and the table, for the tests.
 */
export function createController(view, env) {
    const win = env.win;
    const doc = env.doc;
    const apiClient = env.apiClient;
    const formatTime = env.formatTime || function (iso) { return new Date(iso).toLocaleString(); };

    const ui = {
        changingKey: false,
        changingName: false,
        popup: null,
        popupTimer: null,
        popupWasOpen: false,
        autoRetry: false,
        busy: false,
        checkSeq: 0,
        checkTimer: null,
        lastStep: ''
    };
    let state = null;
    let table = {};
    let language = 'en';
    let shown = false;

    const $ = function (id) { return view.querySelector('#' + id); };

    // ------------------------------------------------------------ text

    function has(key) { return Object.prototype.hasOwnProperty.call(table, key); }

    function t(key, args) {
        if (has(key)) { return format(table[key], args); }
        if (Object.prototype.hasOwnProperty.call(FALLBACK, key)) { return format(FALLBACK[key], args); }
        return FALLBACK['page.generic'];
    }

    function setText(id, text) {
        const el = $(id);
        el.textContent = text;
        return el;
    }

    function show(id, visible) {
        $(id).hidden = !visible;
    }

    // The fixed text is already English in the markup. This only swaps in the loaded language, and leaves an element
    // alone when the table has no sentence for it.
    function applyStatic() {
        const nodes = view.querySelectorAll('[data-i18n]');
        for (let i = 0; i < nodes.length; i++) {
            const key = nodes[i].getAttribute('data-i18n');
            if (has(key)) {
                nodes[i].textContent = table[key];
            }
        }
    }

    // ------------------------------------------------------------ calls

    function call(method, path, body) {
        const req = { type: method, url: apiClient.getUrl('Trailhead/' + path), dataType: 'json', headers: { 'Accept-Language': language } };
        if (body !== undefined) {
            req.data = JSON.stringify(body);
            req.contentType = 'application/json';
        }
        return apiClient.ajax(req).catch(function (err) { return Promise.reject(toApiError(err)); });
    }

    function fail(err) {
        const key = errorKey(err && err.code, err && err.status);
        const el = setText('th-error', t(key));
        el.hidden = false;
    }

    function clearMessages() {
        show('th-error', false);
        show('th-notice', false);
    }

    function notice(text) {
        const el = setText('th-notice', text);
        el.hidden = !text;
    }

    // ------------------------------------------------------------ drawing

    function fillParents() {
        const select = $('th-parent');
        const parents = (state && state.Parents) || [];
        const wanted = parents.map(function (p) { return p.Name; }).join('|');
        if (select.getAttribute('data-parents') === wanted) {
            return;
        }
        select.textContent = '';
        let chosen = '';
        parents.forEach(function (p) {
            const option = doc.createElement('option');
            option.value = p.Name;
            option.textContent = p.Name;
            select.appendChild(option);
            if (p.IsDefault && !chosen) { chosen = p.Name; }
        });
        select.setAttribute('data-parents', wanted);
        select.value = chosen || (parents[0] && parents[0].Name) || '';
    }

    function render() {
        if (!state) { return; }
        const vm = viewFor(state, ui);
        show('th-loading', false);

        const status = statusFor(state, formatTime);
        const statusEl = setText('th-status', t(status.key, status.args));
        statusEl.className = 'th-status th-' + status.tone;
        statusEl.hidden = false;

        show('th-done-key', vm.keyLine);
        show('th-done-name', !!vm.nameLine);
        if (vm.nameLine) { setText('th-done-name-text', t('name.current', { name: vm.nameLine })); }

        show('th-step-dynu', vm.step === 'dynu');
        show('th-reenter', vm.reenter);
        show('th-key-cancel', vm.canCancelKey);
        show('th-step-name', vm.step === 'name');
        show('th-rename-note', vm.renameNote);
        show('th-name-cancel', vm.canCancelName);
        if (vm.step === 'name') { fillParents(); }
        updateWait();

        if (vm.step !== ui.lastStep) {
            ui.lastStep = vm.step;
            if (vm.step !== 'done') {
                const heading = $(vm.step === 'dynu' ? 'th-heading-dynu' : 'th-heading-name');
                if (heading && heading.focus) { heading.focus(); }
            }
        }
    }

    function updateWait() {
        const wait = $('th-dynu-wait');
        if (ui.popup) {
            wait.textContent = t('dynuKey.waiting');
            wait.hidden = false;
        } else if (ui.popupWasOpen) {
            wait.textContent = t('dynuKey.closed');
            wait.hidden = false;
        } else {
            wait.hidden = true;
        }
    }

    function clearResult() {
        setText('th-result-text', '');
        show('th-alts', false);
        $('th-alt-buttons').textContent = '';
        show('th-full', false);
        show('th-panel', false);
        show('th-panel-blocked', false);
    }

    // ------------------------------------------------------------ the popup

    function stopWatching() {
        if (ui.popupTimer !== null) {
            win.clearInterval(ui.popupTimer);
            ui.popupTimer = null;
        }
    }

    function popupClosed() {
        stopWatching();
        ui.popup = null;
        ui.popupWasOpen = true;
        updateWait();
        retryAfterPopup();
    }

    // The add-name page was opened to make the name by hand. When the admin comes back, the claim is asked once more:
    // it is safe to repeat, finds the name the panel made and answers Ours.
    function retryAfterPopup() {
        if (ui.autoRetry && !ui.busy) {
            ui.autoRetry = false;
            claim();
        }
    }

    // Has to run straight from the click: the browser allows the popup only then.
    function openDynu(url, blockedId, linkId) {
        const popup = openPopup(win, url);
        if (!popup) {
            const link = $(linkId);
            link.setAttribute('href', url);
            show(blockedId, true);
            return;
        }
        show(blockedId, false);
        stopWatching();
        ui.popup = popup;
        ui.popupWasOpen = true;
        ui.popupTimer = win.setInterval(function () {
            if (!ui.popup || ui.popup.closed) { popupClosed(); }
        }, POPUP_POLL_MS);
        updateWait();
    }

    function onWindowFocus() {
        if (!shown) { return; }
        if (state && viewFor(state, ui).step === 'dynu') {
            const key = $('th-key');
            if (key && key.focus) { key.focus(); }
        }
        retryAfterPopup();
    }

    // ------------------------------------------------------------ the key

    function saveKey() {
        if (ui.busy) { return Promise.resolve(); }
        const field = $('th-key');
        const key = String(field.value || '').trim();
        clearMessages();
        if (!key) {
            fail({ status: 400, code: 'dynu_key_invalid' });
            return Promise.resolve();
        }
        ui.busy = true;
        $('th-key-save').disabled = true;
        return call('POST', 'dynu-key', { Key: key }).then(function (res) {
            field.value = '';
            state = res.Setup;
            ui.changingKey = false;
            ui.changingName = false;
            ui.popupWasOpen = false;
            stopWatching();
            if (ui.popup) { ui.popup = null; }
            render();
            if (res.NameCleared) { notice(t('dynuKey.nameCleared')); }
        }, function (err) {
            // A key Dynu or Trailhead refused is no use to keep. One that never got an answer stays, so Save works again.
            if (!isRetryable(err)) { field.value = ''; }
            fail(err);
        }).then(function () {
            ui.busy = false;
            $('th-key-save').disabled = false;
        });
    }

    // ------------------------------------------------------------ the name

    function nameBody() {
        return { Label: String($('th-label').value || '').trim(), Parent: String($('th-parent').value || '') };
    }

    function scheduleCheck() {
        if (ui.checkTimer !== null) { win.clearTimeout(ui.checkTimer); }
        ui.checkSeq++;
        clearResult();
        ui.checkTimer = win.setTimeout(function () {
            ui.checkTimer = null;
            checkName();
        }, CHECK_DELAY_MS);
    }

    function checkName() {
        const body = nameBody();
        const seq = ++ui.checkSeq;
        if (!body.Label) {
            setText('th-check', '');
            return Promise.resolve();
        }
        return call('POST', 'name/check', body).then(function (res) {
            if (seq !== ui.checkSeq) { return; }
            setText('th-check', res.Kind === 'Invalid' && res.Message ? res.Message : t(res.Kind === 'Invalid' && res.Problem ? problemKey(res.Problem) : kindKey(res.Kind)));
        }, function (err) {
            if (seq !== ui.checkSeq) { return; }
            setText('th-check', t(errorKey(err.code, err.status)));
        });
    }

    function pickAlternative(fullName) {
        const parent = parentOf(fullName);
        const select = $('th-parent');
        select.value = parent;
        clearResult();
        scheduleCheck();
        const cont = $('th-continue');
        if (cont && cont.focus) { cont.focus(); }
    }

    function showTaken(res) {
        setText('th-result-text', t(kindKey('Taken')));
        const holder = $('th-alt-buttons');
        holder.textContent = '';
        (res.Alternatives || []).slice(0, MAX_ALTERNATIVES).forEach(function (full) {
            const b = doc.createElement('button');
            b.setAttribute('type', 'button');
            b.setAttribute('is', 'emby-button');
            b.className = 'raised emby-button';
            b.textContent = full;
            b.addEventListener('click', function () { pickAlternative(full); });
            holder.appendChild(b);
        });
        show('th-alts', (res.Alternatives || []).length > 0);
    }

    function claim() {
        if (ui.busy) { return Promise.resolve(); }
        const body = nameBody();
        clearMessages();
        if (!body.Label) {
            setText('th-check', t(problemKey('Empty')));
            return Promise.resolve();
        }
        if (ui.checkTimer !== null) { win.clearTimeout(ui.checkTimer); ui.checkTimer = null; }
        ui.checkSeq++;
        clearResult();
        setText('th-check', '');
        ui.busy = true;
        $('th-continue').disabled = true;
        return call('POST', 'name/claim', body).then(function (res) {
            switch (res.Kind) {
                case 'Claimed':
                case 'Ours':
                    state = res.Setup || state;
                    ui.changingName = false;
                    ui.autoRetry = false;
                    render();
                    notice(t(kindKey(res.Kind)));
                    break;
                case 'Taken':
                    showTaken(res);
                    break;
                case 'AccountFull':
                    setText('th-result-text', t(kindKey('AccountFull')));
                    show('th-panel', true);
                    break;
                case 'DynuRefused':
                    setText('th-result-text', t(kindKey('DynuRefused')));
                    $('th-full-name').value = res.Name || '';
                    show('th-full', true);
                    break;
                case 'Invalid':
                    setText('th-result-text', res.Message || t(res.Problem ? problemKey(res.Problem) : kindKey('Invalid')));
                    break;
                default:
                    setText('th-result-text', t('page.generic'));
            }
        }, function (err) {
            if (isKeyProblem(err)) {
                state = Object.assign({}, state, { DynuKey: 'Reenter' });
                ui.changingKey = false;
                render();
            } else {
                fail(err);
            }
        }).then(function () {
            ui.busy = false;
            $('th-continue').disabled = false;
        });
    }

    function copyName() {
        const field = $('th-full-name');
        const done = function () { setText('th-copy', t('name.copied')); };
        const selectIt = function () {
            if (field.select) { field.select(); }
        };
        try {
            if (win.navigator && win.navigator.clipboard && win.navigator.clipboard.writeText) {
                return win.navigator.clipboard.writeText(field.value).then(done, selectIt);
            }
        } catch (e) {
            // Fall through to selecting the name.
        }
        selectIt();
        return Promise.resolve();
    }

    // ------------------------------------------------------------ wiring

    function on(id, name, fn) {
        $(id).addEventListener(name, fn);
    }

    on('th-dynu-create', 'click', function () { openDynu(DYNU.signUp, 'th-dynu-blocked', 'th-dynu-link'); });
    on('th-dynu-have', 'click', function () { openDynu(DYNU.apiCredentials, 'th-dynu-blocked', 'th-dynu-link'); });
    on('th-add-open', 'click', function () {
        ui.autoRetry = true;
        openDynu(DYNU.addName, 'th-panel-blocked', 'th-panel-link');
    });
    on('th-panel-open', 'click', function () { openDynu(DYNU.names, 'th-panel-blocked', 'th-panel-link'); });
    on('th-recheck', 'click', function () { ui.autoRetry = false; claim(); });
    on('th-copy', 'click', copyName);

    on('th-key-form', 'submit', function (e) {
        if (e && e.preventDefault) { e.preventDefault(); }
        saveKey();
    });
    // Pasting saves straight away. The text lands after the event, so the save waits one tick. Typing doesn't save.
    on('th-key', 'paste', function () { win.setTimeout(saveKey, 0); });
    on('th-name-form', 'submit', function (e) {
        if (e && e.preventDefault) { e.preventDefault(); }
        claim();
    });
    on('th-label', 'input', scheduleCheck);
    on('th-parent', 'change', scheduleCheck);

    on('th-change-key', 'click', function () { ui.changingKey = true; clearMessages(); render(); });
    on('th-change-name', 'click', function () { ui.changingName = true; clearMessages(); clearResult(); render(); });
    on('th-key-cancel', 'click', function () { ui.changingKey = false; clearMessages(); render(); });
    on('th-name-cancel', 'click', function () { ui.changingName = false; clearMessages(); render(); });

    // ------------------------------------------------------------ loading

    function loadStrings() {
        const asked = languageToAsk(adminLanguage(env.language || {}));
        language = asked;
        return apiClient.ajax({ type: 'GET', url: apiClient.getUrl('Trailhead/strings/' + asked), dataType: 'json' }).then(function (res) {
            if (res && typeof res === 'object') {
                table = res;
                applyStatic();
            }
        }, function () {
            // The markup is already English, and t() has its fallback.
        });
    }

    function load() {
        clearMessages();
        return loadStrings().then(function () {
            return call('GET', 'setup');
        }).then(function (res) {
            state = res;
            render();
        }, function (err) {
            show('th-loading', true);
            setText('th-loading', t('page.loadFailed'));
            if (err && err.status) { fail(err); }
        });
    }

    view.addEventListener('viewshow', function () {
        shown = true;
        win.addEventListener('focus', onWindowFocus);
        load();
    });
    view.addEventListener('viewhide', function () {
        shown = false;
        win.removeEventListener('focus', onWindowFocus);
        stopWatching();
        if (ui.checkTimer !== null) { win.clearTimeout(ui.checkTimer); ui.checkTimer = null; }
    });

    return {
        load: load,
        t: t,
        get table() { return table; },
        get state() { return state; },
        get ui() { return ui; },
        onWindowFocus: onWindowFocus
    };
}

// ---------------------------------------------------------------- the entry point

function browserEnv() {
    const root = document.documentElement;
    const apiClient = window.ApiClient;
    let userId = '';
    try { userId = apiClient.getCurrentUserId() || ''; } catch (e) { userId = ''; }
    let storage = null;
    try { storage = window.localStorage; } catch (e) { storage = null; }
    return {
        win: window,
        doc: document,
        apiClient: apiClient,
        language: {
            userId: userId,
            storage: storage,
            htmlLang: root.getAttribute('lang') || '',
            dataCulture: root.getAttribute('data-culture') || '',
            navigatorLanguage: window.navigator.language || ''
        }
    };
}

export default function TrailheadPage(view) {
    return createController(view, browserEnv());
}
