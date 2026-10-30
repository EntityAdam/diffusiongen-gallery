const assert = require("node:assert/strict");
const { readFileSync } = require("node:fs");
const path = require("node:path");
const { test } = require("node:test");
const vm = require("node:vm");

const settle = () => new Promise(resolve => setImmediate(resolve));

function setup(initial = "") {
    const listeners = new Map();
    const timers = new Map();
    const calls = [];
    let nextTimer = 0;
    let observer;
    const element = {
        value: initial, disabled: false,
        addEventListener(name, handler) { listeners.set(name, handler); },
        removeEventListener(name) { listeners.delete(name); }
    };
    const target = {
        invokeMethodAsync(method, value, revision) {
            return new Promise((resolve, reject) => calls.push({ method, value, revision, resolve, reject }));
        }
    };
    const window = {};
    vm.runInNewContext(readFileSync(path.join(__dirname, "..", "Gallery.Web", "wwwroot", "filter-input.js"), "utf8"), {
        window,
        MutationObserver: class {
            constructor(callback) { observer = callback; }
            observe() {}
            disconnect() { observer = null; }
        },
        setTimeout(callback, delay) { assert.equal(delay, 300); timers.set(++nextTimer, callback); return nextTimer; },
        clearTimeout(id) { timers.delete(id); }
    });
    window.galleryFilterInput.attach(element, target, initial, 0, 300);
    return {
        element, calls, listeners, timers,
        type(value) { element.value = value; listeners.get("input")(); },
        idle() { const jobs = [...timers.values()]; timers.clear(); jobs.forEach(job => job()); },
        disabled(value) { element.disabled = value; observer(); },
        sync(value, revision) { window.galleryFilterInput.sync(element, value, revision); },
        detach() { window.galleryFilterInput.detach(element); }
    };
}

test("rapid typing submits city once after the debounce, without replaying prefixes", async () => {
    const s = setup();
    for (const value of ["c", "ci", "cit", "city"]) s.type(value);
    assert.equal(s.calls.length, 0);
    assert.equal(s.timers.size, 1);
    s.idle();
    assert.equal(s.calls.length, 1);
    assert.equal(s.calls[0].value, "city");
    s.calls[0].resolve(true);
    await settle();
    assert.equal(s.element.value, "city");
});

test("a slow response never rewinds newer text and coalesces queued edits to the latest value", async () => {
    const s = setup();
    s.type("c"); s.idle();
    for (const value of ["ci", "cit", "city"]) { s.type(value); s.idle(); }
    assert.deepEqual(s.calls.map(call => call.value), ["c"]);
    assert.equal(s.element.value, "city");
    s.calls[0].resolve(true);
    await settle();
    assert.deepEqual(s.calls.map(call => call.value), ["c", "city"]);
    assert.equal(s.element.value, "city");
    s.calls[1].resolve(true);
    await settle();
    assert.equal(s.calls.length, 2);
});

test("an acknowledgement does not bypass the debounce for text still being typed", async () => {
    const s = setup();
    s.type("c"); s.idle();
    s.type("city");
    s.calls[0].resolve(true);
    await settle();
    assert.equal(s.calls.length, 1);
    s.idle();
    assert.equal(s.calls[1].value, "city");
});

test("reset cancels unsent text even when the previous server value was already empty", () => {
    const s = setup();
    s.type("city");
    s.sync("", 1);
    s.idle();
    assert.equal(s.element.value, "");
    assert.equal(s.calls.length, 0);
});

test("reset cancels queued text behind an in-flight request and rejects stale synchronization", async () => {
    const s = setup();
    s.type("c"); s.idle();
    s.type("city"); s.idle();
    s.sync("", 1);
    s.sync("c", 0);
    s.calls[0].resolve(false);
    await settle();
    assert.equal(s.element.value, "");
    assert.equal(s.calls.length, 1);
    s.type("forest"); s.idle();
    assert.equal(s.calls[1].value, "forest");
    assert.equal(s.calls[1].revision, 1);
});

test("temporarily disabled controls resume the latest pending edit when enabled", async () => {
    const s = setup();
    s.type("c"); s.idle();
    s.type("city"); s.idle();
    s.disabled(true);
    s.calls[0].resolve(true);
    await settle();
    assert.equal(s.calls.length, 1);
    s.disabled(false);
    assert.equal(s.calls[1].value, "city");
});

test("a server-busy rejection waits for re-enable instead of retrying in a loop", async () => {
    const s = setup();
    s.type("city"); s.idle();
    s.calls[0].resolve(false);
    await settle();
    assert.equal(s.calls.length, 1);
    s.disabled(true); s.disabled(false);
    assert.equal(s.calls.length, 2);
    assert.equal(s.calls[1].value, "city");
});

test("IME composition is submitted only when finished; Enter flushes immediately", () => {
    const s = setup();
    s.listeners.get("compositionstart")();
    s.type("東京"); s.idle();
    s.listeners.get("keydown")({ key: "Enter", isComposing: true, preventDefault() { assert.fail("IME Enter must be left alone"); } });
    assert.equal(s.calls.length, 0);
    s.listeners.get("compositionend")();
    let prevented = false;
    s.listeners.get("keydown")({ key: "Enter", preventDefault() { prevented = true; } });
    assert.equal(prevented, true);
    assert.equal(s.calls[0].value, "東京");
    assert.equal(s.timers.size, 0);
});

test("blur flushes pending text, while reverting to the applied value does not refilter", () => {
    const s = setup("city");
    s.type("cities"); s.type("city"); s.idle();
    assert.equal(s.calls.length, 0);
    s.type("forest");
    s.listeners.get("blur")();
    assert.equal(s.calls[0].value, "forest");
});

test("disposal cancels timers and prevents queued edits from running after an old request completes", async () => {
    const s = setup();
    s.type("c"); s.idle();
    s.type("city"); s.idle();
    s.detach();
    s.calls[0].resolve(true);
    await settle();
    assert.equal(s.calls.length, 1);
    assert.equal(s.listeners.size, 0);
    assert.equal(s.timers.size, 0);
});

test("disconnection does not cause an automatic retry loop", async () => {
    const s = setup();
    s.type("city"); s.idle();
    s.calls[0].reject(new Error("circuit closed"));
    await settle();
    assert.equal(s.calls.length, 1);
});
