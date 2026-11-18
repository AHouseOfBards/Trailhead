// node --test tests/web/*.test.js: the page's pure functions.
'use strict';

const test = require('node:test');
const assert = require('node:assert');
const s = require('./support.js');

test('popupFeatures: 520 x 720 centred on the Jellyfin window, popup=yes', async () => {
    const mod = await s.loadModule();
    const f = mod.popupFeatures(s.fakeWindow({ screen: { availWidth: 1920, availHeight: 1080 }, outerWidth: 1400, outerHeight: 900, screenX: 100, screenY: 50 }));
    assert.strictEqual(f, 'popup=yes,width=520,height=720,left=540,top=140');
});

test('popupFeatures: smaller on a small screen, never negative, never below a usable size', async () => {
    const mod = await s.loadModule();
    const small = mod.popupFeatures(s.fakeWindow({ screen: { availWidth: 400, availHeight: 500 }, outerWidth: 400, outerHeight: 500, screenX: 0, screenY: 0 }));
    assert.match(small, /width=360,height=460/);
    const tiny = mod.popupFeatures(s.fakeWindow({ screen: { availWidth: 200, availHeight: 200 }, outerWidth: 200, outerHeight: 200, screenX: 0, screenY: 0 }));
    assert.match(tiny, /width=320,height=400/);
    assert.match(tiny, /left=0,top=0/);
    // A window dragged off to the left: the popup still opens on screen.
    const off = mod.popupFeatures(s.fakeWindow({ screenX: -1500 }));
    assert.match(off, /left=0,/);
});

test('openPopup: opens with the popup features, clears the opener, and gives null when blocked', async () => {
    const mod = await s.loadModule();
    const win = s.fakeWindow();
    const popup = mod.openPopup(win, 'https://example.test/x');
    assert.ok(popup);
    assert.strictEqual(popup.opener, null);
    assert.strictEqual(win.calls.open[0].name, 'trailhead-dynu');
    assert.match(win.calls.open[0].features, /popup=yes/);
    assert.ok(win.calls.open[0].features.indexOf('noopener') === -1, 'noopener makes a blocked popup look like an open one');

    assert.strictEqual(mod.openPopup(s.fakeWindow({ blockPopups: true }), 'https://example.test/x'), null);
    const throwing = { screen: {}, open() { throw new Error('no'); } };
    assert.strictEqual(mod.openPopup(throwing, 'https://example.test/x'), null);
});

test('languageToAsk: only a tag the route accepts is passed on', async () => {
    const mod = await s.loadModule();
    assert.strictEqual(mod.languageToAsk('pt-br'), 'pt-br');
    assert.strictEqual(mod.languageToAsk('DE'), 'de');
    assert.strictEqual(mod.languageToAsk('en-us'), 'en-us');
    ['', null, undefined, '../x', 'a', 'pt_br', 'x'.repeat(40), 'de/../../x', 'de?x=1'].forEach((bad) => {
        assert.strictEqual(mod.languageToAsk(bad), 'en', String(bad));
    });
});

test('adminLanguage: the user setting, then html lang, then data-culture, then the browser', async () => {
    const mod = await s.loadModule();
    const store = (v) => ({ getItem: (k) => (k === 'u1-language' ? v : null) });
    assert.strictEqual(mod.adminLanguage({ userId: 'u1', storage: store('pt_BR'), htmlLang: 'en', navigatorLanguage: 'fr' }), 'pt-br');
    assert.strictEqual(mod.adminLanguage({ userId: 'u1', storage: store(''), htmlLang: 'en', dataCulture: 'de', navigatorLanguage: 'fr' }), 'de');
    assert.strictEqual(mod.adminLanguage({ userId: '', htmlLang: 'es', navigatorLanguage: 'fr' }), 'es');
    assert.strictEqual(mod.adminLanguage({ userId: 'u1', storage: { getItem() { throw new Error('blocked'); } }, navigatorLanguage: 'FR-ca' }), 'fr-ca');
    assert.strictEqual(mod.adminLanguage({}), '');
});

test('errorKey: a known code by its code, an unknown one by its status', async () => {
    const mod = await s.loadModule();
    assert.strictEqual(mod.errorKey('dynu_key_rejected', 400), 'error.dynu_key_rejected');
    assert.strictEqual(mod.errorKey('a_new_code', 429), 'error.rate_limited');
    assert.strictEqual(mod.errorKey('', 503), 'error.dynu_unavailable');
    assert.strictEqual(mod.errorKey('', 400), 'error.bad_request');
    assert.strictEqual(mod.errorKey('', 0), 'error.network');
    assert.strictEqual(mod.errorKey('', 500), 'error.internal');
    assert.strictEqual(mod.errorKey('constructor', 500), 'error.internal');
});

