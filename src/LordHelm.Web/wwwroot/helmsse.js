// Blazor <-> EventSource interop for the live log-tail pane.
// Usage from Razor:
//   const ref = await JS.InvokeAsync('helmSse.subscribe', subprocessId, dotNetRef, 'OnSseLine');
//   // later: await JS.InvokeVoidAsync('helmSse.unsubscribe', ref);
// The callback fires once per SSE line with the raw string (already JSON-encoded by the server).
// It uses invokeMethodAsync (fire-and-forget) so the JS event loop is never blocked.

window.helmSse = (function () {
    const subs = new Map();
    let nextId = 1;

    function subscribe(subprocessId, dotnetRef, callbackMethodName) {
        const id = nextId++;
        const url = '/logs/' + encodeURIComponent(subprocessId);
        const source = new EventSource(url);

        // "log" events are the normal payloads; "hello" tells us the stream is open.
        let buffer = [];
        let flushPending = false;
        function schedule() {
            if (flushPending) return;
            flushPending = true;
            setTimeout(() => {
                const drained = buffer;
                buffer = [];
                flushPending = false;
                if (drained.length > 0) {
                    dotnetRef.invokeMethodAsync(callbackMethodName, drained);
                }
            }, 75); // 75ms debounce to coalesce bursts
        }

        source.addEventListener('log', (e) => {
            buffer.push(e.data);
            schedule();
        });
        source.addEventListener('hello', () => { /* no-op */ });
        source.onerror = () => { /* EventSource auto-reconnects */ };

        subs.set(id, source);
        return id;
    }

    function unsubscribe(id) {
        const s = subs.get(id);
        if (!s) return;
        s.close();
        subs.delete(id);
    }

    return { subscribe, unsubscribe };
})();
