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
    focusCollectionNavigation: function (open) {
        document.querySelector(open ? ".collection-nav-open .workspace-navigation button" : ".collection-navigation-toggle")?.focus();
    },
    quickEditDialogs: new Map(),
    openQuickEdit: function (dialog, key) {
        const previousFocus = document.activeElement && document.activeElement !== document.body
            ? document.activeElement : window.galleryFocus.lastElement;
        // Cancel must be prevented synchronously, before the server handles Escape.
        // Otherwise a notes confirmation can leave its underlying details dialog hidden.
        const preventNativeClose = event => event.preventDefault();
        dialog.addEventListener("cancel", preventNativeClose);
        window.galleryViewer.quickEditDialogs.set(key, { dialog, previousFocus, preventNativeClose });
        dialog.showModal();
        (dialog.querySelector("[autofocus]") ?? dialog.querySelector(".advanced-select-value") ?? dialog.querySelector("button:not(:disabled), input:not(:disabled), textarea:not(:disabled), select:not(:disabled)"))?.focus();
    },
    closeQuickEdit: function (key) {
        const entry = window.galleryViewer.quickEditDialogs.get(key);
        if (!entry) return;
        window.galleryViewer.quickEditDialogs.delete(key);
        entry.dialog.removeEventListener("cancel", entry.preventNativeClose);
        entry.dialog.close();
        if (entry.previousFocus?.isConnected) entry.previousFocus.focus();
        else document.querySelector?.(".collection-page-heading h2")?.focus();
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
            // Native modal dialogs own focus, Tab and Escape until they close.
            if (element.querySelector?.("dialog[open]") || event.target?.closest?.("[data-advanced-select]")) return;
            const key = event.key.toLowerCase();
            const reviewKeys = element.dataset.review === "true" ? ["k", "s"] : [];
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
