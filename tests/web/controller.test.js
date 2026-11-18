// node --test tests/web/*.test.js: the page running against the real markup, a fake window and a fake ApiClient.
'use strict';

const test = require('node:test');
const assert = require('node:assert');
const s = require('./support.js');

const en = s.loadEnglish();

function routes(extra) {
    return Object.assign({
        'GET strings': s.ok(en),
        'GET setup': s.ok(s.setupState())
    }, extra || {});
}

// A started and shown page. The console is put back when the test ends.
async function open(t, routeTable, options) {
    const page = await s.startPage(routeTable, options);
    t.after(() => page.restoreConsole());
    await page.show();
    return page;
}

const shown = (page, id) => !page.el(id).hidden;
const text = (page, id) => page.el(id).textContent;
const posts = (page, url) => page.api.requests.filter((r) => r.method === 'POST' && r.url === '/Trailhead/' + url);

test('a key step for a new install: the Dynu step shows, the name step does not, and the status says not set up', async (t) => {
    const page = await open(t, routes());
    assert.ok(shown(page, 'th-step-dynu'));
    assert.ok(!shown(page, 'th-step-name'));
    assert.ok(!shown(page, 'th-loading'));
    assert.ok(!shown(page, 'th-done-key'));
    assert.strictEqual(text(page, 'th-status'), en['status.notSetUp']);
    assert.ok(page.el('th-heading-dynu').focused > 0, 'focus moves to the step heading');
});

test('Reenter shows the step with the re-enter line', async (t) => {
    const page = await open(t, routes({ 'GET setup': s.ok(s.setupState({ DynuKey: 'Reenter' })) }));
    assert.ok(shown(page, 'th-step-dynu'));
    assert.ok(shown(page, 'th-reenter'));
    assert.strictEqual(text(page, 'th-reenter'), en['dynuKey.reenter']);
});

test('a saved key and no name: the name step with the finished key as one line above, and the default ending chosen', async (t) => {
    const page = await open(t, routes({ 'GET setup': s.ok(s.setupState({ DynuKey: 'Saved' })) }));
    assert.ok(shown(page, 'th-step-name'));
    assert.ok(!shown(page, 'th-step-dynu'));
    assert.ok(shown(page, 'th-done-key'));
    assert.ok(!shown(page, 'th-rename-note'));
    assert.deepStrictEqual(page.el('th-parent').children.map((o) => o.value), ['freeddns.org', 'mywire.org', 'webredirect.org']);
    assert.strictEqual(page.el('th-parent').value, 'freeddns.org');
});

test('a saved name: one finished line each, the status line, and Change name with the note about the old name', async (t) => {
    const page = await open(t, routes({
        'GET setup': s.ok(s.setupState({ DynuKey: 'Saved', Hostname: 'alex.freeddns.org', Renewal: { State: 'Ran', Outcome: 'UpToDate', At: '2026-11-16T10:00:00Z' } }))
    }));
    assert.ok(!shown(page, 'th-step-dynu') && !shown(page, 'th-step-name'));
    assert.ok(shown(page, 'th-done-key') && shown(page, 'th-done-name'));
    assert.strictEqual(text(page, 'th-done-name-text'), 'Name: alex.freeddns.org');
    assert.strictEqual(text(page, 'th-status'), 'Certificate is up to date. Checked at 2026-11-16T10:00:00Z.');

    await page.click('th-change-name');
    assert.ok(shown(page, 'th-step-name'));
    assert.ok(shown(page, 'th-rename-note'));
    assert.strictEqual(text(page, 'th-heading-name'), 'Name');
    assert.ok(shown(page, 'th-name-cancel'));

    await page.click('th-name-cancel');
    assert.ok(!shown(page, 'th-step-name'));
    assert.ok(shown(page, 'th-done-name'));
});

