const assert = require("node:assert/strict");
const { readFileSync } = require("node:fs");
const path = require("node:path");
const { test } = require("node:test");
const vm = require("node:vm");

function setup({ dialog = false, selection = false } = {}) {
    const listeners = new Map();
    const calls = [];
    const card = id => {
        const element = {
            dataset: { imageId: id },
            closest(selector) { return selector.startsWith(".image-card") ? element : null; }
        };
        return element;
    };
    const input = { type: "text", closest(selector) { return selector.startsWith("input") ? input : null; } };
    const doc = {
        activeElement: null,
        addEventListener(name, handler, capture) {
            assert.equal(capture, true);
            listeners.set(name, handler);
        },
        removeEventListener(name) { listeners.delete(name); },
        querySelector(selector) {
            return (selector.startsWith("[role=dialog]") && dialog) || (selector === ".bulk-bar, [data-has-selection=true]" && selection) ? {} : null;
        },
        querySelectorAll() { return []; }
    };
    const window = {};
    vm.runInNewContext(readFileSync(path.join(__dirname, "..", "Gallery.Web", "wwwroot", "viewer.js"), "utf8"), {
        window, Element: class { }, document: { addEventListener() { } }
    });
    window.galleryGrid.attach({ invokeMethodAsync: (...args) => { calls.push(args); return Promise.resolve(); } }, doc);
    const key = (value, target, extra = {}) => {
        const event = { key: value, target, defaultPrevented: false, preventDefault() { this.defaultPrevented = true; }, ...extra };
        listeners.get("keydown")(event);
        return event;
    };
    const hover = target => listeners.get("mouseover")({ target });
    return { window, doc, listeners, calls, card, input, key, hover };
}

const flush = () => new Promise(resolve => setImmediate(resolve));

test("review confetti bursts left then right after 80ms and cleans up", async () => {
    const layers = [];
    const delays = [];
    const document = {
        addEventListener() {},
        createElement() {
            return {
                style: {}, children: [], setAttribute() {},
                appendChild(child) { this.children.push(child); },
                animate() { return { finished: Promise.resolve() }; },
                remove() { this.removed = true; }
            };
        }
    };
    const window = { matchMedia: () => ({ matches: false }) };
    vm.runInNewContext(readFileSync(path.join(__dirname, "..", "Gallery.Web", "wwwroot", "viewer.js"), "utf8"), {
        window, document, Element: class {},
        setTimeout(resolve, delay) { delays.push(delay); resolve(); }
    });
    await window.galleryViewer.celebrate({
        isConnected: true, clientWidth: 1000,
        appendChild(layer) { layers.push(layer); }
    });
    await flush();
    assert.deepEqual(delays, [80]);
    assert.equal(layers.length, 2);
    assert.equal(layers[0].children.length, 36);
    assert.equal(layers[0].children[0].style.left, "0");
    assert.equal(layers[1].children[0].style.right, "0");
    assert.ok(layers.every(layer => layer.removed));
    window.matchMedia = () => ({ matches: true });
    await window.galleryViewer.celebrate({ appendChild() { assert.fail("Reduced motion must not animate"); } });
    assert.equal(layers.length, 2);
});

test("Escape forwards clear selection without a hovered or focused card", async () => {
    const { calls, key } = setup({ selection: true });
    assert.equal(key("Escape", {}).defaultPrevented, true);
    await flush();
    assert.deepEqual(calls, [["HandleGridKey", "escape", "", false]]);
});

test("rating, favorite and deletion keys act on the hovered or focused card", async () => {
    const { doc, calls, card, key, hover } = setup();
    hover(card("hovered"));
    assert.equal(key("3", {}).defaultPrevented, true);
    doc.activeElement = card("focused");
    key("F", {});
    assert.equal(key("Delete", {}).defaultPrevented, false);
    key("D", {}, { shiftKey: false });
    key("x", {}, { shiftKey: true });
    await flush();
    assert.deepEqual(calls, [["HandleGridKey", "3", "hovered", false], ["HandleGridKey", "f", "focused", false],
        ["HandleGridKey", "d", "focused", false], ["HandleGridKey", "x", "focused", true]]);
});

test("shortcuts are ignored while typing, with modifiers, without a card, or behind a dialog", async () => {
    const typing = setup();
    typing.hover(typing.card("a"));
    assert.equal(typing.key("5", typing.input).defaultPrevented, false);
    assert.equal(typing.key("5", {}, { ctrlKey: true }).defaultPrevented, false);
    const empty = setup();
    assert.equal(empty.key("5", {}).defaultPrevented, false);
    assert.equal(empty.key("Escape", {}).defaultPrevented, false);
    const modal = setup({ dialog: true });
    modal.hover(modal.card("a"));
    assert.equal(modal.key("5", {}).defaultPrevented, false);
    await flush();
    assert.equal(typing.calls.length + empty.calls.length + modal.calls.length, 0);
});

test("detach removes the document listeners", () => {
    const { window, listeners } = setup();
    window.galleryGrid.detach();
    assert.equal(listeners.size, 0);
});

