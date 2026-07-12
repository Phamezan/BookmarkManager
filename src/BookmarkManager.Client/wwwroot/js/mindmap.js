// Bookmark Mind Map — canvas force-graph visualizer.
// Renders the bookmark tree as a constellation: branch-hued glowing nodes,
// nebula/starfield backdrop, bloom expand, hover focus. Hosted by
// Pages/MindMap.razor via the window.bookmarkMindMap interop surface.
// Requires d3-force + force-graph (loaded in index.html).
(function () {
    "use strict";

    const STYLE_STORAGE_KEY = "mindmap-node-style";
    const PULSE_MS = 700;
    const FOCUS_ZOOM = 2.2;

    // Branch palette — cosmic hues that read on the dark theme; cycles when
    // there are more top-level folders than entries.
    const BRANCH_PALETTE = [
        [227, 123, 159], // rose
        [232, 179, 107], // amber
        [95, 211, 184],  // teal
        [129, 140, 248], // indigo
        [176, 132, 255], // violet
        [96, 205, 255],  // cyan
        [163, 217, 119], // lime
        [255, 148, 112], // coral
    ];
    const NEBULA_HUES = [
        [129, 140, 248],
        [227, 123, 159],
        [95, 211, 184],
    ];

    let state = null; // everything for the active graph instance

    function cssRgb(varName, fallback) {
        const raw = getComputedStyle(document.documentElement).getPropertyValue(varName).trim();
        const m = raw.match(/^#([0-9a-f]{6})$/i);
        if (!m) return fallback;
        const v = parseInt(m[1], 16);
        return [(v >> 16) & 255, (v >> 8) & 255, v & 255];
    }

    function mix(a, b, t) {
        return a.map((c, i) => Math.round(c + (b[i] - c) * t));
    }
    function rgba(rgb, alpha) {
        return `rgba(${rgb[0]},${rgb[1]},${rgb[2]},${alpha})`;
    }

    // ------------------------------------------------------------------
    // Tree index built from the flat DTO list
    // ------------------------------------------------------------------
    function buildIndex(flatNodes) {
        // normalize NodeType: Blazor interop sends enum numbers, but accept
        // the string form too in case serializer options change
        for (const n of flatNodes) {
            n.type = (n.type === "Folder" || n.type === 1) ? 1 : 0;
        }
        const byId = new Map(flatNodes.map(n => [n.id, n]));
        const childIds = new Map(flatNodes.map(n => [n.id, []]));
        const orphans = [];
        for (const n of flatNodes) {
            if (n.parentId && childIds.has(n.parentId)) childIds.get(n.parentId).push(n.id);
            else orphans.push(n.id);
        }
        for (const ids of childIds.values()) {
            ids.sort((a, b) => (byId.get(a).position ?? 0) - (byId.get(b).position ?? 0));
        }

        // Root: single parentless folder → use it (Brave's unnamed root);
        // anything else → synthesize one so the graph always has a center.
        let rootId;
        const FOLDER = 1; // NodeType.Folder
        if (orphans.length === 1 && byId.get(orphans[0]).type === FOLDER) {
            rootId = orphans[0];
            const rootNode = byId.get(rootId);
            if (!rootNode.title || !rootNode.title.trim()) rootNode.title = "Bookmarks";
        } else {
            rootId = "__synthetic_root__";
            byId.set(rootId, { id: rootId, parentId: null, type: FOLDER, title: "Bookmarks", url: null, isFavorite: false });
            childIds.set(rootId, orphans);
            for (const id of orphans) byId.get(id).parentId = rootId;
        }

        const index = new Map(); // id -> { raw, depth, parentId, childIds }
        (function walk(id, depth, parentId) {
            index.set(id, { raw: byId.get(id), depth, parentId, childIds: childIds.get(id) ?? [] });
            for (const c of childIds.get(id) ?? []) walk(c, depth + 1, id);
        })(rootId, 0, null);

        return { index, rootId };
    }

    function countDescendantBookmarks(index, id, memo) {
        if (memo.has(id)) return memo.get(id);
        const entry = index.get(id);
        let count = 0;
        for (const childId of entry.childIds) {
            const child = index.get(childId);
            count += child.raw.type === 0 ? 1 : countDescendantBookmarks(index, childId, memo);
        }
        memo.set(id, count);
        return count;
    }

    // ------------------------------------------------------------------
    // Colors
    // ------------------------------------------------------------------
    function assignBranchColors(index, rootId) {
        const colors = new Map();
        const topIds = index.get(rootId).childIds;
        topIds.forEach((id, i) => colors.set(id, BRANCH_PALETTE[i % BRANCH_PALETTE.length]));
        return colors;
    }

    function branchOf(index, rootId, id) {
        let cur = id;
        while (cur) {
            const entry = index.get(cur);
            if (entry.parentId === rootId) return cur;
            cur = entry.parentId;
        }
        return null;
    }

    function nodeRgb(s, id) {
        const entry = s.index.get(id);
        if (id === s.rootId) return s.rootColor;
        const base = s.branchColors.get(branchOf(s.index, s.rootId, id)) || s.muted;
        if (entry.raw.type === 0) return mix(base, s.muted, 0.35);
        return mix(base, s.muted, Math.min((entry.depth - 1) * 0.18, 0.5));
    }

    // ------------------------------------------------------------------
    // Visible-graph builder (expand/collapse aware)
    // ------------------------------------------------------------------
    function buildVisible(s) {
        const nodes = new Map();
        const links = [];
        const memo = new Map();

        function addNode(id) {
            const entry = s.index.get(id);
            const isFolder = entry.raw.type === 1;
            const bookmarkCount = isFolder ? countDescendantBookmarks(s.index, id, memo) : 0;
            nodes.set(id, {
                id,
                name: entry.raw.title || "(untitled)",
                url: entry.raw.url,
                isFolder,
                isFavorite: !!entry.raw.isFavorite,
                depth: entry.depth,
                childCount: entry.childIds.length,
                bookmarkCount,
                rgb: nodeRgb(s, id),
                val: isFolder ? Math.max(4, 3 + Math.log2(1 + bookmarkCount)) : 2,
            });
        }

        (function walk(id) {
            addNode(id);
            const entry = s.index.get(id);
            if (entry.raw.type === 1 && s.expanded.has(id)) {
                for (const childId of entry.childIds) {
                    links.push({ source: id, target: childId });
                    walk(childId);
                }
            }
        })(s.rootId);

        s.visibleNodes = nodes;
        return { nodes: Array.from(nodes.values()), links };
    }

    // ------------------------------------------------------------------
    // Ambient background: nebula blobs + twinkling starfield in graph space
    // ------------------------------------------------------------------
    function makeStars() {
        return Array.from({ length: 260 }, () => {
            const ang = Math.random() * 2 * Math.PI;
            const rad = 30 + Math.pow(Math.random(), 0.6) * 1400;
            return {
                x: Math.cos(ang) * rad,
                y: Math.sin(ang) * rad,
                r: 0.3 + Math.random() * 1.1,
                phase: Math.random() * Math.PI * 2,
                speed: 0.4 + Math.random() * 1.4,
            };
        });
    }

    function makeNebulae() {
        return NEBULA_HUES.map((rgb, i) => ({
            x: [-420, 480, 120][i],
            y: [-260, 300, -480][i],
            r: [620, 700, 560][i],
            rgb,
            a: [0.085, 0.06, 0.055][i],
            dx: [40, 55, 35][i],
            dy: [25, 30, 45][i],
            s: [0.00006, 0.00005, 0.00007][i],
        }));
    }

    function drawBackground(s, ctx, scale) {
        const t = s.reducedMotion ? 0 : performance.now();

        ctx.globalCompositeOperation = "lighter";
        for (const nb of s.nebulae) {
            const x = nb.x + Math.sin(t * nb.s) * nb.dx;
            const y = nb.y + Math.cos(t * nb.s * 1.3) * nb.dy;
            const g = ctx.createRadialGradient(x, y, 0, x, y, nb.r);
            g.addColorStop(0, rgba(nb.rgb, nb.a));
            g.addColorStop(1, rgba(nb.rgb, 0));
            ctx.fillStyle = g;
            ctx.beginPath();
            ctx.arc(x, y, nb.r, 0, 2 * Math.PI);
            ctx.fill();
        }

        ctx.globalCompositeOperation = "source-over";
        for (const st of s.stars) {
            const tw = s.reducedMotion ? 0.4 : 0.25 + 0.3 * Math.sin(t * 0.001 * st.speed + st.phase);
            ctx.fillStyle = `rgba(185,195,235,${tw})`;
            ctx.beginPath();
            ctx.arc(st.x, st.y, st.r / Math.sqrt(scale), 0, 2 * Math.PI);
            ctx.fill();
        }
    }

    // ------------------------------------------------------------------
    // Node rendering — three switchable looks
    // ------------------------------------------------------------------
    function drawNode(s, node, ctx, globalScale) {
        if (!isFinite(node.x) || !isFinite(node.y)) return;
        const r = 4 * Math.sqrt(node.val);
        const isHi = s.highlightNodes.has(node.id);
        const dimmed = s.hoverNode && !isHi;
        const alpha = dimmed ? 0.22 : 1;
        const now = performance.now();

        if (node.__phase === undefined) node.__phase = Math.random() * Math.PI * 2;
        const breathe = s.reducedMotion ? 1 : 1 + 0.09 * Math.sin(now / 1500 + node.__phase);

        // additive bloom halo
        const glowR = r * (isHi ? 5 : (node.isFolder ? 3.4 : 2.2)) * breathe;
        const haloMul = s.style === "glyph" ? 0.45 : 1;
        ctx.globalCompositeOperation = "lighter";
        const halo = ctx.createRadialGradient(node.x, node.y, r * 0.2, node.x, node.y, glowR);
        halo.addColorStop(0, rgba(node.rgb, (isHi ? 0.55 : (node.isFolder ? 0.3 : 0.18)) * haloMul * alpha));
        halo.addColorStop(0.5, rgba(node.rgb, (isHi ? 0.18 : 0.08) * haloMul * alpha));
        halo.addColorStop(1, rgba(node.rgb, 0));
        ctx.fillStyle = halo;
        ctx.beginPath();
        ctx.arc(node.x, node.y, glowR, 0, 2 * Math.PI);
        ctx.fill();
        ctx.globalCompositeOperation = "source-over";

        // click/focus pulse: expanding ring fading out
        if (s.pulse && s.pulse.id === node.id) {
            const t = (now - s.pulse.t0) / PULSE_MS;
            if (t >= 1) {
                s.pulse = null;
            } else {
                ctx.strokeStyle = rgba(node.rgb, (1 - t) * 0.8);
                ctx.lineWidth = 2 * (1 - t) + 0.5;
                ctx.beginPath();
                ctx.arc(node.x, node.y, r + t * 34, 0, 2 * Math.PI);
                ctx.stroke();
            }
        }

        const isCollapsed = node.isFolder && node.childCount > 0 && !s.expanded.has(node.id);

        if (s.style === "stars") drawStarCore(s, node, ctx, r, isHi, alpha, breathe, isCollapsed);
        else if (s.style === "orbital") drawOrbitalCore(s, node, ctx, r, alpha, now, isCollapsed);
        else drawGlyphCore(s, node, ctx, r, isHi, alpha, isCollapsed);

        // favorite bookmarks: warm ring in the theme's favorite hue
        if (node.isFavorite && !node.isFolder) {
            ctx.strokeStyle = rgba(s.favorite, 0.85 * alpha);
            ctx.lineWidth = 1.2;
            ctx.beginPath();
            ctx.arc(node.x, node.y, r + 2.5, 0, 2 * Math.PI);
            ctx.stroke();
        }

        // pinned node: dashed ring
        if (node.fx != null) {
            ctx.strokeStyle = rgba(s.text, 0.35 * alpha);
            ctx.lineWidth = 1;
            ctx.setLineDash([2, 3]);
            ctx.beginPath();
            ctx.arc(node.x, node.y, r + 6.5, 0, 2 * Math.PI);
            ctx.stroke();
            ctx.setLineDash([]);
        }

        // labels: folders always; bookmarks fade in past zoom threshold
        let labelAlpha = alpha;
        if (!node.isFolder) {
            labelAlpha *= Math.max(0, Math.min((globalScale - 1.3) / 0.7, 1));
            if (isHi) labelAlpha = alpha;
        }
        if (labelAlpha > 0.02) {
            const fontSize = Math.max(10, 12 / globalScale);
            ctx.font = `${node.depth === 0 ? 600 : 500} ${fontSize}px Inter, sans-serif`;
            ctx.textAlign = "center";
            ctx.textBaseline = "top";
            const col = node.depth === 0 ? s.text : (node.isFolder ? s.textSecondary : mix(s.textSecondary, s.muted, 0.5));
            ctx.fillStyle = rgba(isHi ? s.text : col, labelAlpha);
            ctx.shadowColor = "rgba(6,7,16,0.9)";
            ctx.shadowBlur = 4;
            const label = node.name.length > 42 ? node.name.slice(0, 40) + "…" : node.name;
            ctx.fillText(label, node.x, node.y + r + 3.5);
            ctx.shadowBlur = 0;
        }
    }

    function drawStarCore(s, node, ctx, r, isHi, alpha, breathe, isCollapsed) {
        ctx.globalCompositeOperation = "lighter";
        const hot = mix(node.rgb, [255, 255, 255], node.depth === 0 ? 0.95 : 0.82);
        const core = ctx.createRadialGradient(node.x, node.y, 0, node.x, node.y, r);
        core.addColorStop(0, rgba(hot, alpha));
        core.addColorStop(0.35, rgba(mix(node.rgb, [255, 255, 255], 0.3), 0.9 * alpha));
        core.addColorStop(0.7, rgba(node.rgb, 0.45 * alpha));
        core.addColorStop(1, rgba(node.rgb, 0));
        ctx.fillStyle = core;
        ctx.beginPath();
        ctx.arc(node.x, node.y, r, 0, 2 * Math.PI);
        ctx.fill();

        if (node.isFolder) {
            const spikeLen = r * (node.depth === 0 ? 5.5 : 3.6) * breathe * (isHi ? 1.25 : 1);
            const spikeW = r * 0.16;
            const spikeA = (node.depth === 0 ? 0.55 : 0.4) * alpha;
            const flareRgb = mix(node.rgb, [255, 255, 255], 0.5);
            let fg = ctx.createLinearGradient(node.x - spikeLen, node.y, node.x + spikeLen, node.y);
            fg.addColorStop(0, rgba(flareRgb, 0));
            fg.addColorStop(0.5, rgba(flareRgb, spikeA));
            fg.addColorStop(1, rgba(flareRgb, 0));
            ctx.fillStyle = fg;
            ctx.beginPath();
            ctx.moveTo(node.x - spikeLen, node.y);
            ctx.lineTo(node.x, node.y - spikeW);
            ctx.lineTo(node.x + spikeLen, node.y);
            ctx.lineTo(node.x, node.y + spikeW);
            ctx.closePath();
            ctx.fill();
            fg = ctx.createLinearGradient(node.x, node.y - spikeLen, node.x, node.y + spikeLen);
            fg.addColorStop(0, rgba(flareRgb, 0));
            fg.addColorStop(0.5, rgba(flareRgb, spikeA));
            fg.addColorStop(1, rgba(flareRgb, 0));
            ctx.fillStyle = fg;
            ctx.beginPath();
            ctx.moveTo(node.x, node.y - spikeLen);
            ctx.lineTo(node.x - spikeW, node.y);
            ctx.lineTo(node.x, node.y + spikeLen);
            ctx.lineTo(node.x + spikeW, node.y);
            ctx.closePath();
            ctx.fill();
        }
        ctx.globalCompositeOperation = "source-over";

        if (isCollapsed) {
            ctx.strokeStyle = rgba(node.rgb, 0.55 * alpha);
            ctx.lineWidth = 1;
            ctx.beginPath();
            ctx.arc(node.x, node.y, r + 3.5, 0, 2 * Math.PI);
            ctx.stroke();
        }
    }

    function drawOrbitalCore(s, node, ctx, r, alpha, now, isCollapsed) {
        const coreR = node.isFolder ? r * 0.72 : r * 0.85;
        const core = ctx.createRadialGradient(node.x, node.y, 0, node.x, node.y, coreR);
        core.addColorStop(0, rgba(mix(node.rgb, [255, 255, 255], node.depth === 0 ? 0.85 : 0.55), alpha));
        core.addColorStop(0.55, rgba(node.rgb, alpha));
        core.addColorStop(1, rgba(mix(node.rgb, [12, 13, 20], 0.45), alpha));
        ctx.fillStyle = core;
        ctx.beginPath();
        ctx.arc(node.x, node.y, coreR, 0, 2 * Math.PI);
        ctx.fill();

        if (node.isFolder) {
            const ringR = r + 4.5;
            ctx.strokeStyle = rgba(node.rgb, (isCollapsed ? 0.6 : 0.28) * alpha);
            ctx.lineWidth = 1;
            ctx.beginPath();
            ctx.arc(node.x, node.y, ringR, 0, 2 * Math.PI);
            ctx.stroke();

            if (isCollapsed) {
                const moons = Math.min(node.childCount, 8);
                const spin = (s.reducedMotion ? 0 : now * 0.0004) + node.__phase;
                for (let i = 0; i < moons; i++) {
                    const a = spin + i * 2 * Math.PI / moons;
                    ctx.fillStyle = rgba(mix(node.rgb, [255, 255, 255], 0.4), 0.9 * alpha);
                    ctx.beginPath();
                    ctx.arc(node.x + Math.cos(a) * ringR, node.y + Math.sin(a) * ringR, 1.3, 0, 2 * Math.PI);
                    ctx.fill();
                }
            }
        }
    }

    function drawGlyphCore(s, node, ctx, r, isHi, alpha, isCollapsed) {
        const discR = r + 2;
        ctx.fillStyle = `rgba(20,22,31,${0.92 * alpha})`;
        ctx.beginPath();
        ctx.arc(node.x, node.y, discR, 0, 2 * Math.PI);
        ctx.fill();
        ctx.strokeStyle = rgba(node.rgb, (isHi ? 1 : 0.75) * alpha);
        ctx.lineWidth = isHi ? 1.8 : 1.2;
        ctx.beginPath();
        ctx.arc(node.x, node.y, discR, 0, 2 * Math.PI);
        ctx.stroke();
        if (isCollapsed) {
            ctx.strokeStyle = rgba(node.rgb, 0.35 * alpha);
            ctx.lineWidth = 1;
            ctx.beginPath();
            ctx.arc(node.x, node.y, discR + 3, 0, 2 * Math.PI);
            ctx.stroke();
        }

        const gs = discR * 0.52;
        const gcol = rgba(mix(node.rgb, [255, 255, 255], 0.25), alpha);
        if (node.isFolder) {
            ctx.fillStyle = gcol;
            ctx.beginPath();
            ctx.roundRect(node.x - gs, node.y - gs * 0.8, gs * 0.9, gs * 0.45, 1);
            ctx.fill();
            ctx.beginPath();
            ctx.roundRect(node.x - gs, node.y - gs * 0.5, 2 * gs, gs * 1.3, 1.5);
            ctx.fill();
        } else {
            ctx.strokeStyle = gcol;
            ctx.lineWidth = Math.max(1, gs * 0.28);
            ctx.lineCap = "round";
            ctx.beginPath();
            ctx.moveTo(node.x - gs * 0.55, node.y + gs * 0.55);
            ctx.lineTo(node.x + gs * 0.5, node.y - gs * 0.5);
            ctx.moveTo(node.x - gs * 0.15, node.y - gs * 0.5);
            ctx.lineTo(node.x + gs * 0.5, node.y - gs * 0.5);
            ctx.lineTo(node.x + gs * 0.5, node.y + gs * 0.15);
            ctx.stroke();
        }
    }

    // ------------------------------------------------------------------
    // Interaction helpers
    // ------------------------------------------------------------------
    function setHover(s, node) {
        s.hoverNode = node || null;
        s.highlightNodes.clear();
        s.highlightLinks.clear();
        if (!node) return;
        s.highlightNodes.add(node.id);
        for (const link of s.graph.graphData().links) {
            const src = typeof link.source === "object" ? link.source.id : link.source;
            const tgt = typeof link.target === "object" ? link.target.id : link.target;
            if (src === node.id || tgt === node.id) {
                s.highlightLinks.add(link);
                s.highlightNodes.add(src === node.id ? tgt : src);
            }
        }
    }

    function collapseSubtree(s, id) {
        s.expanded.delete(id);
        for (const childId of s.index.get(id).childIds) {
            if (s.index.get(childId).raw.type === 1) collapseSubtree(s, childId);
        }
    }

    function refresh(s, keepPositions) {
        const prevPos = new Map();
        if (keepPositions) {
            s.graph.graphData().nodes.forEach(n => prevPos.set(n.id, { x: n.x, y: n.y, fx: n.fx, fy: n.fy }));
        }
        const data = buildVisible(s);
        data.nodes.forEach(n => {
            const p = prevPos.get(n.id);
            if (p) {
                Object.assign(n, p);
            } else if (keepPositions) {
                // bloom: children spawn at their parent's position and get
                // flung outward by the sim instead of teleporting in
                const pp = prevPos.get(s.index.get(n.id).parentId);
                if (pp) {
                    n.x = pp.x + (Math.random() - 0.5) * 12;
                    n.y = pp.y + (Math.random() - 0.5) * 12;
                }
            }
        });
        s.graph.graphData(data);
        setHover(s, null);
    }

    function scheduleFit(s, delay) {
        clearTimeout(s.fitTimer);
        s.fitTimer = setTimeout(() => s.graph.zoomToFit(600, 80), delay ?? 450);
    }

    // ------------------------------------------------------------------
    // Public interop surface
    // ------------------------------------------------------------------
    window.bookmarkMindMap = {
        init(hostId, flatNodes, options) {
            this.destroy();
            const el = document.getElementById(hostId);
            if (!el || typeof ForceGraph !== "function" || typeof d3 !== "object") return false;

            const { index, rootId } = buildIndex(flatNodes ?? []);
            const s = state = {
                el,
                index,
                rootId,
                expanded: new Set([rootId]),
                branchColors: assignBranchColors(index, rootId),
                visibleNodes: new Map(),
                hoverNode: null,
                highlightNodes: new Set(),
                highlightLinks: new Set(),
                pulse: null,
                fitTimer: 0,
                stars: makeStars(),
                nebulae: makeNebulae(),
                reducedMotion: window.matchMedia("(prefers-reduced-motion: reduce)").matches,
                style: (options && options.style) || localStorage.getItem(STYLE_STORAGE_KEY) || "stars",
                // theme tokens (fall back to default dark palette)
                text: cssRgb("--bm-text", [244, 244, 246]),
                textSecondary: cssRgb("--bm-text-secondary", [168, 174, 192]),
                muted: cssRgb("--bm-text-muted", [107, 110, 124]),
                favorite: cssRgb("--bm-favorite", [255, 77, 224]),
                rootColor: [205, 210, 255],
            };

            const graph = s.graph = ForceGraph()(el)
                .backgroundColor("#00000000")
                .autoPauseRedraw(s.reducedMotion) // ambient animation unless reduced motion
                .nodeId("id")
                .nodeLabel(n => n.isFolder
                    ? `${n.name} · ${n.bookmarkCount} bookmark${n.bookmarkCount === 1 ? "" : "s"}`
                    : `${n.name}${n.url ? `\n${n.url}` : ""}`)
                .nodeVal("val")
                .nodeRelSize(4)
                .onRenderFramePre((ctx, scale) => drawBackground(s, ctx, scale))
                .linkCanvasObjectMode(() => "replace")
                .linkCanvasObject((link, ctx) => {
                    const a = link.source, b = link.target;
                    if (typeof a !== "object" || !isFinite(a.x) || !isFinite(b.x)) return;
                    const isHi = s.highlightLinks.has(link);
                    const base = isHi ? 0.75 : (s.hoverNode ? 0.05 : 0.28);
                    const grad = ctx.createLinearGradient(a.x, a.y, b.x, b.y);
                    grad.addColorStop(0, rgba(a.rgb, base));
                    grad.addColorStop(1, rgba(b.rgb, base * 0.55));
                    ctx.globalCompositeOperation = "lighter";
                    ctx.strokeStyle = grad;
                    ctx.lineWidth = isHi ? 4 : 2.5;
                    ctx.globalAlpha = 0.35;
                    ctx.beginPath();
                    ctx.moveTo(a.x, a.y);
                    ctx.lineTo(b.x, b.y);
                    ctx.stroke();
                    ctx.globalCompositeOperation = "source-over";
                    ctx.globalAlpha = 1;
                    ctx.lineWidth = isHi ? 1.4 : 0.8;
                    ctx.beginPath();
                    ctx.moveTo(a.x, a.y);
                    ctx.lineTo(b.x, b.y);
                    ctx.stroke();
                })
                .linkDirectionalParticles(link => s.highlightLinks.has(link) ? 3 : (s.reducedMotion ? 0 : 1))
                .linkDirectionalParticleSpeed(link => s.highlightLinks.has(link) ? 0.008 : 0.0025)
                .linkDirectionalParticleWidth(link => s.highlightLinks.has(link) ? 3 : 1.6)
                .linkDirectionalParticleColor(link => {
                    const src = typeof link.source === "object" ? link.source : s.visibleNodes.get(link.source);
                    return rgba(src ? src.rgb : s.muted, s.highlightLinks.has(link) ? 0.95 : 0.5);
                })
                .onNodeHover(node => {
                    setHover(s, node);
                    el.style.cursor = node ? "pointer" : "";
                })
                .onNodeClick(node => {
                    const entry = s.index.get(node.id);
                    if (entry.raw.type === 0) {
                        // bookmark: open in a new tab
                        if (entry.raw.url) window.open(entry.raw.url, "_blank", "noopener");
                        return;
                    }
                    if (entry.childIds.length === 0) return;
                    if (s.expanded.has(node.id)) collapseSubtree(s, node.id);
                    else s.expanded.add(node.id);
                    s.pulse = { id: node.id, t0: performance.now() };
                    refresh(s, true);
                    scheduleFit(s);
                })
                .onNodeRightClick(node => {
                    // unpin a dragged node
                    node.fx = undefined;
                    node.fy = undefined;
                    s.graph.d3ReheatSimulation();
                })
                .onNodeDragEnd(node => {
                    node.fx = node.x;
                    node.fy = node.y;
                })
                .nodeCanvasObjectMode(() => "replace")
                .nodeCanvasObject((node, ctx, scale) => drawNode(s, node, ctx, scale))
                .nodePointerAreaPaint((node, color, ctx) => {
                    const r = 4 * Math.sqrt(node.val);
                    ctx.fillStyle = color;
                    ctx.beginPath();
                    ctx.arc(node.x, node.y, r + 4, 0, 2 * Math.PI);
                    ctx.fill();
                });

            // Hybrid layout: force-sim charge/link plus radial pull by depth and
            // per-branch angular home sectors so the structure stays readable.
            graph.d3Force("radial", d3.forceRadial(n => n.depth === 0 ? 0 : 40 + n.depth * 110, 0, 0)
                .strength(n => n.depth === 0 ? 1 : 0.65));
            graph.d3Force("charge").strength(-220);
            graph.d3Force("link").distance(link => {
                const src = typeof link.source === "object" ? link.source : s.visibleNodes.get(link.source);
                return src && src.isFolder ? 60 : 30;
            });
            const topIds = index.get(rootId).childIds;
            const homeAngle = new Map(topIds.map((id, i) => [id, -Math.PI / 2 + i * 2 * Math.PI / Math.max(topIds.length, 1)]));
            function homeCoord(n, trig) {
                const b = n.depth === 1 ? n.id : branchOf(index, rootId, n.id);
                const ang = homeAngle.get(b);
                return ang === undefined ? 0 : trig(ang) * (40 + n.depth * 110);
            }
            graph.d3Force("homeX", d3.forceX(n => homeCoord(n, Math.cos)).strength(n => n.depth === 0 ? 0 : 0.12));
            graph.d3Force("homeY", d3.forceY(n => homeCoord(n, Math.sin)).strength(n => n.depth === 0 ? 0 : 0.12));

            refresh(s, false);

            let didInitialFit = false;
            graph.onEngineStop(() => {
                if (!didInitialFit) {
                    didInitialFit = true;
                    graph.zoomToFit(600, 80);
                }
            });

            s.resize = () => graph.width(el.clientWidth).height(el.clientHeight);
            window.addEventListener("resize", s.resize);
            s.resize();
            return true;
        },

        destroy() {
            if (!state) return;
            clearTimeout(state.fitTimer);
            window.removeEventListener("resize", state.resize);
            if (state.graph && typeof state.graph._destructor === "function") state.graph._destructor();
            if (state.el) state.el.innerHTML = "";
            state = null;
        },

        setStyle(style) {
            if (!state) return;
            state.style = style;
            localStorage.setItem(STYLE_STORAGE_KEY, style);
        },

        getStyle() {
            return localStorage.getItem(STYLE_STORAGE_KEY) || "stars";
        },

        // Expand ancestors of a node, then zoom/pulse it (search jump).
        focusNode(id) {
            const s = state;
            if (!s || !s.index.has(id)) return;
            let cur = s.index.get(id).parentId;
            while (cur) {
                s.expanded.add(cur);
                cur = s.index.get(cur).parentId;
            }
            refresh(s, true);
            s.pulse = { id, t0: performance.now() + 500 };
            clearTimeout(s.fitTimer);
            s.fitTimer = setTimeout(() => {
                const node = s.graph.graphData().nodes.find(n => n.id === id);
                if (!node) return;
                s.graph.centerAt(node.x, node.y, 600);
                s.graph.zoom(Math.max(s.graph.zoom(), FOCUS_ZOOM), 600);
                s.pulse = { id, t0: performance.now() + 600 };
            }, 500);
        },

        expandAll() {
            const s = state;
            if (!s) return;
            for (const [id, entry] of s.index) {
                if (entry.raw.type === 1) s.expanded.add(id);
            }
            refresh(s, true);
            scheduleFit(s, 700);
        },

        collapseAll() {
            const s = state;
            if (!s) return;
            s.expanded.clear();
            s.expanded.add(s.rootId);
            refresh(s, true);
            scheduleFit(s);
        },

        zoomToFit() {
            if (state) state.graph.zoomToFit(600, 80);
        },
    };
})();
