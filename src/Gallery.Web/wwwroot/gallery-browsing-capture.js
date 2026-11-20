(() => {
    const captures = new Map();
    const minimumInterval = 250;
    const timeout = 5000;

    function attach(id, marker, reference, infinite) {
        const root = marker.closest(".gallery-layout");
        const scroller = root.closest(".workspace-body");
        let frame, timer, stopped, sending;
        let sample = { start: performance.now(), initial: true };
        let lastSent = -Infinity;
        let lastMounted = -1;

        function schedule() {
            if (!stopped && !frame) frame = requestAnimationFrame(measure);
        }

        function begin(previousCards) {
            const now = performance.now();
            if (sample?.initial && now - sample.start < timeout) return;
            if (now - lastSent < minimumInterval) return;
            sample = { start: now, previousCards };
            schedule();
        }

        function measure(now) {
            frame = null;
            if (stopped || !root.isConnected) return;
            const cards = Array.from(root.querySelectorAll(".image-card"));
            if (!sample) {
                if (lastMounted !== cards.length && now - lastSent >= minimumInterval)
                    send(cards.length, null, null, 0, false, now);
                return;
            }
            const bounds = scroller.getBoundingClientRect();
            const visible = cards.filter(card => {
                const rect = card.querySelector(".image-open").getBoundingClientRect();
                return rect.bottom > bounds.top && rect.top < bounds.bottom;
            });
            const waitForNewCards = sample.previousCards?.some(card => card.isConnected);
            let pending = 0, failed = 0, loaded = 0;
            if (!waitForNewCards) {
                for (const card of visible) {
                    const preview = card.querySelector(".image-open");
                    const image = preview.querySelector("img");
                    if (image?.complete && image.naturalWidth > 0) loaded++;
                    else if (image?.complete || preview.querySelector('[aria-busy="false"]')) failed++;
                    else pending++;
                }
            }

            const elapsed = now - sample.start;
            if (loaded > 0) sample.first ??= elapsed;
            const ready = visible.length > 0 && !waitForNewCards && pending === 0;
            if (ready || elapsed >= timeout) {
                const hasGrid = root.querySelector(".image-grid, [data-virtual-gallery]");
                const unavailable = waitForNewCards || visible.length === 0 ? Number(!!hasGrid) : failed + pending;
                if (now - lastSent >= minimumInterval &&
                    send(cards.length, sample.first ?? null, ready && !failed ? elapsed : null, unavailable, sample.initial === true, now))
                    sample = null;
            }
            if (sample) {
                clearTimeout(timer);
                timer = setTimeout(schedule, 100);
            }
        }

        function send(mounted, first, ready, unavailable, initial, now) {
            if (sending) return false;
            lastSent = now;
            lastMounted = mounted;
            sending = reference.invokeMethodAsync("Record", mounted, first, ready, unavailable, initial)
                .catch(() => {})
                .finally(() => { sending = null; });
            return true;
        }

        function pageNavigation(event) {
            if (event.target.closest("[data-gallery-page-navigation]"))
                begin(Array.from(root.querySelectorAll(".image-card")));
        }

        let lastScroll, scrollIdle;
        function scroll() {
            lastScroll = performance.now();
            clearTimeout(scrollIdle);
            scrollIdle = setTimeout(() => {
                begin();
                if (sample && !sample.initial) sample.start = lastScroll;
                schedule();
            }, 120);
        }

        const observer = new MutationObserver(schedule);
        observer.observe(root, { childList: true, subtree: true });
        root.addEventListener("load", schedule, true);
        root.addEventListener("error", schedule, true);
        root.addEventListener("click", pageNavigation, true);
        if (infinite) scroller.addEventListener("scroll", scroll, { passive: true });
        schedule();

        captures.set(id, () => {
            stopped = true;
            observer.disconnect();
            cancelAnimationFrame(frame);
            clearTimeout(timer);
            clearTimeout(scrollIdle);
            root.removeEventListener("load", schedule, true);
            root.removeEventListener("error", schedule, true);
            root.removeEventListener("click", pageNavigation, true);
            scroller.removeEventListener("scroll", scroll);
        });
    }

    window.galleryBrowsingCapture = {
        attach,
        detach(id) {
            captures.get(id)?.();
            captures.delete(id);
        }
    };
})();
