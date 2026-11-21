const assert = require("node:assert/strict");
const { readFileSync } = require("node:fs");
const path = require("node:path");
const { test } = require("node:test");
const vm = require("node:vm");
const { JSDOM } = require("jsdom");

function loadGalleryScripts(globals, scripts = ["gallery-dom.js", "gallery-grid.js", "gallery-security.js", "viewer.js"]) {
    const context = vm.createContext(globals);
    for (const script of scripts) {
        const source = readFileSync(path.join(__dirname, "..", "Gallery.Web", "wwwroot", script), "utf8");
        vm.runInContext(source, context, { filename: script });
    }
}

function setup({ dialog = "", selection = false, canUseGallerySelection = false } = {}) {
    const { window } = new JSDOM(`<main data-has-selection="${selection || canUseGallerySelection}"
        data-can-use-gallery-selection="${canUseGallerySelection}"><div class="image-grid"></div><input></main>
        ${dialog}`);
    const doc = window.document;
    const grid = doc.querySelector(".image-grid");
    const input = doc.querySelector("input");
    const calls = [];
    let pointer = null;
    const card = id => {
        const element = doc.createElement("article");
        element.className = "image-card";
        element.dataset.imageId = id;
        element.innerHTML = '<button class="image-open">Open</button>';
        grid.append(element);
        // jsdom has no physical pointer. Only :hover is simulated; structural selectors use the DOM.
        const matches = element.matches.bind(element);
        element.matches = selector => selector === ":hover" ? element.contains(pointer) : matches(selector);
        return element;
    };
    loadGalleryScripts({ window, document: doc }, ["gallery-grid.js"]);
    window.galleryGrid.attach({ invokeMethodAsync: (...args) => { calls.push(args); return Promise.resolve(); } }, doc);
    const key = (value, target = doc.activeElement, extra = {}) => {
        const event = new window.KeyboardEvent("keydown", { key: value, bubbles: true, cancelable: true, ...extra });
        target.dispatchEvent(event);
        return event;
    };
    const hover = (target = doc.body) => {
        pointer = target;
        target.dispatchEvent(new window.MouseEvent("mouseover", { bubbles: true }));
    };
    const leave = (target, relatedTarget = null) => {
        pointer = relatedTarget;
        target.dispatchEvent(new window.MouseEvent("mouseout", { bubbles: true, relatedTarget }));
    };
    return { window, doc, grid, calls, card, input, key, hover, leave };
}

const flush = () => new Promise(resolve => setImmediate(resolve));

for (const shortcut of ["T", "E"]) {
    test(`${shortcut} acts only on a usable gallery selection, outside inputs and dialogs`, async () => {
        const gallery = setup({ canUseGallerySelection: true });
        const otherGrid = setup({ selection: true });
        const modal = setup({ canUseGallerySelection: true, dialog: '<div role="dialog" aria-modal="true"></div>' });
        otherGrid.hover(otherGrid.card("hovered"));
        assert.equal(gallery.key(shortcut).defaultPrevented, true);
        assert.equal(gallery.key(shortcut, gallery.input).defaultPrevented, false);
        assert.equal(gallery.key(shortcut, undefined, { ctrlKey: true }).defaultPrevented, false);
        assert.equal(otherGrid.key(shortcut).defaultPrevented, false);
        assert.equal(modal.key(shortcut).defaultPrevented, false);
        await flush();
        assert.deepEqual(gallery.calls, [["HandleGridKey", shortcut.toLowerCase(), "", false]]);
        assert.deepEqual(otherGrid.calls, []);
        assert.deepEqual(modal.calls, []);
    });
}

test("holding E does not request another export", async () => {
    const gallery = setup({ canUseGallerySelection: true });
    assert.equal(gallery.key("e", undefined, { repeat: true }).defaultPrevented, false);
    await flush();
    assert.deepEqual(gallery.calls, []);
});

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
    loadGalleryScripts({
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
    assert.equal(key("Escape").defaultPrevented, true);
    await flush();
    assert.deepEqual(calls, [["HandleGridKey", "escape", "", false]]);
});

