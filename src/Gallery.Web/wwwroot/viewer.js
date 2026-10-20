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
    keys: ["0", "1", "2", "3", "4", "5", "f", "d", "x", "escape", "arrowleft", "arrowright", "arrowup", "arrowdown"],
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
            const key = (event.key ?? "").toLowerCase();
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
            if (key === "escape" ? doc.querySelector(".bulk-bar") === null : !id) return;
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
