(() => {
    const inputs = new WeakMap();

    function detach(element) {
        const state = inputs.get(element);
        if (!state) return;
        state.disposed = true;
        clearTimeout(state.timer);
        state.observer.disconnect();
        for (const [event, handler] of Object.entries(state.handlers)) element.removeEventListener(event, handler);
        inputs.delete(element);
    }

    window.galleryFilterInput = {
        attach(element, target, value, revision, delay) {
            detach(element);
            const state = { revision, committed: value, timer: null, ready: false, inFlight: false,
                composing: false, disposed: false, retryBlocked: false };
            element.value = value;
            inputs.set(element, state);

            const send = () => {
                if (state.disposed || !state.ready || state.inFlight || state.composing || state.retryBlocked || element.disabled) return;
                state.ready = false;
                const submitted = element.value;
                if (submitted === state.committed) return;
                const submittedRevision = state.revision;
                state.inFlight = true;
                target.invokeMethodAsync("CommitAsync", submitted, submittedRevision)
                    .then(accepted => {
                        if (state.disposed || state.revision !== submittedRevision) return;
                        if (accepted) state.committed = submitted;
                        else { state.retryBlocked = true; state.ready = true; }
                    })
                    .catch(() => {
                        // A disconnected or disposed circuit must not trigger an automatic retry loop.
                        if (!state.disposed && state.revision === submittedRevision) state.retryBlocked = true;
                    })
                    .finally(() => { state.inFlight = false; send(); });
            };
            const schedule = () => {
                clearTimeout(state.timer);
                state.ready = false;
                state.retryBlocked = false;
                if (state.composing) return;
                state.timer = setTimeout(() => { state.ready = true; send(); }, delay);
            };
            const flush = () => {
                if (state.composing) return;
                clearTimeout(state.timer);
                state.ready = true;
                state.retryBlocked = false;
                send();
            };
            state.handlers = {
                input: schedule,
                compositionstart: () => { state.composing = true; state.ready = false; clearTimeout(state.timer); },
                compositionend: () => { state.composing = false; schedule(); },
                blur: flush,
                keydown: event => {
                    if (event.key === "Enter" && !event.isComposing && !state.composing) {
                        event.preventDefault();
                        flush();
                    }
                }
            };
            for (const [event, handler] of Object.entries(state.handlers)) element.addEventListener(event, handler);
            // A pending edit may become ready while the server temporarily disables filters.
            state.observer = new MutationObserver(() => {
                if (!element.disabled) { state.retryBlocked = false; send(); }
            });
            state.observer.observe(element, { attributes: true, attributeFilter: ["disabled"] });
        },
        sync(element, value, revision) {
            const state = inputs.get(element);
            if (!state || revision < state.revision) return;
            clearTimeout(state.timer);
            state.revision = revision;
            state.committed = value;
            state.ready = false;
            state.retryBlocked = false;
            element.value = value;
        },
        detach
    };
})();
