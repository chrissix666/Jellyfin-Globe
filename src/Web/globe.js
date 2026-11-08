// Globe renderer: draws one built globe (column mosaic) into a container.
// The sphere is cut into rings of equal height; each ring into columns, each column into stacked tiles. The fragment
// shader finds ring, column (binary search) and tile for every pixel, so the surface stays seamless.
// Three stages per tile: tiny and small from atlas pages (BC1 for opaque, BC3 for transparent artwork), sharp from
// bundles fetched while zooming in. Transparent artwork shows the globe colour behind it; the polar caps carry the
// Jellyfin logo on that colour.
import * as THREE from './vendor/three.module.js';

const DATA_WIDTH = 4096;              // width of the data textures (rings, columns, tiles, slots)
const POOL_SIZE = 8192;               // sharp-stage pool texture, one for opaque and one for transparent artwork
const FADE = 0.25, MAX_FLIGHT = 6;
const FETCH_PX = 110, SHOW_FROM = 100, SHOW_FULL = 150;   // tile height on screen in pixels
const LOGO_UV = [0.4991, 0.4145], LOGO_UV_R = 0.5836, LOGO_R = 0.45;

export async function createGlobe(container, { base, globeId, color = '#000000', headers = {}, onPlay = () => {}, onSnap = () => {}, onMove = () => {} }) {
  const dataUrl = `${base}data/${globeId}/`;
  const getJson = async name => {
    const response = await fetch(dataUrl + name, { headers });
    if (!response.ok) throw new Error(`globe data ${name}: ${response.status}`);
    return response.json();
  };
  const getFloats = async name => {
    const response = await fetch(dataUrl + name, { headers });
    if (!response.ok) throw new Error(`globe data ${name}: ${response.status}`);
    return new Float32Array(await response.arrayBuffer());
  };
  const [layout, items, rings, columns, tiles] = await Promise.all([
    getJson('layout.json'), getJson('items.json'), getFloats('rings.bin'), getFloats('columns.bin'), getFloats('tiles.bin'),
  ]);

  // Rings, height of a ring in radians, tiles, columns: the shader and the controls use them everywhere.
  const R = layout.ringCount, H = Math.PI / R, N = tiles.length / 16, C = columns.length / 4;
  const kinds = layout.kinds;
  const tiny = layout.atlas.tiny, small = layout.atlas.small;

  // Which column and ring each tile is in (for picking, snapping and the bundles).
  const columnRing = new Int32Array(C), tileColumn = new Int32Array(N);
  for (let k = 0; k < R; k++) {
    const first = rings[k * 4 + 1], count = rings[k * 4 + 2];
    for (let c = first; c < first + count; c++) columnRing[c] = k;
  }
  for (let c = 0; c < C; c++) {
    const first = columns[c * 4 + 2], count = columns[c * 4 + 3];
    for (let t = first; t < first + count; t++) tileColumn[t] = c;
  }

  function tileAt(p) {   // point on the unit sphere -> tile index or -1
    const lat = Math.asin(Math.max(-1, Math.min(1, p.y)));
    const u = ((Math.atan2(p.x, p.z) / (2 * Math.PI)) % 1 + 1) % 1;
    const latN = (lat + Math.PI / 2) / H;
    const k = Math.min(R - 1, Math.floor(latN));
    const count = rings[k * 4 + 2];
    if (!count) return -1;
    const x = ((u - rings[k * 4]) % 1 + 1) % 1;
    let lo = rings[k * 4 + 1], hi = lo + count - 1;
    while (lo < hi) {
      const mid = (lo + hi + 1) >> 1;
      if (columns[mid * 4] <= x) lo = mid; else hi = mid - 1;
    }
    const v = 1 - (latN - k);
    const first = columns[lo * 4 + 2], n = columns[lo * 4 + 3];
    for (let t = first; t < first + n; t++) if (v < tiles[t * 16] + tiles[t * 16 + 1] || t === first + n - 1) return t;
    return -1;
  }

  function tileCentre(t) {
    const c = tileColumn[t], k = columnRing[c];
    const v = tiles[t * 16] + tiles[t * 16 + 1] / 2;
    const lat = -Math.PI / 2 + (k + 1 - v) * H;
    const lon = 2 * Math.PI * (rings[k * 4] + columns[c * 4] + columns[c * 4 + 1] / 2);
    return { lat, lon, dir: new THREE.Vector3(Math.cos(lat) * Math.sin(lon), Math.sin(lat), Math.cos(lat) * Math.cos(lon)) };
  }

  // Graphics
  const background = new THREE.Color(color);
  const renderer = new THREE.WebGLRenderer({ antialias: true });
  renderer.setPixelRatio(window.devicePixelRatio);
  renderer.setSize(innerWidth, innerHeight);
  renderer.setClearColor(background);
  container.appendChild(renderer.domElement);
  const canvas = renderer.domElement;
  canvas.style.cursor = 'grab';
  const gl = renderer.getContext();
  const s3tc = renderer.extensions.get('WEBGL_compressed_texture_s3tc');
  if (!s3tc || renderer.capabilities.maxTextureSize < tiny.pageWidth) {
    renderer.dispose();
    canvas.remove();
    throw Object.assign(new Error('no S3TC textures or no textures of this size on this device'), { unsupported: true });
  }
  const maxAniso = renderer.capabilities.getMaxAnisotropy();
  const scene = new THREE.Scene();
  const camera = new THREE.PerspectiveCamera(40, innerWidth / innerHeight, 0.00005, 100);
  const startLat = layout.startRing === Math.floor((R - 1) / 2) ? 0 : Math.min(-Math.PI / 2 + (layout.startRing + 0.5) * H, THREE.MathUtils.degToRad(80));
  camera.position.set(0, Math.sin(startLat), Math.cos(startLat)).multiplyScalar(3.2);
  camera.lookAt(0, 0, 0);

  function dataTexture(floats) {
    const height = Math.max(1, Math.ceil(floats.length / 4 / DATA_WIDTH));
    const data = new Float32Array(DATA_WIDTH * height * 4);
    data.set(floats);
    const texture = new THREE.DataTexture(data, DATA_WIDTH, height, THREE.RGBAFormat, THREE.FloatType);
    texture.needsUpdate = true;
    return texture;
  }
  const ringData = new Float32Array(R * 4); ringData.set(rings);
  const ringTex = new THREE.DataTexture(ringData, R, 1, THREE.RGBAFormat, THREE.FloatType); ringTex.needsUpdate = true;
  const columnTex = dataTexture(columns);
  const tileTex = dataTexture(tiles);
  const slotTex = dataTexture(new Float32Array(N * 4));
  const slotData = slotTex.image.data;
  const empty = new THREE.DataTexture(new Uint8Array(4), 1, 1); empty.needsUpdate = true;

  function compressed(mips, transparent) {
    const format = transparent ? THREE.RGBA_S3TC_DXT5_Format : THREE.RGB_S3TC_DXT1_Format;
    const texture = new THREE.CompressedTexture(mips, mips[0].width, mips[0].height, format);
    texture.minFilter = mips.length > 1 ? THREE.LinearMipmapLinearFilter : THREE.LinearFilter;
    texture.magFilter = THREE.LinearFilter;
    texture.anisotropy = maxAniso; texture.generateMipmaps = false; texture.needsUpdate = true;
    return texture;
  }
  function mipLevels(buffer, width, height, levels, transparent) {
    const out = []; let offset = 0;
    for (let l = 0; l < levels; l++, width /= 2, height /= 2) {
      const size = width / 4 * height / 4 * (transparent ? 16 : 8);
      out.push({ data: new Uint8Array(buffer, offset, size), width, height });
      offset += size;
    }
    return out;
  }

  // Sharp-stage pools: each kind gets a band of fixed-size slots, sized by its share of the globe.
  const kindCounts = new Array(kinds.length).fill(0);
  for (let t = 0; t < N; t++) kindCounts[tiles[t * 16 + 2]]++;
  function makePool(transparent) {
    const members = kinds.filter(k => k.transparent === transparent && kindCounts[k.index] > 0);
    if (!members.length) return null;
    const area = members.reduce((s, k) => s + kindCounts[k.index] * k.width * k.height, 0);
    const rows = members.map(k => Math.max(1, Math.round(kindCounts[k.index] * k.width * k.height / area * POOL_SIZE / k.height)));
    while (members.reduce((s, k, i) => s + rows[i] * k.height, 0) > POOL_SIZE) {
      const i = rows.indexOf(Math.max(...rows)); if (rows[i] <= 1) break; rows[i]--;
    }
    const bands = {}; let y = 0;
    members.forEach((k, i) => {
      const slots = [];
      for (let r = 0; r < rows[i]; r++) for (let x = 0; x + k.width <= POOL_SIZE; x += k.width) slots.push({ x, y: y + r * k.height, owner: -1, used: 0 });
      bands[k.index] = { slots, free: slots.map((_, j) => j).reverse() };
      y += Math.ceil(rows[i] * k.height / 8) * 8;
    });
    const bytes = transparent ? 1 : 0.5;
    const texture = compressed([
      { data: new Uint8Array(POOL_SIZE * POOL_SIZE * bytes), width: POOL_SIZE, height: POOL_SIZE },
      { data: new Uint8Array(POOL_SIZE * POOL_SIZE * bytes / 4), width: POOL_SIZE / 2, height: POOL_SIZE / 2 },
    ], transparent);
    renderer.initTexture(texture);
    return { texture, bands, transparent };
  }
  const pools = [makePool(false), makePool(true)];

  const pageUniform = (stage, group, page) => `${stage}${group === 'alpha' ? 'A' : 'O'}${page}`;
  const uniforms = {
    ringTex: { value: ringTex }, columnTex: { value: columnTex }, tileTex: { value: tileTex }, slotTex: { value: slotTex },
    poolO: { value: pools[0]?.texture ?? empty }, poolA: { value: pools[1]?.texture ?? empty },
    logo: { value: empty }, base: { value: new THREE.Vector3(background.r, background.g, background.b) },
    tinyO0: { value: empty }, tinyA0: { value: empty },
    smallO0: { value: empty }, smallO1: { value: empty }, smallO2: { value: empty },
    smallA0: { value: empty }, smallA1: { value: empty }, smallA2: { value: empty },
    tinyTime: { value: new THREE.Vector2(1e9, 1e9) },
    smallTimeO: { value: new THREE.Vector3(1e9, 1e9, 1e9) }, smallTimeA: { value: new THREE.Vector3(1e9, 1e9, 1e9) },
    now: { value: 0 }, selected: { value: -1 },
    kindSize: { value: kinds.map(k => new THREE.Vector2(k.width, k.height)) },
  };
  new THREE.TextureLoader().load(base + 'assets/jellyfin-logo.png', texture => {
    texture.colorSpace = THREE.NoColorSpace; texture.anisotropy = maxAniso; uniforms.logo.value = texture;
  });

  const pageHeights = (stage, group) => [0, 1, 2].map(i => (stage[group][i] || 1).toFixed(1));
  const tinyH = [(tiny.opaque[0] || 1).toFixed(1), (tiny.alpha[0] || 1).toFixed(1)];
  const smallHO = pageHeights(small, 'opaque'), smallHA = pageHeights(small, 'alpha');
  const material = new THREE.ShaderMaterial({
    uniforms,
    vertexShader: `
      varying vec3 vPos;
      void main() { vPos = position; gl_Position = projectionMatrix * modelViewMatrix * vec4(position, 1.0); }`,
    fragmentShader: `
      uniform sampler2D ringTex, columnTex, tileTex, slotTex, poolO, poolA, logo;
      uniform sampler2D tinyO0, tinyA0, smallO0, smallO1, smallO2, smallA0, smallA1, smallA2;
      uniform vec3 base; uniform vec2 tinyTime; uniform vec3 smallTimeO, smallTimeA;
      uniform float now; uniform int selected; uniform vec2 kindSize[${kinds.length}];
      varying vec3 vPos;
      const float PI = 3.141592653589793, R = ${R}.0, H = ${H}, PAGE_W = ${tiny.pageWidth}.0;

      vec4 texel(sampler2D t, int i) { return texelFetch(t, ivec2(i % ${DATA_WIDTH}, i / ${DATA_WIDTH}), 0); }
      vec2 inset(vec2 uv, vec2 size) { return clamp(uv, 0.5 / size, 1.0 - 0.5 / size); }
      vec3 over(vec4 c) { return base * (1.0 - c.a) + c.rgb; }

      vec4 tinySample(bool alpha, vec4 rect, vec2 uv, vec2 gx, vec2 gy) {
        vec2 page = vec2(PAGE_W, alpha ? ${tinyH[1]} : ${tinyH[0]});
        vec2 at = (rect.xy + inset(uv, rect.zw) * rect.zw) / page, sx = gx * rect.zw / page, sy = gy * rect.zw / page;
        return alpha ? textureGrad(tinyA0, at, sx, sy) : vec4(textureGrad(tinyO0, at, sx, sy).rgb, 1.0);
      }
      vec4 smallSample(bool alpha, int p, vec4 rect, vec2 uv, vec2 gx, vec2 gy) {
        float h = alpha ? (p == 0 ? ${smallHA[0]} : p == 1 ? ${smallHA[1]} : ${smallHA[2]})
                        : (p == 0 ? ${smallHO[0]} : p == 1 ? ${smallHO[1]} : ${smallHO[2]});
        vec2 page = vec2(PAGE_W, h);
        vec2 at = (rect.xy + inset(uv, rect.zw) * rect.zw) / page, sx = gx * rect.zw / page, sy = gy * rect.zw / page;
        if (alpha) return p == 0 ? textureGrad(smallA0, at, sx, sy) : p == 1 ? textureGrad(smallA1, at, sx, sy) : textureGrad(smallA2, at, sx, sy);
        vec3 c = p == 0 ? textureGrad(smallO0, at, sx, sy).rgb : p == 1 ? textureGrad(smallO1, at, sx, sy).rgb : textureGrad(smallO2, at, sx, sy).rgb;
        return vec4(c, 1.0);
      }

      void main() {
        vec3 p = normalize(vPos);
        float lat = asin(clamp(p.y, -1.0, 1.0));
        float u = fract(atan(p.x, p.z) / (2.0 * PI) + 1.0);
        float latN = (lat + PI / 2.0) / H;
        float dux = dFdx(u), duy = dFdy(u);
        dux -= sign(dux) * step(0.5, abs(dux)); duy -= sign(duy) * step(0.5, abs(duy));
        float dlx = dFdx(latN), dly = dFdy(latN);
        int k = clamp(int(floor(latN)), 0, int(R) - 1);
        if (k == 0 || k == int(R) - 1) {   // polar cap: the Jellyfin logo, centred by its triangle, on the globe colour
          float north = k == 0 ? -1.0 : 1.0;
          float s = ${LOGO_UV_R} / (sin(H) * ${LOGO_R});
          vec2 q = vec2(${LOGO_UV[0]} + p.x * s, ${LOGO_UV[1]} + north * p.z * s);
          vec4 mark = texture(logo, q);
          float inside = step(0.0, q.x) * step(q.x, 1.0) * step(0.0, q.y) * step(q.y, 1.0);
          gl_FragColor = vec4(mix(base, mark.rgb, mark.a * inside), 1.0);
          return;
        }
        vec4 ring = texelFetch(ringTex, ivec2(k, 0), 0);
        int count = int(ring.z);
        if (count == 0) { gl_FragColor = vec4(base, 1.0); return; }
        float x = fract(u - ring.x + 1.0);
        int lo = int(ring.y), hi = lo + count - 1;
        for (int i = 0; i < 14 && lo < hi; i++) {
          int mid = (lo + hi + 1) / 2;
          if (texel(columnTex, mid).x <= x) lo = mid; else hi = mid - 1;
        }
        vec4 column = texel(columnTex, lo);
        float v = 1.0 - (latN - float(k));
        int first = int(column.z), n = int(column.w), t = first;
        vec4 a = texel(tileTex, first * 4);
        for (int j = 1; j < 8; j++) {
          if (j >= n || v < a.x + a.y) break;
          t = first + j; a = texel(tileTex, t * 4);
        }
        vec4 tinyRect = texel(tileTex, t * 4 + 1), smallRect = texel(tileTex, t * 4 + 2), extra = texel(tileTex, t * 4 + 3);
        bool alpha = extra.z > 0.5;
        vec2 uv = vec2((x - column.x) / column.y, (v - a.x) / a.y);
        vec2 gx = vec2(dux / column.y, -dlx / a.y), gy = vec2(duy / column.y, -dly / a.y);
        float tilePx = 1.0 / max(length(vec2(gx.y, gy.y)), 1e-6);

        vec3 col = base;
        int smallPage = int(extra.y);
        float smallFade = clamp((now - (alpha ? smallTimeA[smallPage] : smallTimeO[smallPage])) / ${FADE}, 0.0, 1.0);
        if (smallFade < 1.0) {
          float tinyFade = clamp((now - (alpha ? tinyTime.y : tinyTime.x)) / ${FADE}, 0.0, 1.0);
          if (tinyFade > 0.0) col = mix(col, over(tinySample(alpha, tinyRect, uv, gx, gy)), tinyFade);
        }
        if (smallFade > 0.0) col = mix(col, over(smallSample(alpha, smallPage, smallRect, uv, gx, gy)), smallFade);
        vec4 slot = texel(slotTex, t);
        if (slot.w > 0.0) {
          float sharpFade = clamp((now - slot.z) / ${FADE}, 0.0, 1.0) * smoothstep(${SHOW_FROM}.0, ${SHOW_FULL}.0, tilePx);
          if (sharpFade > 0.0) {
            vec2 size = kindSize[int(a.z)];
            vec2 at = (slot.xy + inset(uv, size) * size) / ${POOL_SIZE}.0;
            vec2 sx = gx * size / ${POOL_SIZE}.0, sy = gy * size / ${POOL_SIZE}.0;
            vec4 c = alpha ? textureGrad(poolA, at, sx, sy) : vec4(textureGrad(poolO, at, sx, sy).rgb, 1.0);
            col = mix(col, over(c), sharpFade);
          }
        }
        if (t == selected) {   // frame of the selected tile, 3 pixels
          vec2 px = vec2(1.0 / max(length(vec2(gx.x, gy.x)), 1e-6), tilePx);
          vec2 edge = min(uv, 1.0 - uv) * px;
          if (min(edge.x, edge.y) < 3.0) col = vec3(1.0, 0.85, 0.2);
        }
        gl_FragColor = vec4(col, 1.0);
      }`,
  });
  scene.add(new THREE.Mesh(new THREE.SphereGeometry(1, 720, 360), material));

  // Tiny and small stage: complete at start
  let disposed = false;
  const nowS = () => performance.now() / 1000;
  async function loadPage(stageName, stage, group, page) {
    const transparent = group === 'alpha';
    const response = await fetch(`${dataUrl}${stageName}-${group}-${page}.bin`, { headers });
    if (!response.ok) throw new Error(`globe page ${stageName}-${group}-${page}: ${response.status}`);
    const buffer = await response.arrayBuffer();
    const texture = compressed(mipLevels(buffer, stage.pageWidth, stage[group][page], stage.levels, transparent), transparent);
    renderer.initTexture(texture);
    return texture;
  }
  (async () => {
    for (const group of ['opaque', 'alpha']) {
      if (!tiny[group].length || disposed) continue;
      uniforms[pageUniform('tiny', group, 0)].value = await loadPage('tiny', tiny, group, 0);
      uniforms.tinyTime.value.setComponent(group === 'alpha' ? 1 : 0, nowS());
    }
    for (const group of ['opaque', 'alpha']) {
      for (let page = 0; page < small[group].length && !disposed; page++) {
        uniforms[pageUniform('small', group, page)].value = await loadPage('small', small, group, page);
        uniforms[group === 'alpha' ? 'smallTimeA' : 'smallTimeO'].value.setComponent(page, nowS());
      }
    }
  })().catch(error => console.warn('Globe:', error));

  // Sharp stage: bundles of 16 neighbouring tiles, outdated requests aborted
  const bundleSize = layout.bundleSize;
  const slotOf = new Map(), inFlight = new Map();
  let slotDirty = false;
  function takeSlot(kind, now) {
    const pool = pools[kinds[kind].transparent ? 1 : 0], band = pool.bands[kind];
    if (band.free.length) return band.slots[band.free.pop()];
    let best = null;
    for (const slot of band.slots) if (slot.used < now - 300 && (!best || slot.used < best.used)) best = slot;
    if (!best) return null;
    slotOf.delete(best.owner); slotData[best.owner * 4 + 3] = 0;
    return best;
  }
  function requestBundle(b) {
    const request = new AbortController();
    inFlight.set(b, request);
    fetch(`${dataUrl}bundle/${b}?build=${layout.fingerprint}:${layout.storeGeneration ?? 0}`, { headers, signal: request.signal }).then(r => {
      if (!r.ok) throw new Error(`globe bundle ${b}: ${r.status}`);
      return r.arrayBuffer();
    }).then(buffer => {
      if (request.signal.aborted || disposed) return;
      const now = performance.now();
      let offset = 0;
      for (let t = b * bundleSize; t < Math.min(N, (b + 1) * bundleSize); t++) {
        const kind = kinds[tiles[t * 16 + 2]];
        const start = offset; offset += kind.bytes;
        if (slotOf.has(t)) continue;
        const slot = takeSlot(kind.index, now);
        if (!slot) continue;
        const pool = pools[kind.transparent ? 1 : 0];
        const level0 = kind.width * kind.height * (kind.transparent ? 1 : 0.5);
        const format = kind.transparent ? s3tc.COMPRESSED_RGBA_S3TC_DXT5_EXT : s3tc.COMPRESSED_RGB_S3TC_DXT1_EXT;
        renderer.state.bindTexture(gl.TEXTURE_2D, renderer.properties.get(pool.texture).__webglTexture);
        gl.compressedTexSubImage2D(gl.TEXTURE_2D, 0, slot.x, slot.y, kind.width, kind.height, format, new Uint8Array(buffer, start, level0));
        gl.compressedTexSubImage2D(gl.TEXTURE_2D, 1, slot.x / 2, slot.y / 2, kind.width / 2, kind.height / 2, format, new Uint8Array(buffer, start + level0, level0 / 4));
        slot.owner = t; slot.used = now; slotOf.set(t, slot);
        slotData.set([slot.x, slot.y, now / 1000, 1], t * 4);
      }
      slotDirty = true;
    }).catch(() => {}).finally(() => { if (inFlight.get(b) === request) inFlight.delete(b); });
  }

  const ray = new THREE.Raycaster(), ndc = new THREE.Vector2();
  const sphere = new THREE.Sphere(new THREE.Vector3(), 1), hit = new THREE.Vector3();
  const pxPerRad = () => (canvas.height / 2) / Math.tan(THREE.MathUtils.degToRad(camera.fov / 2));
  const ringPx = () => H / (camera.position.length() - 1) * pxPerRad();
  function updateSharp() {
    const now = performance.now(), px = ringPx();
    const want = new Map();
    const GX = 48, GY = 28, MARGIN = 1.2, seen = new Set();
    for (let iy = 0; iy <= GY; iy++) for (let ix = 0; ix <= GX; ix++) {
      ndc.set((ix / GX * 2 - 1) * MARGIN, (iy / GY * 2 - 1) * MARGIN);
      ray.setFromCamera(ndc, camera);
      if (!ray.ray.intersectSphere(sphere, hit)) continue;
      const t = tileAt(hit.normalize());
      if (t < 0 || seen.has(t)) continue;
      seen.add(t);
      const slot = slotOf.get(t);
      if (slot) { slot.used = now; continue; }
      if (px * tiles[t * 16 + 1] < FETCH_PX) continue;
      const d = ndc.x * ndc.x + ndc.y * ndc.y, b = Math.floor(t / bundleSize);
      if (!want.has(b) || want.get(b) > d) want.set(b, d);
    }
    for (const [b, request] of inFlight) if (!want.has(b)) { request.abort(); inFlight.delete(b); }
    for (const [b] of [...want].sort((x, y) => x[1] - y[1])) {
      if (inFlight.size >= MAX_FLIGHT) break;
      if (!inFlight.has(b)) requestBundle(b);
    }
  }

  // Controls (Google Earth style)
  const dist = () => camera.position.length();
  const MAX_LAT = THREE.MathUtils.degToRad(89.5);
  function camLatLon() {
    const p = camera.position.clone().normalize();
    return { lat: Math.asin(THREE.MathUtils.clamp(p.y, -1, 1)), lon: Math.atan2(p.x, p.z) };
  }
  function setCam(lat, lon) {
    lat = THREE.MathUtils.clamp(lat, -MAX_LAT, MAX_LAT);
    const d = dist();
    camera.position.set(Math.cos(lat) * Math.sin(lon), Math.sin(lat), Math.cos(lat) * Math.cos(lon)).multiplyScalar(d);
    camera.lookAt(0, 0, 0);
    camera.updateMatrixWorld();
  }
  function surfaceAt(x, y) {
    ndc.set(x / innerWidth * 2 - 1, -y / innerHeight * 2 + 1);
    ray.setFromCamera(ndc, camera);
    const p = new THREE.Vector3();
    if (!ray.ray.intersectSphere(sphere, p)) ray.ray.closestPointToPoint(new THREE.Vector3(), p);
    p.normalize();
    return { lat: Math.asin(THREE.MathUtils.clamp(p.y, -1, 1)), lon: Math.atan2(p.x, p.z) };
  }
  const wrap = a => Math.atan2(Math.sin(a), Math.cos(a));
  let grab = null, spin = null, snap = null;
  canvas.addEventListener('pointerdown', e => {
    if (e.button !== 0) return;
    onMove();
    canvas.setPointerCapture(e.pointerId);
    snap = null; spin = null;
    grab = { p: surfaceAt(e.clientX, e.clientY), hist: [] };
    canvas.style.cursor = 'grabbing';
  });
  canvas.addEventListener('pointermove', e => {
    if (!grab) return;
    for (let i = 0; i < 3; i++) {
      const q = surfaceAt(e.clientX, e.clientY), c = camLatLon();
      setCam(c.lat + (grab.p.lat - q.lat), c.lon + wrap(grab.p.lon - q.lon));
    }
    const c = camLatLon(), t = performance.now();
    grab.hist.push([t, c.lat, c.lon]);
    while (grab.hist.length > 2 && t - grab.hist[0][0] > 80) grab.hist.shift();
  });
  canvas.addEventListener('pointerup', e => {
    if (!grab || e.button !== 0) return;
    const h = grab.hist, t = performance.now();
    grab = null;
    canvas.style.cursor = 'grab';
    if (h.length < 2 || t - h[h.length - 1][0] > 60) return;
    const [startTime, startLat, startLon] = h[0], [endTime, endLat, endLon] = h[h.length - 1];
    if (endTime - startTime < 1) return;
    spin = { vLat: (endLat - startLat) / (endTime - startTime), vLon: wrap(endLon - startLon) / (endTime - startTime), last: t };
  });
  canvas.addEventListener('pointercancel', () => {
    grab = null;
    canvas.style.cursor = 'grab';
  });
  function spinStep(now) {
    const dt = now - spin.last, f = Math.exp(-dt / 325);
    spin.last = now;
    const c = camLatLon();
    setCam(c.lat + spin.vLat * dt, c.lon + spin.vLon * dt);
    spin.vLat *= f; spin.vLon *= f;
    if (Math.hypot(spin.vLat, spin.vLon) * 1000 < 1e-4) spin = null;
  }
  canvas.addEventListener('wheel', e => {
    e.preventDefault();
    onMove();
    camera.position.setLength(1 + Math.min(20, Math.max(0.0003, (dist() - 1) * Math.pow(1.0015, e.deltaY))));
  }, { passive: false });

  // Snap (right-click) and open (double-click)
  function tileScreenPx(t) {
    const c = tileCentre(t), half = H * tiles[t * 16 + 1] / 2;
    const pt = lat => new THREE.Vector3(Math.cos(lat) * Math.sin(c.lon), Math.sin(lat), Math.cos(lat) * Math.cos(c.lon)).project(camera);
    const a = pt(c.lat - half), b = pt(c.lat + half);
    return Math.hypot((a.x - b.x) * canvas.width / 2, (a.y - b.y) * canvas.height / 2);
  }
  // The tile's box on the screen in CSS pixels (its four corners projected), for an overlay that lies over it.
  function tileRect(t) {
    const c = tileCentre(t), half = H * tiles[t * 16 + 1] / 2, halfLon = Math.PI * columns[tileColumn[t] * 4 + 1];
    const xs = [], ys = [];
    for (const lat of [c.lat - half, c.lat + half]) {
      for (const lon of [c.lon - halfLon, c.lon + halfLon]) {
        const p = new THREE.Vector3(Math.cos(lat) * Math.sin(lon), Math.sin(lat), Math.cos(lat) * Math.cos(lon)).project(camera);
        xs.push((p.x + 1) / 2 * canvas.clientWidth);
        ys.push((1 - p.y) / 2 * canvas.clientHeight);
      }
    }
    const x = Math.min(...xs), y = Math.min(...ys);
    return { x, y, width: Math.max(...xs) - x, height: Math.max(...ys) - y };
  }
  function pick(e) {
    ndc.set(e.clientX / innerWidth * 2 - 1, -e.clientY / innerHeight * 2 + 1);
    ray.setFromCamera(ndc, camera);
    return ray.ray.intersectSphere(sphere, hit) ? tileAt(hit.normalize()) : -1;
  }
  function snapTo(t) {
    uniforms.selected.value = t;
    spin = null;
    onMove();
    snap = { tile: t, from: camera.position.clone().normalize(), to: tileCentre(t).dir, t0: performance.now() };
  }
  function snapStep(now) {
    const x = Math.min(1, (now - snap.t0) / 400), e = x < .5 ? 2 * x * x : 1 - (-2 * x + 2) ** 2 / 2;
    const w = Math.acos(Math.min(1, snap.from.dot(snap.to)));
    const dir = w < 1e-6 ? snap.to.clone() : snap.from.clone().multiplyScalar(Math.sin((1 - e) * w) / Math.sin(w))
      .add(snap.to.clone().multiplyScalar(Math.sin(e * w) / Math.sin(w)));
    camera.position.copy(dir.normalize().multiplyScalar(camera.position.length()));
    camera.lookAt(0, 0, 0);
    if (x >= 1) {
      const t = snap.tile;
      snap = null;
      onSnap({ tile: t, animated: tiles[t * 16 + 15] > 0, rect: tileRect(t) });
    }
  }
  canvas.addEventListener('contextmenu', e => {
    e.preventDefault();
    const t = pick(e);
    if (t >= 0) snapTo(t);
  });
  canvas.addEventListener('dblclick', e => {
    const t = pick(e);
    if (t < 0) return;
    if (tileScreenPx(t) < 40) { snapTo(t); return; }
    uniforms.selected.value = t;
    onPlay(items[tiles[t * 16 + 3]]);
  });

  const onResize = () => {
    camera.aspect = innerWidth / innerHeight; camera.updateProjectionMatrix();
    renderer.setSize(innerWidth, innerHeight);
  };
  addEventListener('resize', onResize);

  // Loop
  let lastSharp = 0;
  const loop = now => {
    if (snap) snapStep(now);
    else if (spin) spinStep(now);
    if (now - lastSharp > 100) { updateSharp(); lastSharp = now; }
    if (slotDirty) { slotTex.needsUpdate = true; slotDirty = false; }
    uniforms.now.value = now / 1000;
    renderer.render(scene, camera);
  };
  renderer.setAnimationLoop(loop);

  return {
    pause() { renderer.setAnimationLoop(null); },
    resume() { onResize(); renderer.setAnimationLoop(loop); },
    dispose() {
      disposed = true;
      renderer.setAnimationLoop(null);
      removeEventListener('resize', onResize);
      for (const request of inFlight.values()) request.abort();
      renderer.dispose();
      renderer.forceContextLoss();
      canvas.remove();
    },
  };
}
