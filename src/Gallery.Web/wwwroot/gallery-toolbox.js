// Floating gallery toolbox: drag by its handle (or arrow keys) and keep it inside the viewport.
window.galleryToolbox = {
    attached: new Map(),
    attach: function (element, reference, x, y) {
        if (!element) return;
        window.galleryToolbox.detach(element);
        const margin = 8;
        const handle = element.querySelector(".gallery-toolbox-handle");
        let placed = typeof x === "number" && typeof y === "number";
        const clamp = (left, top) => {
            const width = element.offsetWidth || 0, height = element.querySelector(".gallery-toolbox-bar")?.offsetHeight || element.offsetHeight || 0;
            return [
                Math.round(Math.min(Math.max(margin, left), Math.max(margin, window.innerWidth - width - margin))),
                Math.round(Math.min(Math.max(margin, top), Math.max(margin, window.innerHeight - height - margin)))
            ];
        };
        const apply = (left, top) => {
            const [clampedLeft, clampedTop] = clamp(left, top);
            element.style.left = `${clampedLeft}px`;
            element.style.top = `${clampedTop}px`;
        };
        const placeDefault = () => {
            const main = document.querySelector(".workspace-body") ?? document.querySelector(".main-content");
            const rect = main?.getBoundingClientRect();
            apply((rect?.left ?? 0) + 16, Math.max(0, rect?.top ?? 0) + 16);
        };
        if (placed) apply(x, y); else placeDefault();
        const save = () => {
            const left = parseFloat(element.style.left) || 0, top = parseFloat(element.style.top) || 0;
            placed = true;
            reference.invokeMethodAsync("ToolboxMoved", left, top).catch(error => console.error("Toolbox position was not saved", error));
        };
        let drag = null;
        const onDown = event => {
            if (event.button !== 0) return;
            drag = { id: event.pointerId, dx: event.clientX - element.offsetLeft, dy: event.clientY - element.offsetTop };
            handle.setPointerCapture?.(event.pointerId);
            element.classList.add("is-dragging");
            event.preventDefault();
        };
        const onMove = event => {
            if (!drag || event.pointerId !== drag.id) return;
            apply(event.clientX - drag.dx, event.clientY - drag.dy);
        };
        const onUp = event => {
            if (!drag || event.pointerId !== drag.id) return;
            drag = null;
            element.classList.remove("is-dragging");
            save();
        };
        const onKey = event => {
            const step = event.shiftKey ? 64 : 16;
            const delta = { ArrowLeft: [-step, 0], ArrowRight: [step, 0], ArrowUp: [0, -step], ArrowDown: [0, step] }[event.key];
            if (!delta) return;
            event.preventDefault();
            event.stopPropagation();
            apply(element.offsetLeft + delta[0], element.offsetTop + delta[1]);
            save();
        };
        const onResize = () => {
            if (placed) apply(element.offsetLeft, element.offsetTop); else placeDefault();
        };
        const reset = () => {
            placed = false;
            placeDefault();
            reference.invokeMethodAsync("ToolboxReset").catch(error => console.error("Toolbox position was not reset", error));
        };
        handle?.addEventListener("pointerdown", onDown);
        handle?.addEventListener("pointermove", onMove);
        handle?.addEventListener("pointerup", onUp);
        handle?.addEventListener("pointercancel", onUp);
        handle?.addEventListener("keydown", onKey);
        handle?.addEventListener("dblclick", reset);
        window.addEventListener("resize", onResize);
        const body = document.querySelector(".workspace-body");
        const observer = typeof ResizeObserver === "function" && body ? new ResizeObserver(onResize) : null;
        observer?.observe(body);
        observer?.observe(element);
        window.galleryToolbox.attached.set(element, { reset, detach: () => {
            handle?.removeEventListener("pointerdown", onDown);
            handle?.removeEventListener("pointermove", onMove);
            handle?.removeEventListener("pointerup", onUp);
            handle?.removeEventListener("pointercancel", onUp);
            handle?.removeEventListener("keydown", onKey);
            handle?.removeEventListener("dblclick", reset);
            window.removeEventListener("resize", onResize);
            observer?.disconnect();
        } });
    },
    reset: function (element) {
        window.galleryToolbox.attached.get(element)?.reset();
    },
    detach: function (element) {
        const attached = window.galleryToolbox.attached.get(element);
        if (!attached) return;
        attached.detach();
        window.galleryToolbox.attached.delete(element);
    }
};
