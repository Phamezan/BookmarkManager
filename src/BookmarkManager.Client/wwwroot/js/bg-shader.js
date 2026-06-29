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
        u_resolution: { value: new THREE.Vector2(window.innerWidth, window.innerHeight) }
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
            varying vec2 v_texCoord;

            void main() {
                vec2 uv = v_texCoord;
                
                // Create a flowing tech-inspired noise pattern
                float noise = sin(uv.x * 10.0 + u_time * 0.5) * cos(uv.y * 10.0 - u_time * 0.3);
                noise += 0.5 * sin(uv.x * 20.0 - u_time * 0.8) * cos(uv.y * 15.0 + u_time * 0.4);
                
                // Deep slate/charcoal base palette from Command Center
                vec3 color1 = vec3(0.07, 0.07, 0.07); // Surface
                vec3 color2 = vec3(0.11, 0.11, 0.13); // Surface container
                
                vec3 finalColor = mix(color1, color2, noise * 0.5 + 0.5);
                
                // Add subtle scanline effect
                float scanline = sin(v_texCoord.y * 800.0) * 0.02;
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
    function animate() {
        requestAnimationFrame(animate);
        uniforms.u_time.value = clock.getElapsedTime();
        renderer.render(scene, camera);
    }
    
    animate();
};
