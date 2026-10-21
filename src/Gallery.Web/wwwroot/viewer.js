// Native image/file drops can navigate away from the live gallery circuit.
document.addEventListener("dragstart", event => {
    if (event.target instanceof Element && event.target.closest("img")) event.preventDefault();
}, true);
const preventImageDrop = event => {
    const types = Array.from(event.dataTransfer?.types ?? []);
    if (types.some(type => ["Files", "text/uri-list", "text/html"].includes(type))) {
        event.preventDefault();
        if (event.dataTransfer) event.dataTransfer.dropEffect = "none";
    }
};
document.addEventListener("dragover", preventImageDrop, true);
document.addEventListener("drop", preventImageDrop, true);

// Keyboard curation for the focused or hovered gallery card.
window.galleryFocus = {
    byId: function (id) { document.getElementById(id)?.focus(); }
};

window.galleryGrid = {
    keys: ["0", "1", "2", "3", "4", "5", "f", "d", "x", "`", "escape", "arrowleft", "arrowright", "arrowup", "arrowdown"],
    attach: function (reference, doc) {
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
        const onKey = event => {
            if (event.ctrlKey || event.altKey || event.metaKey || event.defaultPrevented) return;
            const key = event.code === "Backquote" ? "`" : (event.key ?? "").toLowerCase();
            if (!grid.keys.includes(key)) return;
            const editing = event.target?.closest?.("input,textarea,select,[contenteditable=true]");
            if (editing && !(editing.type === "checkbox" && cardOf(editing))) return;
            if (doc.querySelector("[role=dialog][aria-modal=true]")) return;
            const id = cardOf(doc.activeElement)?.dataset.imageId ?? hoveredId;
            if (key.startsWith("arrow")) {
                const cards = Array.from(doc.querySelectorAll(".image-card[data-image-id]"));
                if (cards.length === 0) return;
                const current = cards.findIndex(card => card.dataset.imageId === id);
                const columns = columnsOf(cards[0].parentElement);
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
        doc.addEventListener("keydown", onKey, true);
        grid.detach = () => {
            doc.removeEventListener("mouseover", onHover, true);
            doc.removeEventListener("keydown", onKey, true);
            grid.detach = () => { };
        };
    },
    detach: function () { }
};

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

// Security controls: panic purge and inactivity auto-lock
window.gallerySecurity = {
    activeUrls: new Set(),
    trackUrl: function (url) {
        if (url && typeof url === "string" && url.startsWith("blob:")) {
            window.gallerySecurity.activeUrls.add(url);
        }
        return url;
    },
    releaseUrl: function (url) {
        if (url) {
            window.gallerySecurity.activeUrls.delete(url);
            try { URL.revokeObjectURL(url); } catch (_) { }
        }
    },
    purgeAllMedia: function (doc) {
        doc = doc ?? (typeof document !== "undefined" ? document : null);
        for (const url of window.gallerySecurity.activeUrls) {
            try { URL.revokeObjectURL(url); } catch (_) { }
        }
        window.gallerySecurity.activeUrls.clear();

        if (doc) {
            try {
                const blankImage = "data:image/gif;base64,R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7";
                const images = doc.querySelectorAll?.("img") ?? [];
                for (const img of images) {
                    img.src = blankImage;
                    img.removeAttribute?.("srcset");
                }
                const videos = doc.querySelectorAll?.("video") ?? [];
                for (const video of videos) {
                    try { video.pause?.(); } catch (_) { }
                    video.src = "";
                    video.load?.();
                }
            } catch (_) { }

            try {
                if (doc.activeElement && typeof doc.activeElement.blur === "function") {
                    doc.activeElement.blur();
                }
            } catch (_) { }
        }
    },
    isPanicKey: function (event) {
        const key = (event.key ?? "").toLowerCase();
        if ((event.ctrlKey || event.altKey) && event.shiftKey && key === "l") return true;
        if (event.shiftKey && key === "escape") return true;
        return false;
    },
    panic: function (reference, doc) {
        window.gallerySecurity.purgeAllMedia(doc);
        if (reference && typeof reference.invokeMethodAsync === "function") {
            reference.invokeMethodAsync("TriggerPanicLock").catch(error => console.error("Panic lock failed", error));
        }
    },
    attach: function (reference, autoLockMinutes, doc) {
        const sec = window.gallerySecurity;
        sec.detach();
        doc = doc ?? (typeof document !== "undefined" ? document : null);
        if (!doc) return;
        let lastActivity = Date.now();
        let currentTimeoutMinutes = autoLockMinutes ?? 15;
        let referenceRef = reference;

        const onActivity = () => {
            lastActivity = Date.now();
        };

        const onKeyDown = event => {
            if (sec.isPanicKey(event)) {
                event.preventDefault();
                event.stopPropagation?.();
                sec.panic(referenceRef, doc);
                return;
            }
            onActivity();
        };

        const checkIdle = () => {
            if (currentTimeoutMinutes > 0) {
                const idleMs = Date.now() - lastActivity;
                if (idleMs >= currentTimeoutMinutes * 60 * 1000) {
                    sec.purgeAllMedia(doc);
                    if (referenceRef && typeof referenceRef.invokeMethodAsync === "function") {
                        referenceRef.invokeMethodAsync("TriggerAutoLock").catch(error => console.error("Auto lock failed", error));
                    }
                }
            }
        };

        const onVisibilityChange = () => {
            if (doc.visibilityState === "visible") {
                checkIdle();
            }
        };

        doc.addEventListener("keydown", onKeyDown, true);
        doc.addEventListener("mousemove", onActivity, true);
        doc.addEventListener("mousedown", onActivity, true);
        doc.addEventListener("touchstart", onActivity, true);
        doc.addEventListener("scroll", onActivity, true);
        doc.addEventListener("visibilitychange", onVisibilityChange, true);

        const scheduleInterval = typeof setInterval === "function" ? setInterval : (typeof window !== "undefined" && window.setInterval ? window.setInterval.bind(window) : () => 0);
        const cancelInterval = typeof clearInterval === "function" ? clearInterval : (typeof window !== "undefined" && window.clearInterval ? window.clearInterval.bind(window) : () => { });
        const timerId = scheduleInterval(checkIdle, 5000);

        sec.configureAutoLock = function (minutes) {
            currentTimeoutMinutes = minutes;
            lastActivity = Date.now();
        };

        sec.detach = () => {
            cancelInterval(timerId);
            doc.removeEventListener("keydown", onKeyDown, true);
            doc.removeEventListener("mousemove", onActivity, true);
            doc.removeEventListener("mousedown", onActivity, true);
            doc.removeEventListener("touchstart", onActivity, true);
            doc.removeEventListener("scroll", onActivity, true);
            doc.removeEventListener("visibilitychange", onVisibilityChange, true);
            sec.detach = () => { };
        };
    },
    detach: function () { }
};

window.galleryViewer = {
    celebrate: async function (element) {
        if (window.matchMedia("(prefers-reduced-motion: reduce)").matches) return;
        const burst = side => {
            if (!element.isConnected) return;
            const layer = document.createElement("div");
            layer.className = "review-confetti";
            layer.setAttribute("aria-hidden", "true");
            element.appendChild(layer);
            const colors = ["#75e0b1", "#ffd166", "#ef8bbd", "#8bbcf6"];
            const animations = [];
            for (let i = 0; i < 36; i++) {
                const piece = document.createElement("i");
                piece.style.background = colors[i % colors.length];
                piece.style[side] = "0";
                piece.style.top = "55%";
                layer.appendChild(piece);
                const x = (side === "left" ? 1 : -1) * (80 + Math.random() * element.clientWidth * .65);
                const y = -80 - Math.random() * 220;
                animations.push(piece.animate([
                    { transform: "translate(0, 0) rotate(0deg)", opacity: 1 },
                    { transform: `translate(${x * .6}px, ${y}px) rotate(180deg)`, opacity: 1, offset: .45 },
                    { transform: `translate(${x}px, 260px) rotate(${360 + Math.random() * 360}deg)`, opacity: 0 }
                ], { duration: 1100 + Math.random() * 400, easing: "ease-out" }).finished);
            }
            Promise.allSettled(animations).then(() => layer.remove());
        };
        burst("left");
        await new Promise(resolve => setTimeout(resolve, 80));
        burst("right");
    },
    createImageUrl: function (data) {
        const comma = data.indexOf(",");
        const binary = atob(data.slice(comma + 1));
        const bytes = Uint8Array.from(binary, character => character.charCodeAt(0));
        const url = URL.createObjectURL(new Blob([bytes], { type: data.slice(5, data.indexOf(";")) }));
        return window.gallerySecurity ? window.gallerySecurity.trackUrl(url) : url;
    },
    releaseImageUrls: function (urls) {
        urls.forEach(url => {
            if (window.gallerySecurity) window.gallerySecurity.releaseUrl(url);
            else URL.revokeObjectURL(url);
        });
    },
    attach: function (element, reference) {
        const previousFocus = document.activeElement;
        const previousOverflow = document.body.style.overflow;
        document.body.style.overflow = "hidden";
        element.focus();
        let queue = Promise.resolve();
        let active = true;
        const listener = async event => {
            if (event.ctrlKey || event.altKey || event.metaKey || event.repeat) return;
            const key = event.key.toLowerCase();
            const reviewKeys = element.dataset.review === "true" ? ["k", "s", "1", "2", "3", "4", "5"] : [];
            const extraKeys = (element.dataset.keys || "").split(" ").filter(Boolean);
            if (!["arrowleft", "arrowright", "d", "f", "o", "p", "escape", "tab", ...reviewKeys, ...extraKeys].includes(key)) return;
            if (key === "tab") {
                const buttons = Array.from(element.querySelectorAll("button:not(:disabled)"));
                const first = buttons[0], last = buttons[buttons.length - 1];
                if (event.shiftKey && (document.activeElement === first || document.activeElement === element)) {
                    event.preventDefault(); last?.focus();
                } else if (!event.shiftKey && document.activeElement === last) {
                    event.preventDefault(); first?.focus();
                }
                return;
            }
            if (event.target.closest("input,textarea,select,[contenteditable=true]")) return;
            event.preventDefault();
            const invocation = queue.then(() => active ? reference.invokeMethodAsync("HandleKey", key) : undefined);
            queue = invocation.catch(error => console.error("Viewer shortcut failed", error));
            await invocation;
        };
        document.addEventListener("keydown", listener);
        element.viewerCleanup = () => {
            element.viewerCleanup = null;
            active = false;
            document.removeEventListener("keydown", listener);
            document.body.style.overflow = previousOverflow;
            if (previousFocus?.isConnected) previousFocus.focus();
        };
    },
    detach: function (element) { element?.viewerCleanup?.(); }
};

// Decrypted media arrives as a .NET stream and becomes a revocable Blob URL (no plaintext server URL exists).
window.galleryMedia = {
    createStreamUrl: async function (streamRef, type) {
        const buffer = await streamRef.arrayBuffer();
        const url = URL.createObjectURL(new Blob([buffer], { type: type }));
        return window.gallerySecurity ? window.gallerySecurity.trackUrl(url) : url;
    },
    download: async function (streamRef, fileName, type) {
        const url = await window.galleryMedia.createStreamUrl(streamRef, type);
        try {
            const link = document.createElement("a");
            link.href = url;
            link.download = fileName;
            link.rel = "noopener";
            document.body.appendChild(link);
            link.click();
            link.remove();
        } finally {
            setTimeout(() => {
                if (window.gallerySecurity) window.gallerySecurity.releaseUrl(url);
                else URL.revokeObjectURL(url);
            }, 60000);
        }
    }
};
