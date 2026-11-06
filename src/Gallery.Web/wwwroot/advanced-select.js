window.galleryAdvancedSelect = {
    attached: new Map(),
    attach(root, menu, input, reference, key) {
        const api = window.galleryAdvancedSelect;
        api.detach(key);
        let active = true;
        let queue = Promise.resolve();
        const entry = { root, menu, input, open: false };
        const invoke = (method, ...args) => {
            const invocation = queue.then(() => active ? reference.invokeMethodAsync(method, ...args) : undefined);
            queue = invocation.catch(error => console.error("Advanced select operation failed", error));
            return invocation;
        };
        const position = () => {
            if (!entry.open) return;
            const bounds = root.getBoundingClientRect();
            const gap = 8, margin = 8;
            const below = window.innerHeight - bounds.bottom - gap - margin;
            const above = bounds.top - gap - margin;
            const up = below < Math.min(310, menu.scrollHeight) && above > below;
            const available = Math.max(100, up ? above : below);
            const width = Math.min(bounds.width, window.innerWidth - margin * 2);
            menu.style.width = `${width}px`;
            menu.style.maxHeight = `${available}px`;
            menu.style.left = `${Math.max(margin, Math.min(bounds.left, window.innerWidth - width - margin))}px`;
            menu.style.top = `${up ? Math.max(margin, bounds.top - Math.min(menu.scrollHeight, available) - gap) : bounds.bottom + gap}px`;
        };
        const close = () => { if (entry.open) invoke("CloseMenu"); };
        const onOutside = event => { if (!root.contains(event.target)) close(); };
        // Removing a focused clear/chip button produces a null relatedTarget before sync restores focus.
        const onFocusOut = event => { if (event.relatedTarget && !root.contains(event.relatedTarget)) close(); };
        const onKey = event => {
            if (event.isComposing || event.keyCode === 229) return;
            const keyName = event.key.toLowerCase();
            if ((event.metaKey || event.ctrlKey) && !event.altKey && keyName === "k") {
                event.preventDefault(); event.stopPropagation(); invoke("HandleKey", "focus"); return;
            }
            if (event.metaKey || event.ctrlKey || event.altKey) return;
            const isTrigger = event.target.closest?.("button[aria-expanded]") != null;
            const navigation = ["arrowdown", "arrowup"].includes(keyName);
            const searchKey = event.target === input && ["enter", "home", "end"].includes(keyName);
            if ((keyName === "escape" && entry.open) || navigation || searchKey || (isTrigger && keyName === "enter")) {
                event.preventDefault();
                event.stopPropagation();
                invoke("HandleKey", keyName);
            }
        };
        root.addEventListener("keydown", onKey, true);
        root.addEventListener("focusout", onFocusOut);
        document.addEventListener("pointerdown", onOutside, true);
        window.addEventListener("resize", position);
        document.addEventListener("scroll", position, true);
        const observer = typeof ResizeObserver === "function" ? new ResizeObserver(position) : null;
        observer?.observe(root);
        entry.position = position;
        entry.detach = () => {
            active = false;
            root.removeEventListener("keydown", onKey, true);
            root.removeEventListener("focusout", onFocusOut);
            document.removeEventListener("pointerdown", onOutside, true);
            window.removeEventListener("resize", position);
            document.removeEventListener("scroll", position, true);
            observer?.disconnect();
            if (menu.matches(":popover-open")) menu.hidePopover();
        };
        api.attached.set(key, entry);
    },
    sync(key, open, focusSearch, focusTrigger, resetSearch) {
        const entry = window.galleryAdvancedSelect.attached.get(key);
        if (!entry) return;
        entry.open = open;
        // Blazor keeps the rendered value constant; only deliberate resets replace browser text.
        if (resetSearch) entry.input.value = "";
        if (open) {
            if (!entry.menu.matches(":popover-open")) entry.menu.showPopover();
            entry.position();
            if (focusSearch) entry.input.focus();
            const activeId = entry.input.getAttribute("aria-activedescendant");
            if (activeId) document.getElementById(activeId)?.scrollIntoView({ block: "nearest" });
        } else {
            if (entry.menu.matches(":popover-open")) entry.menu.hidePopover();
            if (focusTrigger) entry.root.querySelector(".advanced-select-value")?.focus();
        }
    },
    detach(key) {
        const entry = window.galleryAdvancedSelect.attached.get(key);
        entry?.detach();
        window.galleryAdvancedSelect.attached.delete(key);
    }
};
