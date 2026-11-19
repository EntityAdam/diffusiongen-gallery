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