test('Change key goes back to the Dynu step and Cancel returns', async (t) => {
    const page = await open(t, routes({ 'GET setup': s.ok(s.setupState({ DynuKey: 'Saved', Hostname: 'alex.freeddns.org' })) }));
    await page.click('th-change-key');
    assert.ok(shown(page, 'th-step-dynu') && shown(page, 'th-key-cancel'));
    await page.click('th-key-cancel');
    assert.ok(!shown(page, 'th-step-dynu'));
});

// ---------------------------------------------------------------- strings

test('English is asked for too (the tag as the browser has it, the server cuts it to en): the script keeps no second English table', async (t) => {
    const page = await open(t, routes());
    assert.strictEqual(page.api.requests[0].url, '/Trailhead/strings/en-us');
    assert.strictEqual(page.api.requests[1].url, '/Trailhead/setup');
});

test('another language is one request for its tag, and the fixed text is swapped in', async (t) => {
    const german = Object.assign({}, en, { 'dynuKey.heading': 'Dynu-Konto', 'dynuKey.save': 'Speichern', 'status.notSetUp': 'Noch nicht eingerichtet.' });
    const page = await open(t, routes({ 'GET strings': s.ok(german) }), {
        language: { userId: 'user1', storage: { getItem: () => 'de-DE' }, htmlLang: 'en', dataCulture: '', navigatorLanguage: 'en' }
    });
    assert.strictEqual(page.api.requests[0].url, '/Trailhead/strings/de-de');
    assert.strictEqual(page.api.requests[0].raw.type, 'GET');
    assert.strictEqual(text(page, 'th-heading-dynu'), 'Dynu-Konto');
    assert.strictEqual(text(page, 'th-status'), 'Noch nicht eingerichtet.');
    // The language goes on every call, so the server's own words match.
    assert.strictEqual(page.api.requests[1].headers['Accept-Language'], 'de-de');
});

test('if the strings do not load, the markup stays English and the script has its fallback, never a raw key', async (t) => {
    const page = await open(t, routes({ 'GET strings': { status: 500, code: 'internal' } }));
    assert.strictEqual(text(page, 'th-heading-dynu'), 'Dynu account');
    assert.ok(shown(page, 'th-step-dynu'), 'the page still works');
    assert.strictEqual(text(page, 'th-status'), page.mod.FALLBACK['page.generic']);
    assert.ok(text(page, 'th-status').indexOf('status.') === -1);

    // An error shown with no table says the fallback sentence too.
    page.el('th-key').value = '';
    await page.submit('th-key-form');
    assert.strictEqual(text(page, 'th-error'), page.mod.FALLBACK['page.generic']);
});

test('if the setup call fails, the page says it could not load, and shows no step', async (t) => {
    const page = await open(t, routes({ 'GET setup': s.fail(500, 'internal') }));
    assert.ok(shown(page, 'th-loading'));
    assert.strictEqual(text(page, 'th-loading'), en['page.loadFailed']);
    assert.ok(!shown(page, 'th-step-dynu') && !shown(page, 'th-step-name'));
    assert.strictEqual(text(page, 'th-error'), en['error.internal']);
});

test('a harness given no routes at all fails the page, it does not pass it', async (t) => {
    const page = await open(t, {});
    assert.ok(shown(page, 'th-loading'));
    assert.strictEqual(text(page, 'th-loading'), page.mod.FALLBACK['page.generic']);
    assert.ok(!shown(page, 'th-status'));
});

// ---------------------------------------------------------------- the popup

test('Create a Dynu account opens sign-up in the popup from the click, and clears the opener', async (t) => {
    const page = await open(t, routes());
    await page.click('th-dynu-create');
    assert.strictEqual(page.win.calls.open.length, 1);
    const call = page.win.calls.open[0];
    assert.strictEqual(call.url, page.mod.DYNU.signUp);
    assert.strictEqual(call.name, 'trailhead-dynu');
    assert.ok(call.duringClick, 'window.open has to run inside the click, or the browser blocks it');
    assert.match(call.features, /popup=yes/);
    assert.strictEqual(page.win.popups[0].opener, null);
    assert.ok(!shown(page, 'th-dynu-blocked'));
    assert.strictEqual(text(page, 'th-dynu-wait'), en['dynuKey.waiting']);
});

