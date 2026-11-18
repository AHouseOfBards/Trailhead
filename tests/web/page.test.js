// node --test tests/web/*.test.js: the markup, the strings and the script have to agree. Nothing here runs a browser.
'use strict';

const test = require('node:test');
const assert = require('node:assert');
const path = require('node:path');
const s = require('./support.js');

const ROOT = path.join(__dirname, '..', '..', 'Jellyfin.Plugin.Trailhead');

function enumMembers(file, name) {
    const text = s.readText(path.join(ROOT, file));
    const m = new RegExp('enum\\s+' + name + '\\s*\\{([^}]*)\\}').exec(text);
    assert.ok(m, 'enum ' + name + ' not found in ' + file);
    const members = m[1].replace(/\/\/.*$/gm, '').replace(/\/\*[\s\S]*?\*\//g, '').replace(/<[^>]*>/g, '')
        .split(',').map((x) => x.trim().replace(/\s*=.*$/, '')).filter((x) => /^[A-Za-z]\w*$/.test(x));
    return members;
}

function apiCodes() {
    const text = s.readText(path.join(ROOT, 'Api', 'ErrorReply.cs'));
    const codes = [];
    const re = /internal const string \w+ = "([a-z_]+)";/g;
    let m;
    while ((m = re.exec(text)) !== null) { codes.push(m[1]); }
    return codes;
}

function i18nInMarkup() {
    const html = s.readText(s.HTML_FILE);
    const found = [];
    const re = /<\w+[^>]*\bdata-i18n="([^"]+)"[^>]*>([^<]*)/g;
    let m;
    while ((m = re.exec(html)) !== null) {
        found.push({ key: m[1], text: m[2].replace(/\s+/g, ' ').trim() });
    }
    return found;
}

test('en.json is not empty, and its values are all text', () => {
    const en = s.loadEnglish();
    assert.ok(Object.keys(en).length > 50);
    Object.keys(en).forEach((k) => assert.ok(typeof en[k] === 'string' && en[k].length > 0, k));
});

test('the markup has data-i18n keys, and each is in en.json with the same English', () => {
    const en = s.loadEnglish();
    const found = i18nInMarkup();
    assert.ok(found.length >= 20, 'only ' + found.length + ' data-i18n elements');
    found.forEach((f) => {
        assert.ok(Object.prototype.hasOwnProperty.call(en, f.key), 'en.json has no ' + f.key);
        assert.strictEqual(f.text, en[f.key], f.key);
    });
});

test('every id the script looks up is in the markup, and no id is there twice', () => {
    return s.loadModule().then((mod) => {
        assert.ok(mod.IDS.length > 20);
        const html = s.readText(s.HTML_FILE);
        const ids = [];
        const re = /\bid="([^"]+)"/g;
        let m;
        while ((m = re.exec(html)) !== null) { ids.push(m[1]); }
        assert.strictEqual(new Set(ids).size, ids.length, 'a duplicate id');
        mod.IDS.forEach((id) => assert.ok(ids.indexOf(id) !== -1, 'the markup has no #' + id));
    });
});

test('every string key the script writes is in en.json', () => {
    const en = s.loadEnglish();
    const src = s.readText(s.JS_FILE);
    const used = new Set();
    const re = /'((?:page|status|dynuKey|name|label|error)\.[A-Za-z_]+)'/g;
    let m;
    while ((m = re.exec(src)) !== null) { used.add(m[1]); }
    assert.ok(used.size > 15, 'found only ' + used.size + ' keys in the script');
    used.forEach((k) => assert.ok(Object.prototype.hasOwnProperty.call(en, k), 'en.json has no ' + k));
});

test('every ClaimKind, LabelProblem and error code in the C# maps to a key that is in en.json', async () => {
    const en = s.loadEnglish();
    const mod = await s.loadModule();
    const kinds = enumMembers('Dns/NameClaimer.cs', 'ClaimKind');
    const problems = enumMembers('Dns/HostLabel.cs', 'LabelProblem').filter((p) => p !== 'None');
    const codes = apiCodes();
    assert.ok(kinds.length >= 6 && problems.length >= 6 && codes.length >= 8, 'the enums were not read');
    kinds.forEach((k) => assert.ok(en[mod.kindKey(k)], 'no text for kind ' + k + ' (' + mod.kindKey(k) + ')'));
    problems.forEach((p) => assert.ok(en[mod.problemKey(p)], 'no text for problem ' + p));
    codes.forEach((c) => {
        assert.strictEqual(mod.errorKey(c, 400), 'error.' + c, 'the script does not know the code ' + c);
        assert.ok(en[mod.errorKey(c, 400)], 'no text for code ' + c);
    });
});

test('the fallback sentences are the same text as en.json', async () => {
    const en = s.loadEnglish();
    const mod = await s.loadModule();
    const keys = Object.keys(mod.FALLBACK);
    assert.ok(keys.length >= 1);
    keys.forEach((k) => assert.strictEqual(mod.FALLBACK[k], en[k], k));
});

