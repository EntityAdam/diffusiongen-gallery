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

window.galleryFocus = {
    lastElement: null,
    byId: function (id) { document.getElementById(id)?.focus(); }
};

// A busy render can disable the opener before an asynchronous dialog is shown.
// Remember focus before that render so closing still returns to the same control.
document.addEventListener("focusin", event => {
    if (event.target?.matches?.("button,a[href],input,textarea,select,[tabindex]"))
        window.galleryFocus.lastElement = event.target;
}, true);
