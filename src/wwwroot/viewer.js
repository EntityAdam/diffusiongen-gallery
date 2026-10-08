window.galleryViewer = {
    createImageUrl: function (data) {
        const comma = data.indexOf(",");
        const binary = atob(data.slice(comma + 1));
        const bytes = Uint8Array.from(binary, character => character.charCodeAt(0));
        return URL.createObjectURL(new Blob([bytes], { type: data.slice(5, data.indexOf(";")) }));
    },
    releaseImageUrls: function (urls) { urls.forEach(url => URL.revokeObjectURL(url)); },
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
            if (!["arrowleft", "arrowright", "d", "f", "o", "p", "escape", "tab", ...reviewKeys].includes(key)) return;
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
