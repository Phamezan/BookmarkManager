// WebGL Shader Setup
window.initBgShader = function() {
    if (window.bgShaderInitialized) return;
    const canvas = document.getElementById('bg-canvas');
    if (!canvas) {
        console.warn('WebGL Background Canvas (#bg-canvas) not found in DOM.');
        return;
    }

    const renderer = new THREE.WebGLRenderer({ canvas, alpha: true });
    window.bgShaderInitialized = true;
    
    // Set initial size
    renderer.setSize(window.innerWidth, window.innerHeight);
    renderer.setPixelRatio(window.devicePixelRatio);

    const scene = new THREE.Scene();
    const camera = new THREE.OrthographicCamera(-1, 1, 1, -1, 0, 1);
    
    // Shader Uniforms
    const uniforms = {
        u_time: { value: 0.0 },
        u_resolution: { value: new THREE.Vector2(window.innerWidth, window.innerHeight) },
        u_color1: { value: new THREE.Color('#0C0E17') },
        u_color2: { value: new THREE.Color('#121524') },
        u_glowColor: { value: new THREE.Color('#27355c') }
    };

    // Handle resize
    window.addEventListener('resize', () => {
        renderer.setSize(window.innerWidth, window.innerHeight);
        uniforms.u_resolution.value.set(window.innerWidth, window.innerHeight);
    });

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
            precision highp float;
            uniform float u_time;
            uniform vec2 u_resolution;
            uniform vec3 u_color1;
            uniform vec3 u_color2;
            uniform vec3 u_glowColor;
            varying vec2 v_texCoord;

            void main() {
                vec2 uv = v_texCoord;
                
                // Flowing premium dark noise pattern
                float noise = sin(uv.x * 8.0 + u_time * 0.35) * cos(uv.y * 8.0 - u_time * 0.22);
                noise += 0.5 * sin(uv.x * 18.0 - u_time * 0.6) * cos(uv.y * 13.0 + u_time * 0.3);

                // Theme-responsive background colors
                vec3 color1 = u_color1;
                vec3 color2 = u_color2;
                vec3 indigo = u_glowColor;

                vec3 finalColor = mix(color1, color2, noise * 0.5 + 0.5);

                // Subtle indigo glow rising from below
                float glow = smoothstep(0.0, 1.0, uv.y) * 0.5 + smoothstep(1.0, 0.0, uv.y) * 0.5;
                float sideGlow = pow(1.0 - abs(uv.x - 0.5) * 2.0, 3.0) * (1.0 - uv.y);
                finalColor += indigo * sideGlow * 0.06 * (0.6 + 0.4 * sin(u_time * 0.4));

                // Soft scanline for texture
                float scanline = sin(v_texCoord.y * 900.0) * 0.015;
                finalColor -= scanline;

                gl_FragColor = vec4(finalColor, 1.0);
            }
        `
    });

    const geometry = new THREE.PlaneGeometry(2, 2);
    const mesh = new THREE.Mesh(geometry, material);
    scene.add(mesh);

    // Animation loop
    const clock = new THREE.Clock();
    let lastThemeAttr = '';

    function updateThemeColors() {
        const themeAttr = document.documentElement.getAttribute('data-theme') || 'default';
        if (themeAttr !== lastThemeAttr) {
            lastThemeAttr = themeAttr;
            const styles = getComputedStyle(document.documentElement);
            
            const bg = styles.getPropertyValue('--bm-bg').trim() || '#0C0E17';
            const bgElevated = styles.getPropertyValue('--bm-bg-elevated').trim() || '#121524';
            const accent = styles.getPropertyValue('--bm-accent').trim() || '#818CF8';
            
            // Set Color values
            uniforms.u_color1.value.set(bg);
            uniforms.u_color2.value.set(bgElevated);
            uniforms.u_glowColor.value.set(accent);
        }
    }

    function animate() {
        requestAnimationFrame(animate);
        updateThemeColors();
        uniforms.u_time.value = clock.getElapsedTime();
        renderer.render(scene, camera);
    }
    
    animate();
};