test('I have an account opens API Credentials', async (t) => {
    const page = await open(t, routes());
    await page.click('th-dynu-have');
    assert.strictEqual(page.win.calls.open[0].url, page.mod.DYNU.apiCredentials);
});

test('a blocked popup shows a plain link to the same page, and nothing waits', async (t) => {
    const page = await open(t, routes(), { window: { blockPopups: true } });
    await page.click('th-dynu-have');
    assert.ok(shown(page, 'th-dynu-blocked'));
    assert.strictEqual(page.el('th-dynu-link').getAttribute('href'), page.mod.DYNU.apiCredentials);
    assert.strictEqual(page.el('th-dynu-link').getAttribute('target'), '_blank');
    assert.strictEqual(page.el('th-dynu-link').getAttribute('rel'), 'noopener noreferrer');
    assert.ok(!shown(page, 'th-dynu-wait'));
    assert.strictEqual(page.win.activeTimers(), 0);
});

test('when the popup closes the sentence changes, and the page stops watching', async (t) => {
    const page = await open(t, routes());
    await page.click('th-dynu-create');
    assert.strictEqual(page.win.activeTimers(), 1);
    await page.win.tick(3000);
    assert.strictEqual(text(page, 'th-dynu-wait'), en['dynuKey.waiting']);
    page.win.popups[0].closed = true;
    await page.win.tick(1000);
    assert.strictEqual(text(page, 'th-dynu-wait'), en['dynuKey.closed']);
    assert.strictEqual(page.win.activeTimers(), 0);
    assert.strictEqual(posts(page, 'dynu-key').length, 0, 'closing the popup saves nothing');
});

test('when the window gets focus back the key field gets focus; the clipboard is never read', async (t) => {
    const page = await open(t, routes());
    await page.click('th-dynu-have');
    const before = page.el('th-key').focused;
    page.win.fire('focus');
    assert.strictEqual(page.el('th-key').focused, before + 1);
    assert.strictEqual(page.win.navigator.clipboard, undefined);
});

test('leaving the page stops the timers and the focus listener', async (t) => {
    const page = await open(t, routes());
    await page.click('th-dynu-have');
    assert.strictEqual(page.win.listenerCount('focus'), 1);
    page.dom.view.dispatch('viewhide');
    assert.strictEqual(page.win.listenerCount('focus'), 0);
    assert.strictEqual(page.win.activeTimers(), 0);
});

// ---------------------------------------------------------------- the key

test('typing a key saves nothing; Save sends it once, clears the field and moves to the name step', async (t) => {
    const saved = s.setupState({ DynuKey: 'Saved' });
    const page = await open(t, routes({ 'POST dynu-key': s.ok({ NameCleared: false, Setup: saved }) }));
    page.el('th-key').value = s.SENTINEL;
    page.el('th-key').dispatch('input');
    await s.flush();
    await page.win.tick(5000);
    assert.strictEqual(posts(page, 'dynu-key').length, 0, 'typing must not save');

    await page.submit('th-key-form');
    assert.strictEqual(posts(page, 'dynu-key').length, 1);
    assert.deepStrictEqual(posts(page, 'dynu-key')[0].body, { Key: s.SENTINEL });
    assert.strictEqual(posts(page, 'dynu-key')[0].raw.contentType, 'application/json');
    assert.strictEqual(page.el('th-key').value, '');
    assert.ok(shown(page, 'th-step-name') && !shown(page, 'th-step-dynu'));
});

test('pasting saves straight away, after the text has landed', async (t) => {
    const page = await open(t, routes({ 'POST dynu-key': s.ok({ NameCleared: false, Setup: s.setupState({ DynuKey: 'Saved' }) }) }));
    // The browser fires paste before it inserts the text, so the field is still empty at that moment.
    page.el('th-key').dispatch('paste');
    page.el('th-key').value = s.SENTINEL;
    assert.strictEqual(posts(page, 'dynu-key').length, 0, 'the save has to wait a tick');
    await page.win.tick(0);
    assert.strictEqual(posts(page, 'dynu-key').length, 1);
    assert.strictEqual(posts(page, 'dynu-key')[0].body.Key, s.SENTINEL);
});

