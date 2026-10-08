const assert = require("node:assert/strict");
const { readFileSync } = require("node:fs");
const path = require("node:path");
const { test } = require("node:test");
const vm = require("node:vm");

function setup() {
    const listeners = new Map();
    class Element {
        constructor(image = false) { this.image = image; }
        closest(selector) { return selector === "img" && this.image ? this : null; }
    }
    vm.runInNewContext(readFileSync(path.join(__dirname, "..", "wwwroot", "viewer.js"), "utf8"), {
        window: {},
        Element,
        document: {
            addEventListener(name, handler, capture) {
                assert.equal(capture, true);
                listeners.set(name, handler);
            }
        }
    });
    function dispatch(name, target, types) {
        const event = {
            target,
            defaultPrevented: false,
            dataTransfer: types ? { types, dropEffect: "copy" } : null,
            preventDefault() { this.defaultPrevented = true; }
        };
        listeners.get(name)(event);
        return event;
    }
    return { Element, dispatch };
}

test("image dragging is canceled without blocking other draggable content", () => {
    const { Element, dispatch } = setup();
    assert.equal(dispatch("dragstart", new Element(true)).defaultPrevented, true);
    assert.equal(dispatch("dragstart", new Element()).defaultPrevented, false);
    assert.equal(dispatch("dragstart", {}).defaultPrevented, false);
});

test("file and browser-image drops cannot navigate the gallery", () => {
    const { Element, dispatch } = setup();
    for (const name of ["dragover", "drop"]) {
        for (const types of [["Files"], ["text/uri-list", "text/plain"], ["text/html"]]) {
            const event = dispatch(name, new Element(), types);
            assert.equal(event.defaultPrevented, true);
            assert.equal(event.dataTransfer.dropEffect, "none");
        }
    }
});

test("plain text editing and drag events without a payload remain available", () => {
    const { Element, dispatch } = setup();
    for (const name of ["dragover", "drop"]) {
        assert.equal(dispatch(name, new Element(), ["text/plain"]).defaultPrevented, false);
        assert.equal(dispatch(name, new Element()).defaultPrevented, false);
    }
});
