const assert = require("node:assert/strict");
const { readFileSync } = require("node:fs");
const path = require("node:path");
const { test } = require("node:test");
const vm = require("node:vm");

function setup({ top = 100, height = 700 } = {}) {
    const calls = [], listeners = new Map(), documentListeners = new Map(), windowListeners = new Map();
    let focus = "trigger";
    const trigger = { focus() { focus = "trigger"; } };
    const input = { focus() { focus = "search"; }, getAttribute() { return null; } };
    const root = {
        addEventListener(name, handler) { listeners.set(name, handler); },
        removeEventListener(name) { listeners.delete(name); },
        getBoundingClientRect() { return { top, left: 20, bottom: top + 44, width: 318 }; },
        contains(target) { return target === input || target === trigger; },
        querySelector() { return trigger; }
    };
    const menu = {
        style: {}, scrollHeight: 338, open: false,
        matches() { return this.open; },
        showPopover() { this.open = true; }, hidePopover() { this.open = false; }
    };
    const document = {
        addEventListener(name, handler) { documentListeners.set(name, handler); },
        removeEventListener(name) { documentListeners.delete(name); },
        getElementById() { return null; }
    };
    const window = {
        innerHeight: height, innerWidth: 1000,
        addEventListener(name, handler) { windowListeners.set(name, handler); },
        removeEventListener(name) { windowListeners.delete(name); }
    };
    vm.runInNewContext(readFileSync(path.join(__dirname, "..", "Gallery.Web", "wwwroot", "advanced-select.js"), "utf8"), { window, document, console });
    const api = window.galleryAdvancedSelect;
    api.attach(root, menu, input, { invokeMethodAsync: async (...args) => calls.push(args) }, "select");
    const press = (key, target = input, extra = {}) => {
        const event = { key, target, defaultPrevented: false, stopped: false,
            preventDefault() { this.defaultPrevented = true; }, stopPropagation() { this.stopped = true; }, ...extra };
        listeners.get("keydown")(event);
        return event;
    };
    return { api, calls, root, menu, input, trigger, press, listeners, documentListeners, windowListeners, focused: () => focus };
}
const flush = () => new Promise(resolve => setImmediate(resolve));

test("menu positions below the trigger, takes search focus, and restores trigger focus", () => {
    const { api, menu, focused } = setup();
    api.sync("select", true, true, false);
    assert.equal(menu.open, true);
    assert.equal(menu.style.top, "152px");
    assert.equal(menu.style.width, "318px");
    assert.equal(focused(), "search");
    api.sync("select", false, false, true);
    assert.equal(menu.open, false);
    assert.equal(focused(), "trigger");
});

test("menu flips above the trigger near the viewport bottom", () => {
    const { api, menu } = setup({ top: 550, height: 650 });
    api.sync("select", true, false, false);
    assert.equal(menu.style.top, "204px");
});

test("keyboard navigation is serialized and prevents form submission and dialog dismissal", async () => {
    const { api, press, calls } = setup();
    api.sync("select", true, true, false);
    for (const key of ["ArrowDown", "ArrowUp", "Enter", "Escape"]) {
        const event = press(key);
        assert.equal(event.defaultPrevented, true);
        assert.equal(event.stopped, true);
    }
    assert.equal(press("x").defaultPrevented, false);
    assert.equal(press("Enter", undefined, { isComposing: true }).defaultPrevented, false);
    assert.equal(press("Enter", { closest() { return null; } }).defaultPrevented, false);
    assert.equal(press("k", undefined, { metaKey: true }).defaultPrevented, true);
    await flush();
    assert.deepEqual(calls, ["arrowdown", "arrowup", "enter", "escape", "focus"].map(key => ["HandleKey", key]));
    api.sync("select", false, false, false);
    assert.equal(press("Escape").defaultPrevented, false);
});

test("outside click and blur close the menu; detach removes listeners and cancels queued work", async () => {
    const { api, calls, input, menu, listeners, documentListeners, windowListeners } = setup();
    api.sync("select", true, true, false);
    documentListeners.get("pointerdown")({ target: input });
    listeners.get("focusout")({ relatedTarget: null });
    await flush();
    assert.equal(calls.length, 0);
    documentListeners.get("pointerdown")({ target: {} });
    await flush();
    assert.deepEqual(calls, [["CloseMenu"]]);
    listeners.get("focusout")({ relatedTarget: {} });
    api.detach("select");
    await flush();
    assert.equal(calls.length, 1);
    assert.equal(menu.open, false);
    assert.equal(listeners.size, 0);
    assert.equal(documentListeners.size, 0);
    assert.equal(windowListeners.size, 0);
    assert.equal(api.attached.size, 0);
});

test("renders preserve newer browser search text until an explicit reset", () => {
    const { api, input } = setup();
    input.value = "newer typing";
    api.sync("select", true, false, false, false);
    assert.equal(input.value, "newer typing");
    api.sync("select", true, true, false, true);
    assert.equal(input.value, "");
});