test('the key is in no element, attribute or console line after a save, a rejection or an outage', async (t) => {
    for (const answer of [
        s.ok({ NameCleared: false, Setup: s.setupState({ DynuKey: 'Saved' }) }),
        s.fail(400, 'dynu_key_rejected'),
        s.fail(503, 'dynu_unavailable'),
        s.fail(500, 'internal')
    ]) {
        const page = await open(t, routes({ 'POST dynu-key': answer }));
        page.el('th-key').value = s.SENTINEL;
        await page.submit('th-key-form');
        const everywhere = page.dom.all.filter((e) => e !== page.el('th-key')).flatMap((e) => e.everything());
        assert.ok(everywhere.every((x) => x.indexOf(s.SENTINEL) === -1), 'the key is on the page after ' + JSON.stringify(answer.status));
        assert.deepStrictEqual(page.consoleCalls, [], 'the page wrote to the console');
        // The one place it may remain is the field, and only when Save should work again.
        const kept = page.el('th-key').value === s.SENTINEL;
        assert.strictEqual(kept, answer.status === 503 || answer.status === 500);
        page.restoreConsole();
    }
});

test('a key Dynu refuses says so in words, and the field is cleared', async (t) => {
    const page = await open(t, routes({ 'POST dynu-key': s.fail(400, 'dynu_key_rejected') }));
    page.el('th-key').value = s.SENTINEL;
    await page.submit('th-key-form');
    assert.strictEqual(text(page, 'th-error'), en['error.dynu_key_rejected']);
    assert.ok(shown(page, 'th-error'));
    assert.strictEqual(page.el('th-key').value, '');
    assert.ok(shown(page, 'th-step-dynu'));
    assert.strictEqual(page.el('th-key-save').disabled, false);
});

test('each error code reads in plain words, by the header and not the body', async (t) => {
    for (const code of ['dynu_key_invalid', 'dynu_unavailable', 'rate_limited', 'bad_request', 'request_too_large', 'save_failed', 'internal']) {
        const page = await open(t, routes({ 'POST dynu-key': s.fail(code === 'rate_limited' ? 429 : 400, code) }));
        page.el('th-key').value = 'abc';
        await page.submit('th-key-form');
        assert.strictEqual(text(page, 'th-error'), en['error.' + code], code);
        page.restoreConsole();
    }
});

test('an empty key is refused before any request', async (t) => {
    const page = await open(t, routes());
    page.el('th-key').value = '   ';
    await page.submit('th-key-form');
    assert.strictEqual(posts(page, 'dynu-key').length, 0);
    assert.strictEqual(text(page, 'th-error'), en['error.dynu_key_invalid']);
});

test('a second Save while the first is running does nothing', async (t) => {
    let release;
    let sent = 0;
    const gate = new Promise((r) => { release = r; });
    const page = await open(t, routes());
    const original = page.api.ajax;
    page.api.ajax = function (req) {
        if (req.type === 'POST') {
            sent++;
            return gate.then(() => ({ NameCleared: false, Setup: s.setupState({ DynuKey: 'Saved' }) }));
        }
        return original.call(page.api, req);
    };
    page.el('th-key').value = 'abc';
    page.el('th-key-form').dispatch('submit');
    page.el('th-key-form').dispatch('submit');
    await s.flush();
    assert.strictEqual(sent, 1);
    release();
    await s.flush();
    assert.ok(shown(page, 'th-step-name'));
});