test('the script writes text only through textContent, and never logs', () => {
    const src = s.readText(s.JS_FILE);
    assert.ok(src.length > 1000);
    ['innerHTML', 'outerHTML', 'insertAdjacentHTML', 'document.write', 'eval(', 'new Function'].forEach((bad) => {
        assert.ok(src.indexOf(bad) === -1, 'the script uses ' + bad);
    });
    assert.ok(!/\bconsole\./.test(src), 'the script writes to the console');
    assert.ok(!/localStorage\.setItem|sessionStorage\.setItem|document\.cookie/.test(src), 'the script stores something');
    assert.ok(!/readText\(/.test(src), 'the script reads the clipboard');
});

test('the key field is a password field that no browser saves, and every field has a label', () => {
    const html = s.readText(s.HTML_FILE);
    const key = /<input[^>]*id="th-key"[^>]*>/.exec(html);
    assert.ok(key, 'no #th-key');
    assert.ok(/type="password"/.test(key[0]));
    assert.ok(/autocomplete="off"/.test(key[0]));
    assert.ok(/spellcheck="false"/.test(key[0]));
    const fields = html.match(/<(?:input|select)[^>]*id="[^"]+"/g) || [];
    assert.ok(fields.length >= 4);
    fields.forEach((f) => {
        const id = /id="([^"]+)"/.exec(f)[1];
        assert.ok(new RegExp('<label[^>]*for="' + id + '"').test(html), 'no <label for="' + id + '">');
    });
});

test('links that open Dynu are plain anchors with noopener, results are live regions, headings take focus', () => {
    const html = s.readText(s.HTML_FILE);
    const anchors = html.match(/<a [^>]*>/g) || [];
    assert.ok(anchors.length >= 2);
    anchors.forEach((a) => {
        assert.ok(/target="_blank"/.test(a) && /rel="noopener noreferrer"/.test(a), a);
    });
    assert.ok(/role="alert"/.test(html) && /role="status"/.test(html));
    assert.ok(/<h3 id="th-heading-dynu"[^>]*tabindex="-1"/.test(html));
    assert.ok(/<h3 id="th-heading-name"[^>]*tabindex="-1"/.test(html));
    assert.ok(!/<script/i.test(html), 'the markup has an inline script');
    assert.ok(!/\son\w+="/i.test(html), 'the markup has an inline handler');
    assert.ok(/data-controller="__plugin\/trailhead\.js"/.test(html));
});

// ---- colour: each of the page's own colours is at least 4.5:1 on the backgrounds of its themes.

function lum(hex) {
    const n = parseInt(hex.slice(1), 16);
    const c = [(n >> 16) & 255, (n >> 8) & 255, n & 255].map((v) => {
        const x = v / 255;
        return x <= 0.03928 ? x / 12.92 : Math.pow((x + 0.055) / 1.055, 2.4);
    });
    return 0.2126 * c[0] + 0.7152 * c[1] + 0.0722 * c[2];
}

function ratio(a, b) {
    const x = lum(a); const y = lum(b);
    return (Math.max(x, y) + 0.05) / (Math.min(x, y) + 0.05);
}

function vars(block) {
    const out = {};
    const re = /--(th-[a-z]+):\s*(#[0-9a-fA-F]{6})/g;
    let m;
    while ((m = re.exec(block)) !== null) { out[m[1]] = m[2]; }
    return out;
}

test('the good, bad and warn colours meet 4.5:1 on each theme background', () => {
    const html = s.readText(s.HTML_FILE);
    const dark = vars(/#trailheadPage\s*\{([^}]*)\}/.exec(html)[1]);
    const light = vars(/html\[data-theme="light"\][^{]*\{([^}]*)\}/.exec(html)[1]);
    assert.deepStrictEqual(Object.keys(dark).sort(), ['th-bad', 'th-good', 'th-warn']);
    assert.deepStrictEqual(Object.keys(light).sort(), ['th-bad', 'th-good', 'th-warn']);
    // jellyfin-web v12.0 src/themes: dark #101010 (paper #202020), light #f2f2f2 (paper #e8e8e8), appletv #d5e9f2.
    const sets = [
        [dark, ['#101010', '#202020']],
        [light, ['#f2f2f2', '#e8e8e8', '#d5e9f2']]
    ];
    sets.forEach(([colours, backgrounds]) => {
        Object.keys(colours).forEach((name) => backgrounds.forEach((bg) => {
            assert.ok(ratio(colours[name], bg) >= 4.5, name + ' ' + colours[name] + ' on ' + bg + ' is ' + ratio(colours[name], bg).toFixed(2));
        }));
    });
    assert.ok(/html\[data-theme="appletv"\]/.test(html), 'appletv has no override');
});

test('a harness given nothing fails: no routes, no page, no ids', async () => {
    const dom = s.buildView();
    assert.ok(Object.keys(dom.byId).length > 20, 'the fake view found no ids in the markup');
    await assert.rejects(s.fakeApiClient({}).ajax({ type: 'GET', url: '/Trailhead/setup' }));
    const mod = await s.loadModule();
    const empty = { querySelector: () => null, querySelectorAll: () => [], addEventListener() {} };
    assert.throws(() => mod.createController(empty, { win: s.fakeWindow(), doc: {}, apiClient: null }));
});