test("rating, favorite and deletion keys act on the hovered or focused card", async () => {
    const { calls, card, key, hover } = setup();
    hover(card("hovered"));
    assert.equal(key("3").defaultPrevented, true);
    hover();
    card("focused").querySelector("button").focus();
    key("F");
    assert.equal(key("Delete").defaultPrevented, false);
    key("D");
    key("x", undefined, { shiftKey: true });
    await flush();
    assert.deepEqual(calls, [["HandleGridKey", "3", "hovered", false], ["HandleGridKey", "f", "focused", false],
        ["HandleGridKey", "d", "focused", false], ["HandleGridKey", "x", "focused", true]]);
});

test("hovered card overrides retained focus for all curation and selection shortcuts", async () => {
    const { doc, calls, card, key, hover } = setup();
    card("last-selected").querySelector("button").focus();
    hover(card("under-pointer"));
    for (const value of ["0", "1", "2", "3", "4", "5", "f", "d", "x"]) key(value, doc.activeElement);
    hover(card("next-pointer-target"));
    key("x", doc.activeElement, { shiftKey: true });
    await flush();
    assert.deepEqual(calls, [
        ...["0", "1", "2", "3", "4", "5", "f", "d", "x"].map(value => ["HandleGridKey", value, "under-pointer", false]),
        ["HandleGridKey", "x", "next-pointer-target", true]
    ]);
});

test("leaving a card or the document clears hover and restores focused-card shortcuts", async () => {
    const { doc, calls, card, key, hover, leave } = setup();
    const hovered = card("under-pointer");
    card("focused").querySelector("button").focus();
    hover(hovered);
    // Moving between controls within the same card must retain its target.
    leave(hovered, hovered.querySelector("button"));
    key("f", doc.activeElement);
    leave(hovered);
    key("d", doc.activeElement);
    doc.activeElement.blur();
    assert.equal(key("f").defaultPrevented, false);
    await flush();
    assert.deepEqual(calls, [["HandleGridKey", "f", "under-pointer", false], ["HandleGridKey", "d", "focused", false]]);
});

test("shortcuts are ignored while typing, with modifiers, without a card, or behind a dialog", async () => {
    const typing = setup();
    typing.hover(typing.card("a"));
    assert.equal(typing.key("5", typing.input).defaultPrevented, false);
    assert.equal(typing.key("5", undefined, { ctrlKey: true }).defaultPrevented, false);
    const empty = setup();
    assert.equal(empty.key("5").defaultPrevented, false);
    assert.equal(empty.key("Escape").defaultPrevented, false);
    const modal = setup({ dialog: "<dialog open></dialog>" });
    modal.hover(modal.card("a"));
    assert.equal(modal.key("5").defaultPrevented, false);
    await flush();
    assert.equal(typing.calls.length + empty.calls.length + modal.calls.length, 0);
});

test("detaching a retired grid leaves the replacement listener attached", async () => {
    const { window, doc, card, key, calls } = setup();
    const reference = { invokeMethodAsync: (...args) => { calls.push(args); return Promise.resolve(); } };
    window.galleryGrid.attach(reference, doc, 1);
    window.galleryGrid.attach(reference, doc, 2);
    window.galleryGrid.detach(1);
    card("a").querySelector("button").focus();
    key("5");
    await flush();
    assert.deepEqual(calls, [["HandleGridKey", "5", "a", false]]);
    window.galleryGrid.detach(2);
    assert.equal(key("5").defaultPrevented, false);
    await flush();
    assert.equal(calls.length, 1);
});

test("detach releases all grid event listeners", t => {
    const { window, doc } = setup();
    window.galleryGrid.detach();
    const added = t.mock.method(doc, "addEventListener").mock;
    const removed = t.mock.method(doc, "removeEventListener").mock;
    window.galleryGrid.attach({}, doc);
    window.galleryGrid.detach();
    assert.ok(added.callCount() > 0);
    for (const { arguments: [type, handler, capture] } of added.calls)
        assert.ok(removed.calls.some(({ arguments: args }) =>
            args[0] === type && args[1] === handler && args[2] === capture), type);
});