test('a new key whose account lacks the saved name says the name was cleared', async (t) => {
    const page = await open(t, routes({
        'GET setup': s.ok(s.setupState({ DynuKey: 'Saved', Hostname: 'alex.freeddns.org' })),
        'POST dynu-key': s.ok({ NameCleared: true, Setup: s.setupState({ DynuKey: 'Saved', Hostname: '' }) })
    }));
    await page.click('th-change-key');
    page.el('th-key').value = 'abc';
    await page.submit('th-key-form');
    assert.ok(shown(page, 'th-step-name'));
    assert.strictEqual(text(page, 'th-notice'), en['dynuKey.nameCleared']);
});

// ---------------------------------------------------------------- the name

const nameRoutes = (extra) => routes(Object.assign({ 'GET setup': s.ok(s.setupState({ DynuKey: 'Saved' })) }, extra));

test('the name is checked 600 ms after typing stops, once, and the answer is in the check line', async (t) => {
    const page = await open(t, nameRoutes({ 'POST name/check': s.ok({ Kind: 'Unchecked', Name: 'alex.freeddns.org', Problem: null, Message: 'x' }) }));
    await page.type('th-label', 'a');
    await page.win.tick(500);
    await page.type('th-label', 'al');
    await page.win.tick(500);
    assert.strictEqual(posts(page, 'name/check').length, 0, 'the timer restarts on every key');
    await page.win.tick(100);
    assert.strictEqual(posts(page, 'name/check').length, 1);
    assert.deepStrictEqual(posts(page, 'name/check')[0].body, { Label: 'al', Parent: 'freeddns.org' });
    assert.strictEqual(text(page, 'th-check'), en['name.kind.unchecked']);
});

test('check answers: Ours, and Invalid with the rule it broke', async (t) => {
    let answer = { Kind: 'Ours', Name: 'alex.freeddns.org', Problem: null, Message: 'ignored' };
    const page = await open(t, nameRoutes({ 'POST name/check': () => s.ok(answer) }));
    await page.type('th-label', 'alex');
    await page.win.tick(600);
    assert.strictEqual(text(page, 'th-check'), en['name.kind.ours']);

    answer = { Kind: 'Invalid', Name: null, Problem: 'TooShort', Message: 'Use at least 3 characters.' };
    await page.type('th-label', 'a');
    await page.win.tick(600);
    assert.strictEqual(text(page, 'th-check'), 'Use at least 3 characters.');

    answer = { Kind: 'Invalid', Name: null, Problem: 'HasDot', Message: '' };
    await page.type('th-label', 'a.b');
    await page.win.tick(600);
    assert.strictEqual(text(page, 'th-check'), en['label.hasDot']);
});

test('an old check answer that arrives late is dropped', async (t) => {
    const waiting = [];
    const page = await open(t, nameRoutes());
    const original = page.api.ajax;
    page.api.ajax = function (req) {
        if (req.url === '/Trailhead/name/check') {
            return new Promise((resolve) => waiting.push({ body: JSON.parse(req.data), resolve }));
        }
        return original.call(page.api, req);
    };
    await page.type('th-label', 'first');
    await page.win.tick(600);
    await page.type('th-label', 'second');
    await page.win.tick(600);
    assert.strictEqual(waiting.length, 2);
    waiting[1].resolve({ Kind: 'Ours', Message: '' });
    await s.flush();
    waiting[0].resolve({ Kind: 'Unchecked', Message: '' });
    await s.flush();
    assert.strictEqual(text(page, 'th-check'), en['name.kind.ours']);
});

test('Continue with an empty name asks for one and sends nothing', async (t) => {
    const page = await open(t, nameRoutes());
    await page.submit('th-name-form');
    assert.strictEqual(posts(page, 'name/claim').length, 0);
    assert.strictEqual(text(page, 'th-check'), en['label.empty']);
});

async function claimWith(t, answer, extra, label) {
    const page = await open(t, nameRoutes(Object.assign({ 'POST name/claim': answer }, extra)));
    page.el('th-label').value = label || 'alex';
    page.el('th-parent').value = 'freeddns.org';
    await page.submit('th-name-form');
    return page;
}

const SETUP_NAMED = s.setupState({ DynuKey: 'Saved', Hostname: 'alex.freeddns.org', Renewal: { State: 'NotRunYet', Outcome: null, At: null } });

