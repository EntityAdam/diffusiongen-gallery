const assert = require("node:assert/strict");
const { readFileSync } = require("node:fs");
const path = require("node:path");
const { test } = require("node:test");
const vm = require("node:vm");

function setup() {
    const observers = [];
    const errors = [];
    class Observer {
        constructor(callback, options) { this.callback = callback; this.options = options; this.targets = new Set(); observers.push(this); }
        observe(target) { this.targets.add(target); }
        unobserve(target) { this.targets.delete(target); }
        disconnect() { this.targets.clear(); this.disconnected = true; }
        intersect(target, isIntersecting = true) { this.callback([{ target, isIntersecting }]); }
    }
    const window = {};
    vm.runInNewContext(readFileSync(path.join(__dirname, "..", "Gallery.Web", "wwwroot", "infinite-scroll.js"), "utf8"), {
        window, IntersectionObserver: Observer, queueMicrotask, console: { error(...args) { errors.push(args); } }
    });
    const card = id => ({ dataset: { imageId: id } });
    const cards = [card("a"), card("b"), card("c")];
    const element = { cards, scrolls: 0, querySelectorAll() { return this.cards; }, scrollIntoView() { this.scrolls++; } };
    const sentinel = {};
    const calls = [];
    let invoke = (...args) => { calls.push(JSON.parse(JSON.stringify(args))); return Promise.resolve(); };
    const reference = { invokeMethodAsync: (...args) => invoke(...args) };
    const sync = (revision = 1, more = true) => window.galleryScroll.sync(element, sentinel, reference, revision, more);
    return { window, observers, errors, card, cards, element, sentinel, calls, sync, setInvoke(value) { invoke = value; } };
}
const flush = () => new Promise(resolve => setImmediate(resolve));

test("only intersecting cards request previews; leaving the viewport releases them", async () => {
    const { sync, observers, cards, calls } = setup();
    sync();
    await flush();
    assert.deepEqual(calls, [["UpdateViewportAsync", [], 1]]);
    observers[0].intersect(cards[1]);
    observers[0].intersect(cards[2]);
    await flush();
    assert.deepEqual(calls.at(-1), ["UpdateViewportAsync", ["b", "c"], 1]);
    observers[0].intersect(cards[1], false);
    await flush();
    assert.deepEqual(calls.at(-1), ["UpdateViewportAsync", ["c"], 1]);
    const count = calls.length;
    sync();
    await flush();
    assert.equal(calls.length, count);
});

test("sentinel appends one batch at a time and rearms to fill a short viewport", async () => {
    const env = setup();
    let complete;
    env.setInvoke((...args) => {
        env.calls.push(args);
        return args[0] === "LoadMoreAsync" ? new Promise(resolve => complete = resolve) : Promise.resolve();
    });
    env.sync();
    const more = env.observers[1];
    more.intersect(env.sentinel);
    more.intersect(env.sentinel);
    assert.equal(env.calls.filter(call => call[0] === "LoadMoreAsync").length, 1);
    complete();
    await flush();
    assert.ok(more.targets.has(env.sentinel));
    more.intersect(env.sentinel);
    assert.equal(env.calls.filter(call => call[0] === "LoadMoreAsync").length, 2);
    env.sync(1, false);
    complete();
    await flush();
    more.intersect(env.sentinel);
    assert.equal(env.calls.filter(call => call[0] === "LoadMoreAsync").length, 2);
});

test("filter resets disconnect stale observers and cancel queued reports", async () => {
    const { sync, observers, cards, calls, element } = setup();
    sync();
    observers[0].intersect(cards[0]);
    sync(2);
    observers[0].intersect(cards[1]);
    observers[1].intersect({});
    observers[2].intersect(cards[2]);
    await flush();
    assert.ok(observers[0].disconnected);
    assert.ok(observers[1].disconnected);
    assert.deepEqual(calls, [["UpdateViewportAsync", ["c"], 2]]);
    assert.equal(element.scrolls, 1);
});

test("new cards are observed and removed cards stop requesting previews", async () => {
    const { sync, observers, cards, calls, element, card } = setup();
    sync();
    observers[0].intersect(cards[0]);
    await flush();
    const appended = card("d");
    element.cards = [cards[1], appended];
    sync();
    observers[0].intersect(cards[0]);
    observers[0].intersect(appended);
    await flush();
    assert.deepEqual(calls.at(-1), ["UpdateViewportAsync", ["d"], 1]);
    assert.ok(!observers[0].targets.has(cards[0]));
    assert.ok(observers[0].targets.has(appended));
});

test("disposal disconnects observers and prevents queued or late work", async () => {
    const { window, sync, observers, cards, calls, element, sentinel } = setup();
    sync();
    observers[0].intersect(cards[0]);
    window.galleryScroll.detach(element);
    observers[0].intersect(cards[1]);
    observers[1].intersect(sentinel);
    await flush();
    assert.deepEqual(calls, []);
    assert.ok(observers.every(observer => observer.disconnected));
});


test("failed append does not create an observer retry loop", async () => {
    const env = setup();
    env.setInvoke((...args) => {
        env.calls.push(args);
        return args[0] === "LoadMoreAsync" ? Promise.reject(new Error("disconnected")) : Promise.resolve();
    });
    env.sync();
    env.observers[1].intersect(env.sentinel, false);
    assert.equal(env.calls.filter(call => call[0] === "LoadMoreAsync").length, 0);
    env.observers[1].intersect(env.sentinel);
    await flush();
    env.sync();
    env.observers[1].intersect(env.sentinel);
    await flush();
    assert.equal(env.calls.filter(call => call[0] === "LoadMoreAsync").length, 1);
    assert.equal(env.errors.length, 1);
});