test('toApiError reads the code from X-Campground-Error, and copes with anything', async () => {
    const mod = await s.loadModule();
    assert.deepStrictEqual(mod.toApiError({ status: 409, headers: { get: (h) => (h === 'X-Campground-Error' ? 'dynu_key_missing' : null) } }), { status: 409, code: 'dynu_key_missing' });
    assert.deepStrictEqual(mod.toApiError(new Error('network')), { status: 0, code: '' });
    assert.deepStrictEqual(mod.toApiError(undefined), { status: 0, code: '' });
    assert.deepStrictEqual(mod.toApiError({ status: 500, headers: { get() { throw new Error('x'); } } }), { status: 500, code: '' });
});

test('viewFor: one step at a time, the finished step as a line above', async () => {
    const mod = await s.loadModule();
    const none = mod.viewFor(s.setupState(), {});
    assert.strictEqual(none.step, 'dynu');
    assert.strictEqual(none.keyLine, false);
    assert.strictEqual(none.reenter, false);

    const reenter = mod.viewFor(s.setupState({ DynuKey: 'Reenter' }), {});
    assert.strictEqual(reenter.step, 'dynu');
    assert.strictEqual(reenter.reenter, true);

    const saved = mod.viewFor(s.setupState({ DynuKey: 'Saved' }), {});
    assert.strictEqual(saved.step, 'name');
    assert.strictEqual(saved.keyLine, true);
    assert.strictEqual(saved.renameNote, false);
    assert.strictEqual(saved.canCancelName, false);

    const done = mod.viewFor(s.setupState({ DynuKey: 'Saved', Hostname: 'a.freeddns.org' }), {});
    assert.strictEqual(done.step, 'done');
    assert.strictEqual(done.nameLine, 'a.freeddns.org');
    assert.strictEqual(done.keyLine, true);

    const rename = mod.viewFor(s.setupState({ DynuKey: 'Saved', Hostname: 'a.freeddns.org' }), { changingName: true });
    assert.strictEqual(rename.step, 'name');
    assert.strictEqual(rename.renameNote, true);
    assert.strictEqual(rename.canCancelName, true);

    const change = mod.viewFor(s.setupState({ DynuKey: 'Saved', Hostname: 'a.freeddns.org' }), { changingKey: true });
    assert.strictEqual(change.step, 'dynu');
    assert.strictEqual(change.canCancelKey, true);
    assert.strictEqual(change.keyLine, false);
});

test('statusFor: not set up, name ready, and each renewal outcome', async () => {
    const mod = await s.loadModule();
    const named = (Renewal) => s.setupState({ DynuKey: 'Saved', Hostname: 'a.freeddns.org', Renewal });
    const at = (iso) => 'T' + iso;
    assert.strictEqual(mod.statusFor(s.setupState(), at).key, 'status.notSetUp');
    assert.strictEqual(mod.statusFor(named({ State: 'NotRunYet' }), at).key, 'status.nameReady');
    assert.strictEqual(mod.statusFor(named({ State: 'NotRunYet' }), at).args.name, 'a.freeddns.org');
    assert.strictEqual(mod.statusFor(named({ State: 'NotSetUp' }), at).key, 'status.nameReady');
    assert.strictEqual(mod.statusFor(named({ State: 'BuildFailed' }), at).key, 'status.buildFailed');
    assert.deepStrictEqual(['Issued', 'UpToDate', 'Blocked', 'Failed'].map((o) => mod.statusFor(named({ State: 'Ran', Outcome: o, At: '1' }), at).key),
        ['status.issued', 'status.upToDate', 'status.blocked', 'status.failed']);
    assert.strictEqual(mod.statusFor(named({ State: 'Ran', Outcome: 'Issued', At: '1' }), at).args.when, 'T1');
    // Tone never stands alone: every status has words too (checked in the controller test), and a failure is not "good".
    assert.strictEqual(mod.statusFor(named({ State: 'Ran', Outcome: 'Failed', At: '1' }), at).tone, 'bad');
});

test('format and parentOf', async () => {
    const mod = await s.loadModule();
    assert.strictEqual(mod.format('Use at least {min} characters.', { min: 3 }), 'Use at least 3 characters.');
    assert.strictEqual(mod.format('keep {unknown}', {}), 'keep {unknown}');
    assert.strictEqual(mod.format('keep {toString}', {}), 'keep {toString}');
    assert.strictEqual(mod.parentOf('alex.mywire.org'), 'mywire.org');
    assert.strictEqual(mod.parentOf('nodots'), '');
});

test('the Dynu URLs are https on dynu.com and nothing else', async () => {
    const mod = await s.loadModule();
    const urls = Object.values(mod.DYNU);
    assert.strictEqual(urls.length, 4);
    urls.forEach((u) => assert.ok(/^https:\/\/www\.dynu\.com\/en-US\/ControlPanel\/[A-Za-z]+$/.test(u), u));
});
