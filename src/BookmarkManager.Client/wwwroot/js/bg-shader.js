// WebGL background shader.
// Monitor moves (1440p ↔ 1080p / DPR change) previously:
//  - blew up scanline artifacts into zebra stripes (buffer ≠ CSS size)
//  - left Chromium backdrop-filter text shredded in the top bar
//  - flooded layout with 100vw-based overflow scrollbars
(function () {
    const MAX_PIXEL_RATIO = 1.25;
    let controller = null;
    let repairTimer = null;

    function effectivePixelRatio() {
        return Math.min(window.devicePixelRatio || 1, MAX_PIXEL_RATIO);
    }

    function scheduleDisplayRepair() {
        if (repairTimer !== null) clearTimeout(repairTimer);
        document.documentElement.classList.add('bm-display-repair');
        repairTimer = setTimeout(() => {
            repairTimer = null;
            try {
                const ind = document.querySelector('.nav-active-indicator');
                if (ind && window.gsap) {
                    window.gsap.set(ind, { clearProps: 'width,height,x,y,transform' });
                }
                if (typeof window.repositionNavIndicator === 'function') {
                    window.repositionNavIndicator();
                }
                // Nudge Chromium to rebuild composited layers after DPI change.
                void document.body.offsetWidth;
            } catch (_) { /* ignore */ }
            requestAnimationFrame(() => {
                document.documentElement.classList.remove('bm-display-repair');
            });
        }, 180);
    }

    function disposeController() {
        if (!controller) return;
        controller.stop();
        try {
            controller.geometry.dispose();
            controller.material.dispose();
            controller.renderer.dispose();
        } catch (_) { /* ignore */ }
        controller = null;
        window.bgShaderInitialized = false;
    }

    function abandonWebGl(canvas) {
        disposeController();
        if (canvas) {
            canvas.style.display = 'none';
        }
        document.documentElement.classList.add('bm-no-webgl-bg');
    }

    window.initBgShader = function () {
        if (controller) return;
        if (document.documentElement.classList.contains('bm-no-webgl-bg')) return;

        const canvas = document.getElementById('bg-canvas');
        if (!canvas) {
            console.warn('WebGL Background Canvas (#bg-canvas) not found in DOM.');
            return;
        }

        if (typeof THREE === 'undefined') {
            console.warn('THREE.js not loaded; background shader skipped.');
            return;
        }

        let renderer;
        try {
            renderer = new THREE.WebGLRenderer({
                canvas,
                alpha: true,
                antialias: false,
                powerPreference: 'low-power'
            });
        } catch (e) {
            console.warn('WebGLRenderer init failed', e);
            abandonWebGl(canvas);
            return;
        }

        window.bgShaderInitialized = true;
        canvas.style.display = '';
        canvas.style.width = '100%';
        canvas.style.height = '100%';

        const uniforms = {
            u_time: { value: 0.0 },
            u_resolution: { value: new THREE.Vector2(1, 1) },
            u_color1: { value: new THREE.Color('#0C0E17') },
            u_color2: { value: new THREE.Color('#121524') },
            u_glowColor: { value: new THREE.Color('#27355c') }
        };

        const scene = new THREE.Scene();
        const camera = new THREE.OrthographicCamera(-1, 1, 1, -1, 0, 1);
        // No scanline term — when the drawing buffer drifts from CSS size after a
        // monitor move, sin(y*900) becomes thick zebra stripes across the UI.
        const material = new THREE.ShaderMaterial({
            uniforms: uniforms,
            vertexShader: `
                varying vec2 v_texCoord;
                void main() {
                    v_texCoord = uv;
                    gl_Position = vec4(position, 1.0);
                }
            `,
            fragmentShader: `
                precision mediump float;
                uniform float u_time;
                uniform vec3 u_color1;
                uniform vec3 u_color2;
                uniform vec3 u_glowColor;
                varying vec2 v_texCoord;

                void main() {
                    vec2 uv = v_texCoord;
                    float noise = sin(uv.x * 8.0 + u_time * 0.35) * cos(uv.y * 8.0 - u_time * 0.22);
                    noise += 0.5 * sin(uv.x * 18.0 - u_time * 0.6) * cos(uv.y * 13.0 + u_time * 0.3);
                    vec3 finalColor = mix(u_color1, u_color2, noise * 0.5 + 0.5);
                    float sideGlow = pow(1.0 - abs(uv.x - 0.5) * 2.0, 3.0) * (1.0 - uv.y);
                    finalColor += u_glowColor * sideGlow * 0.05 * (0.6 + 0.4 * sin(u_time * 0.4));
                    gl_FragColor = vec4(finalColor, 1.0);
                }
            `
        });
        const geometry = new THREE.PlaneGeometry(2, 2);
        scene.add(new THREE.Mesh(geometry, material));

        const clock = new THREE.Clock();
        let lastThemeAttr = '';
        let contextLost = false;
        let rafId = null;
        let resizeRaf = null;
        let restoreAttempts = 0;

        function applySize() {
            // Prefer the canvas's laid-out CSS box over window.innerWidth — after a
            // monitor move those can disagree briefly and produce a stretched buffer.
            const rect = canvas.getBoundingClientRect();
            const w = Math.max(1, Math.round(rect.width) || window.innerWidth || 1);
            const h = Math.max(1, Math.round(rect.height) || window.innerHeight || 1);
            renderer.setPixelRatio(effectivePixelRatio());
            renderer.setSize(w, h, false);
            uniforms.u_resolution.value.set(w, h);
        }

        function updateThemeColors() {
            const themeAttr = document.documentElement.getAttribute('data-theme') || 'default';
            if (themeAttr === lastThemeAttr) return;
            lastThemeAttr = themeAttr;
            const styles = getComputedStyle(document.documentElement);
            uniforms.u_color1.value.set(styles.getPropertyValue('--bm-bg').trim() || '#0C0E17');
            uniforms.u_color2.value.set(styles.getPropertyValue('--bm-bg-elevated').trim() || '#121524');
            uniforms.u_glowColor.value.set(styles.getPropertyValue('--bm-accent').trim() || '#818CF8');
        }

        function stop() {
            if (rafId !== null) {
                cancelAnimationFrame(rafId);
                rafId = null;
            }
            if (resizeRaf !== null) {
                cancelAnimationFrame(resizeRaf);
                resizeRaf = null;
            }
            window.removeEventListener('resize', onResize);
            if (window.visualViewport) {
                window.visualViewport.removeEventListener('resize', onResize);
            }
        }

        function animate() {
            rafId = requestAnimationFrame(animate);
            if (contextLost || document.hidden) return;
            updateThemeColors();
            uniforms.u_time.value = clock.getElapsedTime();
            try {
                renderer.render(scene, camera);
            } catch (_) { /* GPU hiccup */ }
        }

        function onResize() {
            scheduleDisplayRepair();
            if (resizeRaf !== null) return;
            resizeRaf = requestAnimationFrame(() => {
                resizeRaf = null;
                if (contextLost || !controller) return;
                applySize();
            });
        }

        function onContextLost(e) {
            e.preventDefault();
            contextLost = true;
            if (rafId !== null) {
                cancelAnimationFrame(rafId);
                rafId = null;
            }
        }

        function onContextRestored() {
            restoreAttempts += 1;
            // After a couple of GPU flips, stop fighting — solid CSS bg is fine.
            if (restoreAttempts > 2) {
                abandonWebGl(canvas);
                scheduleDisplayRepair();
                return;
            }
            disposeController();
            requestAnimationFrame(() => {
                try { window.initBgShader(); } catch (_) { abandonWebGl(canvas); }
                scheduleDisplayRepair();
            });
        }

        if (!canvas._bmShaderContextHooks) {
            canvas._bmShaderContextHooks = true;
            canvas.addEventListener('webglcontextlost', (e) => {
                if (controller && controller.onContextLost) controller.onContextLost(e);
            });
            canvas.addEventListener('webglcontextrestored', () => {
                if (controller && controller.onContextRestored) controller.onContextRestored();
            });
        }

        applySize();
        window.addEventListener('resize', onResize);
        if (window.visualViewport) {
            window.visualViewport.addEventListener('resize', onResize);
        }

        controller = {
            renderer,
            geometry,
            material,
            stop,
            onContextLost,
            onContextRestored
        };

        animate();
    };
})();
