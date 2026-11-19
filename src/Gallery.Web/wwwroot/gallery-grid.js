window.galleryGrid = {
    keys: ["0", "1", "2", "3", "4", "5", "f", "d", "x", "`", "escape", "arrowleft", "arrowright", "arrowup", "arrowdown"],
    attach: function (reference, doc, owner) {
        const grid = window.galleryGrid;
        grid.detach();
        doc = doc ?? document;
        let hoveredId = null;
        let queue = Promise.resolve();
        const cardOf = target => typeof target?.closest === "function" ? target.closest(".image-card[data-image-id]") : null;
        const columnsOf = element => {
            const style = element && typeof window.getComputedStyle === "function" ? window.getComputedStyle(element) : null;
            return Math.max(1, (style?.gridTemplateColumns ?? "").split(" ").filter(Boolean).length);
        };
        const onHover = event => { hoveredId = cardOf(event.target)?.dataset.imageId ?? null; };
        const onLeave = event => {
            if (cardOf(event.target)?.dataset.imageId === hoveredId)
                hoveredId = cardOf(event.relatedTarget)?.dataset.imageId ?? null;
        };
        const onKey = event => {
            if (event.ctrlKey || event.altKey || event.metaKey || event.defaultPrevented) return;
            if (event.target?.closest?.("[data-advanced-select]")) return;
            const key = event.code === "Backquote" ? "`" : (event.key ?? "").toLowerCase();
            if (!grid.keys.includes(key)) return;
            const editing = event.target?.closest?.("input,textarea,select,[contenteditable=true]");
            if (editing && !(editing.type === "checkbox" && cardOf(editing))) return;
            if (doc.querySelector("[role=dialog][aria-modal=true], dialog[open]")) return;
            const focusedId = cardOf(doc.activeElement)?.dataset.imageId;
            // Pointer curation follows the hovered card even when a previous card retains focus.
            const id = hoveredId ?? focusedId;
            if (key.startsWith("arrow")) {
                const cards = Array.from(doc.querySelectorAll(".image-card[data-image-id]"));
                if (cards.length === 0) return;
                // Arrow navigation continues from keyboard focus while the pointer stays parked.
                const current = cards.findIndex(card => card.dataset.imageId === (focusedId ?? hoveredId));
                const columns = columnsOf(cards[0].closest(".image-grid") ?? cards[0].parentElement);
                const step = { arrowleft: -1, arrowright: 1, arrowup: -columns, arrowdown: columns }[key];
                const next = cards[current < 0 ? 0 : Math.min(cards.length - 1, Math.max(0, current + step))];
                event.preventDefault();
                next.querySelector(".image-open")?.focus();
                next.scrollIntoView?.({ block: "nearest" });
                return;
            }
            if (key !== "`" && (key === "escape" ? doc.querySelector("[data-has-selection=true]") === null : !id)) return;
            event.preventDefault();
            const invocation = queue.then(() => reference.invokeMethodAsync("HandleGridKey", key, id ?? "", event.shiftKey === true));
            queue = invocation.catch(error => console.error("Gallery shortcut failed", error));
        };
        doc.addEventListener("mouseover", onHover, true);
        doc.addEventListener("mouseout", onLeave, true);
        doc.addEventListener("keydown", onKey, true);
        grid.detach = (requestOwner) => {
            if (requestOwner !== undefined && requestOwner !== owner) return;
            doc.removeEventListener("mouseover", onHover, true);
            doc.removeEventListener("mouseout", onLeave, true);
            doc.removeEventListener("keydown", onKey, true);
            grid.detach = () => { };
        };
    },
    detach: function () { }
};
