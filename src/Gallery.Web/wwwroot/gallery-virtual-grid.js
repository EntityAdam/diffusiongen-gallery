(() => {
    const grids = new WeakMap();
    const detachById = new Map();

    function attach(id, host, reference) {
        const scroller = host.closest(".workspace-body");
        const sizer = host.querySelector(".virtual-gallery-sizer");
        const canvas = host.querySelector(".virtual-gallery-canvas");
        const mounts = host.querySelector(".virtual-gallery-mounts");
        let config, frame, running, pending, stopped, focused, focusWithinGrid;
        let latestTop = 0;
        let measured = new Map();
        let lastGeometry;

        function geometry() {
            const width = sizer.getBoundingClientRect().width;
            const columns = getComputedStyle(sizer).gridTemplateColumns.split(/\s+/).filter(Boolean).length;
            const height = scroller.clientHeight;
            const origin = canvas.getBoundingClientRect().top - scroller.getBoundingClientRect().top
                - scroller.clientTop + scroller.scrollTop;
            const physicalRange = Math.max(0, canvas.offsetHeight - height);
            const logicalRange = Math.max(0, (config?.totalHeight ?? 0) - height);
            const scale = physicalRange > 0 && logicalRange > physicalRange + 1 ? logicalRange / physicalRange : 1;
            return { width, columns: Math.max(1, columns), height, origin, scale, logicalRange };
        }

        function place(view) {
            const physicalTop = scroller.scrollTop - view.origin;
            latestTop = Math.min(view.logicalRange, Math.max(0, physicalTop * view.scale));
            const translation = view.scale > 1 ? Math.max(0, physicalTop) - latestTop : 0;
            mounts.style.top = (config.offset + translation) + "px";
        }

        function schedule() {
            if (!stopped && !frame) frame = requestAnimationFrame(report);
        }

        function invokeLatest(payload) {
            if (running) { pending = payload; return; }
            lastGeometry = payload;
            const mountedRows = new Set(Array.from(mounts.children, row => Number(row.dataset.virtualRow)));
            for (const row of measured.keys()) {
                if (!mountedRows.has(row)) measured.delete(row);
            }
            for (const row of payload.rows) measured.set(row.row, row.height);
            running = reference.invokeMethodAsync("ViewportChanged", payload)
                .catch(reportError)
                .finally(() => {
                    running = null;
                    const next = pending;
                    pending = null;
                    if (!stopped && next?.revision === config?.revision) invokeLatest(next);
                });
        }

        function reportError(error) {
            if (!stopped && host.isConnected) console.error("Gallery viewport update failed", error);
        }

        function report() {
            frame = null;
            if (!config || stopped || !host.isConnected) return;
            const view = geometry();
            if (!view.width || !view.height) return;
            place(view);
            const rows = [];
            for (const row of mounts.children) {
                const index = Number(row.dataset.virtualRow);
                const height = row.getBoundingClientRect().height;
                if (height > 0 && Math.abs(height - (measured.get(index) ?? 0)) >= 0.5)
                    rows.push({ row: index, height });
            }

            const changed = !lastGeometry || lastGeometry.columns !== view.columns ||
                Math.abs(lastGeometry.width - view.width) >= 1 || lastGeometry.height !== view.height;
            const bottom = latestTop + Math.max(0, view.height - 0.01);
            const outside = config.totalHeight > 0 &&
                (latestTop < config.startMin || latestTop >= config.startMax ||
                    bottom < config.endMin || (bottom >= config.endMax && config.endMax < config.totalHeight));
            const measurements = rows.slice(0, 256);
            if (changed || outside || measurements.length > 0) {
                invokeLatest({ revision: config.revision, columns: view.columns, width: view.width,
                    height: view.height, top: latestTop, rows: measurements });
            } else pending = null;

            if (config.focusId && config.focusRequest !== focused) {
                const button = Array.from(host.querySelectorAll(".image-card")).find(card =>
                    card.dataset.imageId === config.focusId)?.querySelector(".image-open");
                if (button) {
                    focused = config.focusRequest;
                    button.focus({ preventScroll: true });
                    reference.invokeMethodAsync("FocusApplied", focused, config.focusId).catch(reportError);
                }
            } else if (!config.focusId) focused = null;

            if (focusWithinGrid && document.activeElement === document.body && !document.querySelector("[aria-modal=true]"))
                host.focus({ preventScroll: true });
        }

        function update(next) {
            const anchoredTop = next.preserveOffsetFrom == null ? next.scrollTo :
                next.scrollTo + latestTop - next.preserveOffsetFrom;
            if (config?.revision !== next.revision) {
                measured.clear();
                if (pending?.revision !== next.revision) pending = null;
            }
            config = next;
            if (anchoredTop != null) {
                const view = geometry();
                const target = Math.min(view.logicalRange, Math.max(0, anchoredTop));
                scroller.scrollTop = target <= 0 ? 0 : view.origin + target / view.scale;
            }
            place(geometry());
            schedule();
        }

        function arrow(key, id) {
            const view = geometry();
            place(view);
            reference.invokeMethodAsync("MoveFocus", key, id, latestTop).catch(reportError);
        }

        function scroll() {
            if (config) place(geometry());
            schedule();
        }

        // A tall canvas uses compressed scroll coordinates. Preserve ordinary wheel movement in logical pixels.
        function wheel(event) {
            const view = geometry();
            if (view.scale <= 1 || event.ctrlKey || event.shiftKey || event.defaultPrevented) return;
            const delta = event.deltaY * (event.deltaMode === 1 ? 16 : event.deltaMode === 2 ? view.height : 1);
            const prompt = event.target.closest(".virtual-gallery-header p");
            if (!delta || prompt && prompt.scrollHeight > prompt.clientHeight) return;
            event.preventDefault();
            const distance = delta / view.scale;
            scroller.scrollTop += Math.sign(distance) * Math.max(1, Math.abs(distance));
        }

        function focusIn(event) {
            focusWithinGrid = host.contains(event.target);
        }

        const resize = new ResizeObserver(schedule);
        resize.observe(scroller);
        resize.observe(sizer);
        scroller.addEventListener("scroll", scroll, { passive: true });
        scroller.addEventListener("wheel", wheel, { passive: false });
        document.addEventListener("focusin", focusIn);

        grids.set(host, { update, arrow });
        detachById.set(id, () => {
            stopped = true;
            resize.disconnect();
            if (frame) cancelAnimationFrame(frame);
            scroller.removeEventListener("scroll", scroll);
            scroller.removeEventListener("wheel", wheel);
            document.removeEventListener("focusin", focusIn);
            grids.delete(host);
        });
    }

    window.galleryVirtualGrid = {
        attach,
        update: (host, config) => grids.get(host)?.update(config),
        arrow: (host, key, id) => grids.get(host)?.arrow(key, id),
        detach(id) {
            detachById.get(id)?.();
            detachById.delete(id);
        }
    };
})();
