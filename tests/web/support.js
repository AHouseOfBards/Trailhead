// Shared pieces for the page tests: the page module loaded from its file, a small fake browser built from the real
// HTML, and a fake ApiClient. No packages. The HTML is the real Web/trailhead.html, so an id the script looks up that
// the markup lacks fails here.
'use strict';

const fs = require('node:fs');
const path = require('node:path');

const WEB = path.join(__dirname, '..', '..', 'Jellyfin.Plugin.Trailhead', 'Web');
const HTML_FILE = path.join(WEB, 'trailhead.html');
const JS_FILE = path.join(WEB, 'trailhead.js');
const EN_FILE = path.join(WEB, 'i18n', 'en.json');

const SENTINEL = 'SENTINEL-dynu-key-0123456789abcdef';

function readText(file) {
    return fs.readFileSync(file, 'utf8');
}

// The module's own text, imported through a data: URL as the plan says: no file path, no loader.
async function loadModule() {
    const text = readText(JS_FILE);
    return import('data:text/javascript;base64,' + Buffer.from(text, 'utf8').toString('base64'));
}

function loadEnglish() {
    return JSON.parse(readText(EN_FILE));
}

// ---------------------------------------------------------------- fake DOM

class FakeElement {
    constructor(tag, attrs) {
        this.tag = tag;
        this.attrs = Object.assign({}, attrs || {});
        this.hidden = Object.prototype.hasOwnProperty.call(this.attrs, 'hidden');
        this.children = [];
        this.listeners = {};
        this.value = '';
        this.disabled = false;
        this.className = this.attrs.class || '';
        this._text = '';
        this.selected = false;
        this.focused = 0;
    }

    get textContent() { return this._text; }

    set textContent(v) {
        this._text = String(v);
        this.children = [];
    }

    setAttribute(k, v) { this.attrs[k] = String(v); }

    getAttribute(k) { return Object.prototype.hasOwnProperty.call(this.attrs, k) ? this.attrs[k] : null; }

    appendChild(c) { this.children.push(c); return c; }

    addEventListener(name, fn) { (this.listeners[name] = this.listeners[name] || []).push(fn); }

    removeEventListener(name, fn) { this.listeners[name] = (this.listeners[name] || []).filter((f) => f !== fn); }

    focus() { this.focused++; this.ownerFocus = true; }

    select() { this.selected = true; }

    dispatch(name, event) {
        const e = event || {};
        e.type = name;
        e.defaultPrevented = false;
        e.preventDefault = function () { this.defaultPrevented = true; };
        (this.listeners[name] || []).slice().forEach((fn) => fn(e));
        return e;
    }

    // Everything that could show text or hold a value, for "the key is nowhere" checks.
    everything() {
        const own = [this._text, String(this.value), JSON.stringify(this.attrs), this.className];
        return own.concat(...this.children.map((c) => c.everything()));
    }
}

// One element per tag that has an id or a data-i18n, with the English text the markup holds. The structure is flat on
// purpose: the script only looks things up by id and by [data-i18n].
function buildView() {
    const html = readText(HTML_FILE);
    const byId = {};
    const i18n = [];
    const all = [];
    const tag = /<(\w+)([^>]*)>([^<]*)/g;
    let m;
    while ((m = tag.exec(html)) !== null) {
        const attrs = {};
        const attrRe = /([A-Za-z_:][\w:.-]*)(?:="([^"]*)")?/g;
        let a;
        while ((a = attrRe.exec(m[2])) !== null) {
            attrs[a[1]] = a[2] === undefined ? '' : a[2];
        }
        if (attrs.id === undefined && attrs['data-i18n'] === undefined) { continue; }
        const el = new FakeElement(m[1], attrs);
        if (attrs['data-i18n'] !== undefined) {
            el._text = m[3].replace(/\s+/g, ' ').trim();
            i18n.push(el);
        }
        if (attrs.id !== undefined) { byId[attrs.id] = el; }
        all.push(el);
    }
    const viewListeners = {};
    const view = {
        querySelector(sel) {
            if (sel.charAt(0) !== '#') { throw new Error('the fake only does #id: ' + sel); }
            return byId[sel.slice(1)] || null;
        },
        querySelectorAll(sel) {
            if (sel !== '[data-i18n]') { throw new Error('the fake only does [data-i18n]: ' + sel); }
            return i18n.slice();
        },
        addEventListener(name, fn) { (viewListeners[name] = viewListeners[name] || []).push(fn); },
        dispatch(name) { (viewListeners[name] || []).slice().forEach((fn) => fn({ type: name })); }
    };
    return { view, byId, all, i18n, html };
}

// ---------------------------------------------------------------- fake window, with a clock