test("backtick is no longer a gallery shortcut", async () => {
    const { calls, key, input } = setup();
    assert.equal(key("`", undefined, { code: "Backquote" }).defaultPrevented, false);
    assert.equal(key("~", undefined, { code: "Backquote", shiftKey: true }).defaultPrevented, false);
    assert.equal(key("`", input, { code: "Backquote" }).defaultPrevented, false);
    await flush();
    assert.deepEqual(calls, []);
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
    loadGalleryScripts({ window, Element: class { }, document });
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

for (const review of ["false", "true"]) {
    test(`viewer forwards rating keys in review=${review} and ignores editing and modified keys`, async () => {
        let keydown;
        const document = {
            body: { style: {} }, activeElement: null,
            addEventListener(name, handler) { if (name === "keydown") keydown = handler; },
            removeEventListener() {}
        };
        const window = {};
        loadGalleryScripts({
            window, document, Element: class {}
        });
        const calls = [];
        const element = { dataset: { review, keys: review === "true" ? "1 2 3 4 5 t c" : "0 1 2 3 4 5 t c" }, focus() {}, querySelectorAll() { return []; }, querySelector() { return this.modalOpen ? {} : null; } };
        window.galleryViewer.attach(element, { invokeMethodAsync: (...args) => { calls.push(args); return Promise.resolve(); } });
        const press = async (key, extra = {}) => {
            const event = { key, target: { closest() { return null; } }, defaultPrevented: false,
                preventDefault() { this.defaultPrevented = true; }, ...extra };
            await keydown(event);
            return event.defaultPrevented;
        };
        for (const key of ["1", "2", "3", "4", "5"]) assert.equal(await press(key), true);
        for (const extra of [{ ctrlKey: true }, { altKey: true }, { metaKey: true }, { repeat: true },
            { target: { closest() { return {}; } } }]) assert.equal(await press("3", extra), false);
        assert.equal(await press("0"), review !== "true");
        assert.equal(await press("k"), review === "true");
        assert.equal(await press("s"), review === "true");
        assert.deepEqual(calls.slice(0, 5), ["1", "2", "3", "4", "5"].map(key => ["HandleKey", key]));
        assert.equal(await press("T"), true);
        assert.equal(await press("C"), true);
        assert.deepEqual(calls.slice(-2), [["HandleKey", "t"], ["HandleKey", "c"]]);
        const callCount = calls.length;
        element.modalOpen = true;
        for (const key of ["Escape", "Tab", "ArrowRight", "1", "f", "d", "t", "c"])
            assert.equal(await press(key), false);
        assert.equal(calls.length, callCount);
        window.galleryViewer.detach(element);
    });
}

test("quick edit focuses its input and returns focus when dismissed", () => {
    let focused = "viewer";
    const previous = { isConnected: true, focus() { focused = "viewer"; } };
    const document = { activeElement: previous, addEventListener() {} };
    const window = {};
    loadGalleryScripts({
        window, document, Element: class {}
    });
    const dialog = {
        addEventListener() {}, removeEventListener() {},
        showModal() { this.open = true; }, close() { this.open = false; },
        querySelector() { return { focus() { focused = "input"; } }; }
    };
    window.galleryViewer.openQuickEdit(dialog, "edit");
    assert.equal(dialog.open, true);
    assert.equal(focused, "input");
    window.galleryViewer.closeQuickEdit("edit");
    assert.equal(dialog.open, false);
    assert.equal(focused, "viewer");
});

test("gallerySecurity recognizes panic hotkeys and triggers panic lock", () => {
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
    loadGalleryScripts({
        window, Element: class { }, document: { addEventListener() { } }, URL: window.URL, setInterval, clearInterval, Date
    });

    const dotNetRef = {
        invokeMethodAsync: (...args) => { calls.push(args); return Promise.resolve(); }
    };
    window.gallerySecurity.attach(dotNetRef, 15, doc);
    try {
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
    } finally {
        window.gallerySecurity.detach();
    }
});

test("gallerySecurity purgeAllMedia clears tracked blobs and DOM images/videos", () => {
    const revoked = [];
    const window = {
        URL: { revokeObjectURL: url => revoked.push(url) }
    };
    loadGalleryScripts({
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

test("gallerySecurity idle auto-lock triggers after configured inactivity", () => {
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
    let idleCheck;
    let timerCleared = false;
    let now = 0;
    const clock = { now: () => now };
    loadGalleryScripts({
        window, Element: class { }, document: { addEventListener() { } }, URL: window.URL,
        setInterval: callback => { idleCheck = callback; return 1; },
        clearInterval: id => { assert.equal(id, 1); timerCleared = true; }, Date: clock
    });

    const dotNetRef = {
        invokeMethodAsync: (...args) => { calls.push(args); return Promise.resolve(); }
    };
    window.gallerySecurity.attach(dotNetRef, 0.0001, doc);
    try {
        now = 7;
        idleCheck();
        const visibilityHandler = listeners.get("visibilitychange");
        doc.visibilityState = "visible";
        visibilityHandler();

        assert.deepEqual(calls, [["TriggerAutoLock"], ["TriggerAutoLock"]]);
    } finally {
        window.gallerySecurity.detach();
    }
    assert.equal(timerCleared, true);
});


test("modal opening respects autofocus before other controls and restores its opener", () => {
    let focused;
    const opener = { isConnected: true, focus() { focused = "opener"; } };
    const document = { activeElement: opener, addEventListener() {} };
    const window = {};
    loadGalleryScripts({
        window, document, Element: class {}
    });
    const dialog = {
        addEventListener() {}, removeEventListener() {},
        showModal() { this.open = true; }, close() { this.open = false; },
        querySelector(selector) {
            return { focus() { focused = selector === "[autofocus]" ? "cancel" : "other"; } };
        }
    };
    window.galleryViewer.openQuickEdit(dialog, "removal");
    assert.equal(focused, "cancel");
    window.galleryViewer.closeQuickEdit("removal");
    assert.equal(focused, "opener");
    assert.equal(dialog.open, false);
});

test("modal restores an opener that lost focus while disabled by a busy render", () => {
    let focusin;
    let focused = false;
    const body = {};
    const document = {
        body, activeElement: body,
        addEventListener(name, listener) { if (name === "focusin") focusin = listener; }
    };
    const window = {};
    loadGalleryScripts({
        window, document, Element: class {}
    });
    const opener = { isConnected: true, matches() { return true; }, focus() { focused = true; } };
    focusin({ target: opener });
    const dialog = { addEventListener() {}, removeEventListener() {}, showModal() {}, close() {}, querySelector() { return null; } };
    window.galleryViewer.openQuickEdit(dialog, "details");
    window.galleryViewer.closeQuickEdit("details");
    assert.equal(focused, true);
});


test("Escape leaves native dialogs open until the save/discard decision closes them", () => {
    const document = { activeElement: null, body: {}, addEventListener() {}, querySelector() { return null; } };
    const window = {};
    loadGalleryScripts({
        window, document, Element: class {}
    });
    const makeDialog = () => {
        const target = new EventTarget();
        target.showModal = function () { this.open = true; };
        target.close = function () { this.open = false; };
        target.querySelector = () => null;
        return target;
    };
    const details = makeDialog();
    const confirmation = makeDialog();
    window.galleryViewer.openQuickEdit(details, "details");
    const escapeDetails = new Event("cancel", { cancelable: true });
    details.dispatchEvent(escapeDetails);
    assert.equal(escapeDetails.defaultPrevented, true);
    assert.equal(details.open, true);
    window.galleryViewer.openQuickEdit(confirmation, "confirmation");
    const escapeConfirmation = new Event("cancel", { cancelable: true });
    confirmation.dispatchEvent(escapeConfirmation);
    assert.equal(escapeConfirmation.defaultPrevented, true);
    window.galleryViewer.closeQuickEdit("confirmation");
    assert.equal(details.open, true);
    assert.equal(confirmation.open, false);
    window.galleryViewer.closeQuickEdit("details");
    const afterClose = new Event("cancel", { cancelable: true });
    details.dispatchEvent(afterClose);
    assert.equal(afterClose.defaultPrevented, false);
});

test("vertical card navigation uses grid columns through collection wrappers", () => {
    const { doc, grid, card, key, hover } = setup();
    grid.style.gridTemplateColumns = "100px 100px 100px";
    const cards = Array.from({ length: 6 }, (_, index) => {
        const element = card(String(index));
        const wrapper = grid.appendChild(doc.createElement("div"));
        wrapper.append(element);
        return element;
    });
    cards[0].querySelector("button").focus();
    hover(cards[5]);
    assert.equal(key("ArrowDown").defaultPrevented, true);
    assert.equal(doc.activeElement, cards[3].querySelector("button"));
});