test('Claimed: the page shows the name and the status line', async (t) => {
    const page = await claimWith(t, s.ok({ Kind: 'Claimed', Name: 'alex.freeddns.org', Problem: null, Message: 'x', Alternatives: [], Setup: SETUP_NAMED }));
    assert.deepStrictEqual(posts(page, 'name/claim')[0].body, { Label: 'alex', Parent: 'freeddns.org' });
    assert.ok(!shown(page, 'th-step-name'));
    assert.strictEqual(text(page, 'th-done-name-text'), 'Name: alex.freeddns.org');
    assert.strictEqual(text(page, 'th-status'), 'Your name is alex.freeddns.org.');
    assert.strictEqual(text(page, 'th-notice'), en['name.kind.claimed']);
});

test('Ours: the same, with its own words', async (t) => {
    const page = await claimWith(t, s.ok({ Kind: 'Ours', Name: 'alex.freeddns.org', Problem: null, Message: 'x', Alternatives: [], Setup: SETUP_NAMED }));
    assert.ok(shown(page, 'th-done-name'));
    assert.strictEqual(text(page, 'th-notice'), en['name.kind.ours']);
});

test('Taken: three alternatives as buttons, picking one only picks the ending and creates nothing', async (t) => {
    const page = await claimWith(t, s.ok({
        Kind: 'Taken', Name: 'www.freeddns.org', Problem: null, Message: 'x', Setup: null,
        Alternatives: ['www.mywire.org', 'www.webredirect.org', 'www.ddnsking.com', 'www.fourth.org']
    }), {}, 'www');
    assert.strictEqual(text(page, 'th-result-text'), en['name.kind.taken']);
    assert.ok(shown(page, 'th-alts'));
    const buttons = page.el('th-alt-buttons').children;
    assert.deepStrictEqual(buttons.map((b) => b.textContent), ['www.mywire.org', 'www.webredirect.org', 'www.ddnsking.com']);
    assert.ok(buttons.every((b) => b.getAttribute('type') === 'button'));

    buttons[1].dispatch('click');
    await s.flush();
    assert.strictEqual(page.el('th-parent').value, 'webredirect.org');
    assert.strictEqual(posts(page, 'name/claim').length, 1, 'picking an ending must not claim');
    assert.ok(!shown(page, 'th-alts'));
});

test('AccountFull: a button opens the Dynu names page in the popup, and Continue is pressed again by the admin', async (t) => {
    const page = await claimWith(t, s.ok({ Kind: 'AccountFull', Name: 'alex.freeddns.org', Problem: null, Message: 'x', Alternatives: [], Setup: null }));
    assert.strictEqual(text(page, 'th-result-text'), en['name.kind.accountFull']);
    assert.ok(shown(page, 'th-panel'));
    await page.click('th-panel-open');
    assert.strictEqual(page.win.calls.open[0].url, page.mod.DYNU.names);
    assert.ok(page.win.calls.open[0].duringClick);
    page.win.popups[0].closed = true;
    await page.win.tick(1000);
    assert.strictEqual(posts(page, 'name/claim').length, 1, 'closing the names page claims nothing');
});

test('DynuRefused: the full name with Copy, the add page in the popup, and one claim when it closes', async (t) => {
    let answers = [
        { Kind: 'DynuRefused', Name: 'alex.freeddns.org', Problem: null, Message: 'x', Alternatives: [], Setup: null },
        { Kind: 'Ours', Name: 'alex.freeddns.org', Problem: null, Message: 'x', Alternatives: [], Setup: SETUP_NAMED }
    ];
    const page = await claimWith(t, () => s.ok(answers.shift()));
    assert.strictEqual(text(page, 'th-result-text'), en['name.kind.dynuRefused']);
    assert.ok(shown(page, 'th-full'));
    assert.strictEqual(page.el('th-full-name').value, 'alex.freeddns.org');

    await page.click('th-add-open');
    assert.strictEqual(page.win.calls.open[0].url, page.mod.DYNU.addName);
    assert.ok(page.win.calls.open[0].duringClick);
    assert.strictEqual(posts(page, 'name/claim').length, 1);

    page.win.popups[0].closed = true;
    await page.win.tick(1000);
    assert.strictEqual(posts(page, 'name/claim').length, 2, 'one more claim when the popup closes');
    assert.ok(shown(page, 'th-done-name'));
    await page.win.tick(5000);
    page.win.fire('focus');
    await s.flush();
    assert.strictEqual(posts(page, 'name/claim').length, 2, 'and only one');
});

