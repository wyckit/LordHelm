// helmtopo.js — client-side packet animation for the Topology widget.
// Reads node data-x/y/kind attributes from the DOM on each rAF tick, spawns
// new packets every ~360ms from working agents up to their pod and onward to
// helm (telemetry), and plays a small set of helm→pod command packets. All
// animation runs locally — Blazor only pushes static node/edge state + load
// updates. Panel-endorsed per debate-lordhelm-design-handoff-implementation-2026-04-21.

const HOP_MS = 1800;     // one edge hop
const SPAWN_MS = 360;    // new packet burst interval
const MAX_PACKETS = 40;

function byId(root) {
    const map = {};
    root.querySelectorAll('.topo-node').forEach(el => { map[el.dataset.id] = el; });
    return map;
}

function readEdges(root) {
    // Parse edges out of the rendered SVG <line> elements. Each line's
    // endpoints match node positions; we look them up by coord rounding.
    const edges = [];
    const nodes = Array.from(root.querySelectorAll('.topo-node')).map(n => ({
        id: n.dataset.id,
        x: parseFloat(n.dataset.x),
        y: parseFloat(n.dataset.y),
        kind: n.dataset.kind,
    }));
    root.querySelectorAll('.topo-edges line').forEach(line => {
        const x1 = parseFloat(line.getAttribute('x1'));
        const y1 = parseFloat(line.getAttribute('y1'));
        const x2 = parseFloat(line.getAttribute('x2'));
        const y2 = parseFloat(line.getAttribute('y2'));
        const from = nodes.find(n => Math.abs(n.x - x1) < 0.5 && Math.abs(n.y - y1) < 0.5);
        const to   = nodes.find(n => Math.abs(n.x - x2) < 0.5 && Math.abs(n.y - y2) < 0.5);
        if (from && to) edges.push([from, to]);
    });
    return edges;
}

function pickAgentToPod(edges) {
    // Returns { agentId: podId }.
    const m = {};
    edges.forEach(([a, b]) => {
        if (a.kind === 'orc' && b.kind === 'agent') m[b.id] = a.id;
    });
    return m;
}

function pickHelmPods(edges) {
    return edges
        .filter(([a, b]) => a.id === 'helm' && b.kind === 'orc')
        .map(([, b]) => b.id);
}

export function start(host) {
    if (!host || host._helmtopoStarted) return;
    host._helmtopoStarted = true;

    const packetLayer = host.querySelector('.topo-packets');
    if (!packetLayer) return;

    let packets = [];
    let lastSpawn = 0;
    let running = true;

    function spawn(now) {
        const nodes = Array.from(host.querySelectorAll('.topo-node'));
        const edges = readEdges(host);
        const agentToPod = pickAgentToPod(edges);
        const helmPods = pickHelmPods(edges);

        nodes.forEach(el => {
            const id = el.dataset.id;
            const kind = el.dataset.kind;
            const load = parseFloat(el.dataset.load || '0');
            const status = el.dataset.status;
            if (kind !== 'agent') return;
            const pod = agentToPod[id];
            if (!pod) return;
            const working = status === 'working' && Math.random() < (0.3 + load * 0.4);
            const blocked = status === 'blocked' && Math.random() < 0.25;
            if (!working && !blocked) return;
            packets.push({
                id: `${id}-${now}-${Math.random().toString(36).slice(2, 5)}`,
                hops: [id, pod, 'helm'],
                hopIndex: 0,
                start: now,
                color: blocked ? 'var(--err)' : 'var(--accent)',
            });
        });
        if (Math.random() < 0.35 && helmPods.length) {
            const target = helmPods[Math.floor(Math.random() * helmPods.length)];
            packets.push({
                id: `cmd-${now}-${Math.random().toString(36).slice(2, 5)}`,
                hops: ['helm', target],
                hopIndex: 0,
                start: now,
                color: 'var(--brand)',
            });
        }
        if (packets.length > MAX_PACKETS) packets = packets.slice(-MAX_PACKETS);
    }

    function posOf(id) {
        const el = host.querySelector(`.topo-node[data-id="${CSS.escape(id)}"]`);
        if (!el) return null;
        return { x: parseFloat(el.dataset.x), y: parseFloat(el.dataset.y) };
    }

    function render(now) {
        if (!running) return;
        if (now - lastSpawn > SPAWN_MS) { spawn(now); lastSpawn = now; }

        // rebuild packet DOM fresh each frame — cheaper than diff for ≤40 circles
        while (packetLayer.firstChild) packetLayer.removeChild(packetLayer.firstChild);

        packets = packets.filter(p => {
            const elapsed = now - p.start;
            const hopIdx = Math.floor(elapsed / HOP_MS);
            if (hopIdx >= p.hops.length - 1) return false;
            const from = posOf(p.hops[hopIdx]);
            const to = posOf(p.hops[hopIdx + 1]);
            if (!from || !to) return false;
            const t = (elapsed % HOP_MS) / HOP_MS;
            const x = from.x + (to.x - from.x) * t;
            const y = from.y + (to.y - from.y) * t;
            const g = document.createElementNS('http://www.w3.org/2000/svg', 'g');
            [
                { r: 1.6, o: 0.15 },
                { r: 0.9, o: 0.35 },
                { r: 0.5, o: 1.0 },
            ].forEach(s => {
                const c = document.createElementNS('http://www.w3.org/2000/svg', 'circle');
                c.setAttribute('cx', x);
                c.setAttribute('cy', y);
                c.setAttribute('r', s.r);
                c.setAttribute('fill', p.color);
                c.setAttribute('opacity', s.o);
                g.appendChild(c);
            });
            packetLayer.appendChild(g);
            return true;
        });

        requestAnimationFrame(render);
    }

    requestAnimationFrame(render);

    host._helmtopoStop = () => { running = false; };
}

export function stop(host) {
    if (host?._helmtopoStop) host._helmtopoStop();
    if (host) host._helmtopoStarted = false;
}
