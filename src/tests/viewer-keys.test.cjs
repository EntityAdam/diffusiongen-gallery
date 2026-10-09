const assert = require("node:assert/strict");
const { readFileSync } = require("node:fs");
const path = require("node:path");
const { test } = require("node:test");
const vm = require("node:vm");

function setup({ dialog = false } = {}) {
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
        querySelector(selector) { return selector.startsWith("[role=dialog]") && dialog ? {} : null; },
        querySelectorAll() { return []; }
    };
    const window = {};
    vm.runInNewContext(readFileSync(path.join(__dirname, "..", "wwwroot", "viewer.js"), "utf8"), {
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

test("rating, favorite and deletion keys act on the hovered or focused card", async () => {
    const { doc, calls, card, key, hover } = setup();
    hover(card("hovered"));
    assert.equal(key("3", {}).defaultPrevented, true);
    doc.activeElement = card("focused");
    key("F", {});
    key("Delete", {}, { shiftKey: false });
    key("x", {}, { shiftKey: true });
    await flush();
    assert.deepEqual(calls, [["HandleGridKey", "3", "hovered", false], ["HandleGridKey", "f", "focused", false],
        ["HandleGridKey", "delete", "focused", false], ["HandleGridKey", "x", "focused", true]]);
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
