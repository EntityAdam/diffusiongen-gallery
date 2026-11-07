// Render cards in batches, but decrypt previews only near the viewport.
// Old observers and queued callbacks are invalidated when filters or scopes change.
window.galleryScroll = (() => {
    const regions = new WeakMap();
    const detach = element => {
        const state = regions.get(element);
        if (!state) return;
        state.active = false;
        state.previews.disconnect();
        state.more.disconnect();
        regions.delete(element);
    };
    const sync = (element, sentinel, reference, revision, canLoadMore) => {
        if (typeof IntersectionObserver === "undefined") return; // The Load more button remains available.
        let state = regions.get(element);
        const reset = state && state.revision !== revision;
        if (reset) detach(element);
        if (!state || reset) {
            state = { active: true, revision, cards: new Set(), visible: new Set(), lastIds: "", canLoadMore,
                reportQueued: false, morePending: false, moreBlocked: false, queue: Promise.resolve() };
            const report = () => {
                if (state.reportQueued) return;
                state.reportQueued = true;
                queueMicrotask(() => {
                    state.reportQueued = false;
                    if (!state.active) return;
                    const ids = Array.from(state.cards).filter(card => state.visible.has(card)).map(card => card.dataset.imageId);
                    const signature = JSON.stringify(ids);
                    if (signature === state.lastIds) return;
                    state.lastIds = signature;
                    state.queue = state.queue.then(() => state.active
                        ? reference.invokeMethodAsync("UpdateViewportAsync", ids, revision) : undefined)
                        .catch(error => console.error("Viewport preview update failed", error));
                });
            };
            state.previews = new IntersectionObserver(entries => {
                if (!state.active) return;
                for (const entry of entries) {
                    if (!state.cards.has(entry.target)) continue;
                    if (entry.isIntersecting) state.visible.add(entry.target);
                    else state.visible.delete(entry.target);
                }
                report();
            }, { rootMargin: "200px 0px" });
            state.more = new IntersectionObserver(entries => {
                if (!state.active || !state.canLoadMore || state.morePending || state.moreBlocked
                    || !entries.some(entry => entry.isIntersecting)) return;
                state.morePending = true;
                reference.invokeMethodAsync("LoadMoreAsync", revision)
                    .catch(error => { state.moreBlocked = true; console.error("Loading more media failed", error); })
                    .finally(() => {
                        state.morePending = false;
                        if (state.active && state.canLoadMore && !state.moreBlocked) {
                            state.more.unobserve(sentinel);
                            state.more.observe(sentinel);
                        }
                    });
            }, { rootMargin: "600px 0px" });
            state.report = report;
            regions.set(element, state);
            if (reset) element.scrollIntoView({ block: "start" });
        }
        state.canLoadMore = canLoadMore;
        const cards = new Set(element.querySelectorAll(".image-card[data-image-id]"));
        for (const card of state.cards) {
            if (cards.has(card)) continue;
            state.previews.unobserve(card);
            state.visible.delete(card);
        }
        for (const card of cards) if (!state.cards.has(card)) state.previews.observe(card);
        state.cards = cards;
        state.report();
        if (!state.morePending) {
            state.more.unobserve(sentinel);
            if (canLoadMore && !state.moreBlocked) state.more.observe(sentinel);
        }
    };
    return { sync, detach };
})();