test('DynuRefused: getting the window back also claims once, and Check again claims on demand', async (t) => {
    let n = 0;
    const page = await claimWith(t, () => { n++; return s.ok({ Kind: 'DynuRefused', Name: 'alex.freeddns.org', Problem: null, Message: 'x', Alternatives: [], Setup: null }); });
    await page.click('th-add-open');
    page.win.fire('focus');
    await s.flush();
    assert.strictEqual(n, 2);
    page.win.fire('focus');
    await s.flush();
    assert.strictEqual(n, 2);
    await page.click('th-recheck');
    assert.strictEqual(n, 3);
});

test('DynuRefused: Copy writes the name to the clipboard, and selects it when the clipboard is not allowed', async (t) => {
    const written = [];
    const page = await claimWith(t, s.ok({ Kind: 'DynuRefused', Name: 'alex.freeddns.org', Problem: null, Message: 'x', Alternatives: [], Setup: null }));
    page.win.navigator.clipboard = { writeText: (v) => { written.push(v); return Promise.resolve(); } };
    await page.click('th-copy');
    assert.deepStrictEqual(written, ['alex.freeddns.org']);
    assert.strictEqual(text(page, 'th-copy'), en['name.copied']);

    page.win.navigator.clipboard = { writeText: () => Promise.reject(new Error('denied')) };
    await page.click('th-copy');
    assert.ok(page.el('th-full-name').selected);
});

test('Invalid on a claim shows the server words for the rule', async (t) => {
    const page = await claimWith(t, s.ok({ Kind: 'Invalid', Name: null, Problem: 'TooShort', Message: 'Use at least 3 characters.', Alternatives: [], Setup: null }), {}, 'a');
    assert.strictEqual(text(page, 'th-result-text'), 'Use at least 3 characters.');
});

test('a 409 from the name route (the saved key is no good) goes back to the Dynu step with the re-enter line', async (t) => {
    for (const code of ['dynu_key_rejected', 'dynu_key_missing']) {
        const page = await claimWith(t, s.fail(409, code));
        assert.ok(shown(page, 'th-step-dynu'), code);
        assert.ok(!shown(page, 'th-step-name'), code);
        assert.ok(shown(page, 'th-reenter'), code);
        page.restoreConsole();
    }
});

test('a 503 from Dynu says so and keeps the name step', async (t) => {
    const page = await claimWith(t, s.fail(503, 'dynu_unavailable'));
    assert.strictEqual(text(page, 'th-error'), en['error.dynu_unavailable']);
    assert.ok(shown(page, 'th-step-name'));
    assert.strictEqual(page.el('th-continue').disabled, false);
});

test('the status line always has words, whatever the tone', async (t) => {
    for (const renewal of [
        { State: 'BuildFailed', Outcome: null, At: null },
        { State: 'Ran', Outcome: 'Blocked', At: '1' },
        { State: 'Ran', Outcome: 'Failed', At: '1' },
        { State: 'Ran', Outcome: 'Issued', At: '1' }
    ]) {
        const page = await open(t, routes({ 'GET setup': s.ok(s.setupState({ DynuKey: 'Saved', Hostname: 'alex.freeddns.org', Renewal: renewal })) }));
        assert.ok(text(page, 'th-status').length > 10);
        assert.ok(text(page, 'th-status').indexOf('{') === -1, 'a placeholder was left: ' + text(page, 'th-status'));
        page.restoreConsole();
    }
});