test("full screen viewer forwards declared extra keys only", async () => {
    let keydown = null;
    const document = {
        activeElement: null,
        body: { style: { overflow: "" } },
        addEventListener(name, handler) { if (name === "keydown") keydown = handler; },
        removeEventListener() { keydown = null; }
    };
    const window = {};
    vm.runInNewContext(readFileSync(path.join(__dirname, "..", "Gallery.Web", "wwwroot", "viewer.js"), "utf8"), { window, Element: class { }, document });
    const calls = [];
    const element = { dataset: { review: "false", keys: "arrowup k a" }, focus() { }, querySelectorAll() { return []; } };
    window.galleryViewer.attach(element, { invokeMethodAsync: (...args) => { calls.push(args); return Promise.resolve(); } });
    const target = { closest() { return null; } };
    const press = key => { const event = { key, target, defaultPrevented: false, preventDefault() { this.defaultPrevented = true; } }; keydown(event); return event; };
    assert.equal(press("K").defaultPrevented, true);
    press("ArrowUp");
    assert.equal(press("s").defaultPrevented, false);
    await flush();
    assert.deepEqual(calls, [["HandleKey", "k"], ["HandleKey", "arrowup"]]);
    window.galleryViewer.detach(element);
    assert.equal(keydown, null);
});

test("gallerySecurity recognizes panic hotkeys and triggers panic lock", async () => {
    const listeners = new Map();
    const calls = [];
    const doc = {
        activeElement: null,
        addEventListener(name, handler, capture) {
            listeners.set(name, handler);
        },
        removeEventListener(name) { listeners.delete(name); },
        querySelectorAll() { return []; }
    };
    const revokedUrls = [];
    const window = {
        URL: {
            revokeObjectURL(url) { revokedUrls.push(url); }
        }
    };
    vm.runInNewContext(readFileSync(path.join(__dirname, "..", "Gallery.Web", "wwwroot", "viewer.js"), "utf8"), {
        window, Element: class { }, document: { addEventListener() { } }, URL: window.URL, setInterval, clearInterval, Date
    });

    const dotNetRef = {
        invokeMethodAsync: (...args) => { calls.push(args); return Promise.resolve(); }
    };
    window.gallerySecurity.attach(dotNetRef, 15, doc);

    window.gallerySecurity.trackUrl("blob:http://localhost/test-blob-1");
    assert.equal(window.gallerySecurity.activeUrls.has("blob:http://localhost/test-blob-1"), true);

    const keydown = listeners.get("keydown");
    assert.ok(keydown);

    keydown({ key: "l", ctrlKey: true, shiftKey: false, defaultPrevented: false, preventDefault() {} });
    assert.equal(calls.length, 0);

    let prevented = false;
    keydown({ key: "L", ctrlKey: true, shiftKey: true, defaultPrevented: false, preventDefault() { prevented = true; } });
    assert.equal(prevented, true);
    assert.deepEqual(calls, [["TriggerPanicLock"]]);
    assert.deepEqual(revokedUrls, ["blob:http://localhost/test-blob-1"]);
    assert.equal(window.gallerySecurity.activeUrls.size, 0);

    assert.equal(window.gallerySecurity.isPanicKey({ key: "l", altKey: true, shiftKey: true }), true);
    assert.equal(window.gallerySecurity.isPanicKey({ key: "Escape", shiftKey: true }), true);
    assert.equal(window.gallerySecurity.isPanicKey({ key: "Escape", shiftKey: false }), false);

    window.gallerySecurity.detach();
});

test("gallerySecurity purgeAllMedia clears tracked blobs and DOM images/videos", () => {
    const revoked = [];
    const window = {
        URL: { revokeObjectURL: url => revoked.push(url) }
    };
    vm.runInNewContext(readFileSync(path.join(__dirname, "..", "Gallery.Web", "wwwroot", "viewer.js"), "utf8"), {
        window, Element: class { }, document: { addEventListener() { } }, URL: window.URL
    });

    window.gallerySecurity.trackUrl("blob:http://localhost/image-1");
    window.gallerySecurity.trackUrl("blob:http://localhost/video-1");
    window.gallerySecurity.trackUrl("https://external.com/not-blob");

    const img = { src: "blob:http://localhost/image-1", removeAttribute() {} };
    let videoPaused = false;
    const video = { src: "blob:http://localhost/video-1", pause() { videoPaused = true; }, load() {} };
    const doc = {
        querySelectorAll(sel) {
            if (sel === "img") return [img];
            if (sel === "video") return [video];
            return [];
        }
    };

    window.gallerySecurity.purgeAllMedia(doc);
    assert.equal(revoked.includes("blob:http://localhost/image-1"), true);
    assert.equal(revoked.includes("blob:http://localhost/video-1"), true);
    assert.equal(img.src.startsWith("data:image/gif;base64"), true);
    assert.equal(video.src, "");
    assert.equal(videoPaused, true);
    assert.equal(window.gallerySecurity.activeUrls.size, 0);
});

test("gallerySecurity idle auto-lock triggers after configured inactivity", async () => {
    const listeners = new Map();
    const calls = [];
    const doc = {
        addEventListener(name, handler) { listeners.set(name, handler); },
        removeEventListener(name) { listeners.delete(name); },
        querySelectorAll() { return []; }
    };
    const window = {
        URL: { revokeObjectURL() {} }
    };
    vm.runInNewContext(readFileSync(path.join(__dirname, "..", "Gallery.Web", "wwwroot", "viewer.js"), "utf8"), {
        window, Element: class { }, document: { addEventListener() { } }, URL: window.URL, setInterval, clearInterval, Date
    });

    const dotNetRef = {
        invokeMethodAsync: (...args) => { calls.push(args); return Promise.resolve(); }
    };
    window.gallerySecurity.attach(dotNetRef, 0.0001, doc);

    await new Promise(resolve => setTimeout(resolve, 50));
    const visibilityHandler = listeners.get("visibilitychange");
    doc.visibilityState = "visible";
    visibilityHandler();

    assert.deepEqual(calls, [["TriggerAutoLock"]]);
    window.gallerySecurity.detach();
});