function fakeWindow(options) {
    const o = options || {};
    const calls = { open: [], intervals: [], timeouts: [] };
    let now = 0;
    let nextId = 1;
    const timers = new Map();
    const listeners = {};
    const popups = [];
    const win = {
        calls,
        popups,
        screen: o.screen || { availWidth: 1920, availHeight: 1080 },
        outerWidth: o.outerWidth || 1400,
        outerHeight: o.outerHeight || 900,
        screenX: o.screenX === undefined ? 100 : o.screenX,
        screenY: o.screenY === undefined ? 50 : o.screenY,
        navigator: o.navigator || { language: 'en-US' },
        blockPopups: !!o.blockPopups,
        open(url, name, features) {
            calls.open.push({ url, name, features, duringClick: win.inClick });
            if (win.blockPopups) { return null; }
            const popup = { url, closed: false, opener: 'the-jellyfin-window' };
            popups.push(popup);
            return popup;
        },
        setTimeout(fn, ms) {
            const id = nextId++;
            timers.set(id, { fn, at: now + (ms || 0), every: 0 });
            calls.timeouts.push(ms);
            return id;
        },
        clearTimeout(id) { timers.delete(id); },
        setInterval(fn, ms) {
            const id = nextId++;
            timers.set(id, { fn, at: now + ms, every: ms });
            calls.intervals.push(ms);
            return id;
        },
        clearInterval(id) { timers.delete(id); },
        addEventListener(name, fn) { (listeners[name] = listeners[name] || []).push(fn); },
        removeEventListener(name, fn) { listeners[name] = (listeners[name] || []).filter((f) => f !== fn); },
        fire(name) { (listeners[name] || []).slice().forEach((fn) => fn({ type: name })); },
        listenerCount(name) { return (listeners[name] || []).length; },
        activeTimers() { return timers.size; },
        // Moves the clock on, running what's due. Promise work between timers needs a flush by the caller.
        async tick(ms) {
            const end = now + ms;
            for (;;) {
                let next = null;
                timers.forEach((t, id) => { if (t.at <= end && (!next || t.at < next.t.at)) { next = { id, t }; } });
                if (!next) { break; }
                now = next.t.at;
                if (next.t.every) { next.t.at += next.t.every; } else { timers.delete(next.id); }
                next.t.fn();
                await flush();
            }
            now = end;
        }
    };
    return win;
}

async function flush() {
    for (let i = 0; i < 12; i++) { await Promise.resolve(); }
    await new Promise((r) => setImmediate(r));
}

// ---------------------------------------------------------------- fake ApiClient

// routes: { 'GET setup': (req, body) => ({ status: 200, body: {...} }) | { status: 409, code: 'x' } | { reject: err } }
function fakeApiClient(routes) {
    const requests = [];
    return {
        requests,
        getUrl(p) { return '/' + p; },
        getCurrentUserId() { return 'user1'; },
        ajax(req) {
            const body = req.data === undefined ? undefined : JSON.parse(req.data);
            requests.push({ method: req.type, url: req.url, body, headers: req.headers || {}, raw: req });
            const route = req.type + ' ' + req.url.replace(/^\/Trailhead\//, '').replace(/^strings\/.*/, 'strings');
            const handler = routes[route];
            if (!handler) { return Promise.reject({ status: 404, headers: { get() { return null; } } }); }
            const answer = typeof handler === 'function' ? handler(req, body) : handler;
            if (answer && answer.reject) { return Promise.reject(answer.reject); }
            if (answer && answer.status >= 400) {
                return Promise.reject({
                    status: answer.status,
                    headers: { get: (h) => (h.toLowerCase() === 'x-campground-error' ? (answer.code || null) : null) }
                });
            }
            return Promise.resolve(answer.body);
        }
    };
}

function setupState(extra) {
    return Object.assign({
        DynuKey: 'None',
        Hostname: '',
        Parents: [{ Name: 'freeddns.org', IsDefault: true }, { Name: 'mywire.org', IsDefault: false }, { Name: 'webredirect.org', IsDefault: false }],
        Renewal: { State: 'NotRunYet', Outcome: null, At: null }
    }, extra || {});
}

// A running page: the real module, the real HTML, a fake window and a fake API.
async function startPage(routes, options) {
    const o = options || {};
    const mod = await loadModule();
    const dom = buildView();
    const win = fakeWindow(o.window);
    const api = fakeApiClient(routes);
    const consoleCalls = [];
    const real = {};
    ['log', 'info', 'warn', 'error', 'debug', 'trace'].forEach((k) => {
        real[k] = console[k];
        console[k] = function () { consoleCalls.push([k].concat(Array.from(arguments).map(String))); };
    });
    const doc = { createElement: (tag) => new FakeElement(tag) };
    const controller = mod.createController(dom.view, {
        win, doc, apiClient: api,
        language: o.language || { userId: 'user1', storage: { getItem: () => '' }, htmlLang: 'en-US', dataCulture: '', navigatorLanguage: 'en-US' },
        formatTime: (iso) => 'at ' + iso
    });
    const page = {
        mod, dom, win, api, controller, consoleCalls,
        el: (id) => dom.byId[id],
        async show() { dom.view.dispatch('viewshow'); await flush(); },
        // What the admin does: a click goes through win.inClick so a test can see window.open ran inside it.
        async click(id) {
            win.inClick = true;
            try { dom.byId[id].dispatch('click'); } finally { win.inClick = false; }
            await flush();
        },
        async submit(id) { dom.byId[id].dispatch('submit'); await flush(); },
        async type(id, text) { dom.byId[id].value = text; dom.byId[id].dispatch('input'); await flush(); },
        restoreConsole() { Object.keys(real).forEach((k) => { console[k] = real[k]; }); }
    };
    return page;
}

// The answer of a route for a given case, to keep the tests short.
const ok = (body) => ({ status: 200, body });
const fail = (status, code) => ({ status, code });

module.exports = {
    SENTINEL, WEB, HTML_FILE, JS_FILE, EN_FILE, readText, loadModule, loadEnglish, buildView, fakeWindow, fakeApiClient,
    setupState, startPage, flush, ok, fail, FakeElement
};
