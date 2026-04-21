// Blazor <-> GridStack interop for the Lord Helm command center.
// Keeps the widget grid draggable + resizable and persists the layout
// (x, y, w, h per widget id) to localStorage.

const LAYOUT_KEY = 'lordhelm.widget-layout.v1';

window.helmGrid = (function () {
    let grid = null;
    let dotnetRef = null;

    function readLayout() {
        try {
            const raw = localStorage.getItem(LAYOUT_KEY);
            return raw ? JSON.parse(raw) : {};
        } catch { return {}; }
    }

    function writeLayout() {
        if (!grid) return;
        const items = grid.save(false);
        const map = {};
        for (const it of items) {
            if (!it.id) continue;
            map[it.id] = { x: it.x, y: it.y, w: it.w, h: it.h };
        }
        localStorage.setItem(LAYOUT_KEY, JSON.stringify(map));
    }

    function init(selector, dotnetObjectRef) {
        dotnetRef = dotnetObjectRef;
        const el = document.querySelector(selector);
        if (!el) { console.warn('helmGrid: root not found', selector); return; }
        if (grid) grid.destroy(false);
        grid = GridStack.init({
            cellHeight: 100,
            margin: 8,
            float: true,
            animate: true,
            disableOneColumnMode: false,
            column: 12,
            minRow: 3,
        }, el);
        grid.on('change', () => writeLayout());
        grid.on('resizestop dragstop', () => writeLayout());
        applyLayout();
    }

    function applyLayout() {
        const saved = readLayout();
        if (!grid) return;
        for (const node of grid.engine.nodes) {
            const s = saved[node.id];
            if (s) grid.update(node.el, { x: s.x, y: s.y, w: s.w, h: s.h });
        }
    }

    function resetLayout() {
        localStorage.removeItem(LAYOUT_KEY);
        if (dotnetRef) dotnetRef.invokeMethodAsync('ReloadWidgets');
    }

    function reapply() {
        if (grid) { grid.compact(); applyLayout(); }
    }

    return { init, applyLayout, resetLayout, reapply };
})();
