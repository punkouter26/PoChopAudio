// Browser-side background removal using onnxruntime-web and the u2netp model.
// Loaded once via <script src="js/cutout.js"> from the Blazor WASM shell.
// The model is downloaded on first use and cached in IndexedDB.

(function () {
    'use strict';

    // Served by the API from Content/Models. The old '_content/cutout-models/…' path was the Razor
    // class library convention for a library this solution does not have, so it always 404'd and the
    // browser engine never once loaded a model.
    const MODEL_URL = 'api/cutout/model';
    const MODEL_BYTES_KEY = 'pochopaudio:u2netp-model';
    const MODEL_VERSION = 1;

    let sessionPromise = null;

    // The onnxruntime module itself, not just the session: runMask needs ort.Tensor, and holding it
    // only inside getSession's closure is what produced "ort is not defined" at inference time.
    let ort = null;

    /// The IndexedDB copy is an optimisation, never a requirement. Private-browsing modes and
    /// blocked site data make the whole API throw, and losing the cache should cost a re-download,
    /// not the feature.
    async function loadModelFromCache() {
        try {
            return await readModelFromCache();
        } catch {
            return null;
        }
    }

    async function readModelFromCache() {
        if (typeof indexedDB === 'undefined') {
            return null;
        }

        const db = await openDb();
        const tx = db.transaction(MODEL_BYTES_KEY, 'readonly');
        const store = tx.objectStore(MODEL_BYTES_KEY);
        const meta = await reqAsPromise(store.get('meta'));
        const data = await reqAsPromise(store.get('data'));
        if (!meta || !data || meta.version !== MODEL_VERSION) return null;
        return data;
    }

    /// Best-effort, for the same reason as the read: failing to cache 4.4 MB is not a reason to
    /// fail the cutout the user is waiting on.
    async function saveModelToCache(bytes) {
        if (typeof indexedDB === 'undefined') {
            return;
        }

        try {
            const db = await openDb();
            const tx = db.transaction(MODEL_BYTES_KEY, 'readwrite');
            const store = tx.objectStore(MODEL_BYTES_KEY);
            store.put({ id: 'meta', version: MODEL_VERSION, bytes: bytes.byteLength }, 'meta');
            store.put(bytes, 'data');
            await txDone(tx);
        } catch {
            // Quota exceeded, private mode, or storage blocked. The model re-downloads next time.
        }
    }

    function openDb() {
        return new Promise((resolve, reject) => {
            const req = indexedDB.open('pochopaudio', 1);
            req.onupgradeneeded = () => {
                const db = req.result;
                if (!db.objectStoreNames.contains(MODEL_BYTES_KEY)) {
                    db.createObjectStore(MODEL_BYTES_KEY);
                }
            };
            req.onsuccess = () => resolve(req.result);
            req.onerror = () => reject(req.error);
        });
    }

    function reqAsPromise(req) {
        return new Promise((resolve, reject) => {
            req.onsuccess = () => resolve(req.result);
            req.onerror = () => reject(req.error);
        });
    }

    function txDone(tx) {
        return new Promise((resolve, reject) => {
            tx.oncomplete = () => resolve();
            tx.onerror = () => reject(tx.error);
        });
    }

    async function getSession() {
        if (sessionPromise) return sessionPromise;

        sessionPromise = (async () => {
            // Load onnxruntime-web lazily. CDN fallback because we don't want to vendor its 6 MB of wasm.
            ort = await import('https://cdn.jsdelivr.net/npm/onnxruntime-web@1.20.0/dist/ort.min.mjs');
            ort.env.wasm.wasmPaths = 'https://cdn.jsdelivr.net/npm/onnxruntime-web@1.20.0/dist/';

            let modelBytes = await loadModelFromCache();
            if (!modelBytes) {
                const response = await fetch(MODEL_URL);
                if (!response.ok) {
                    throw new Error(response.status === 404
                        ? 'The server has no u2netp.onnx. Run SCRIPTS/download-models.ps1 and restart.'
                        : `Model download failed: ${response.status}`);
                }

                // A Blazor host answers any unmatched GET with index.html and a cheerful 200. Handing
                // that to the ONNX parser produces a baffling "invalid protobuf" instead of the real
                // problem, which is that the model is not being served.
                const type = response.headers.get('content-type') || '';
                if (type.includes('text/html')) {
                    throw new Error(`Expected the ONNX model at ${MODEL_URL} but got an HTML page.`);
                }

                modelBytes = new Uint8Array(await response.arrayBuffer());
                await saveModelToCache(modelBytes);
            }

            return ort.InferenceSession.create(modelBytes, { executionProviders: ['wasm'] });
        })();

        // A rejected promise left in sessionPromise would be handed to every later call, so one
        // transient failure would make Re-cut permanently useless. Clear it and let the next
        // attempt start over.
        sessionPromise.catch(() => {
            sessionPromise = null;
        });

        return sessionPromise;
    }

    async function decodeImageBytes(bytes) {
        const blob = new Blob([bytes]);
        const url = URL.createObjectURL(blob);
        try {
            const img = await new Promise((resolve, reject) => {
                const i = new Image();
                i.onload = () => resolve(i);
                i.onerror = () => reject(new Error('Could not decode image.'));
                i.src = url;
            });
            const canvas = document.createElement('canvas');
            canvas.width = img.naturalWidth;
            canvas.height = img.naturalHeight;
            const ctx = canvas.getContext('2d');
            ctx.drawImage(img, 0, 0);
            return {
                rgba: ctx.getImageData(0, 0, img.naturalWidth, img.naturalHeight).data,
                width: img.naturalWidth,
                height: img.naturalHeight,
            };
        } finally {
            URL.revokeObjectURL(url);
        }
    }

    function bytesToBase64(bytes) {
        let binary = '';
        const chunk = 0x8000;
        for (let i = 0; i < bytes.length; i += chunk) {
            binary += String.fromCharCode.apply(null, bytes.subarray(i, i + chunk));
        }
        return btoa(binary);
    }

    async function encodePng(rgba, width, height) {
        const canvas = document.createElement('canvas');
        canvas.width = width;
        canvas.height = height;
        const ctx = canvas.getContext('2d');
        ctx.putImageData(new ImageData(rgba, width, height), 0, 0);
        return new Promise((resolve, reject) => {
            canvas.toBlob(blob => {
                if (!blob) {
                    reject(new Error('PNG encode failed.'));
                    return;
                }
                blob.arrayBuffer().then(buffer => resolve(bytesToBase64(new Uint8Array(buffer))));
            }, 'image/png');
        });
    }

    async function removeBackgroundBytes(bytes) {
        const { rgba, width, height } = await decodeImageBytes(bytes);
        const masked = await runMask(rgba, width, height);
        return encodePng(masked, width, height);
    }

    async function runMask(rgba, width, height) {
        const session = await getSession();
        const INPUT = 320;

        // Per-channel ImageNet statistics, as u2net was trained with. Reusing the red channel's
        // numbers for green and blue tints the input and costs mask quality for nothing.
        const mean = [0.485, 0.456, 0.406];
        const std = [0.229, 0.224, 0.225];
        const input = new Float32Array(1 * 3 * INPUT * INPUT);

        const stepX = width / INPUT;
        const stepY = height / INPUT;

        for (let y = 0; y < INPUT; y++) {
            for (let x = 0; x < INPUT; x++) {
                const sx = Math.min(width - 1, Math.floor(x * stepX));
                const sy = Math.min(height - 1, Math.floor(y * stepY));
                const src = (sy * width + sx) * 4;
                const base = y * INPUT + x;
                input[(0 * INPUT * INPUT) + base] = ((rgba[src + 0] / 255) - mean[0]) / std[0];
                input[(1 * INPUT * INPUT) + base] = ((rgba[src + 1] / 255) - mean[1]) / std[1];
                input[(2 * INPUT * INPUT) + base] = ((rgba[src + 2] / 255) - mean[2]) / std[2];
            }
        }

        const tensor = new ort.Tensor('float32', input, [1, 3, INPUT, INPUT]);

        // Ask the session what its input is actually called. This build of u2netp names it
        // "input.1"; hardcoding "input" failed every run with "input 'input.1' is missing in feeds".
        const inputName = (session.inputNames && session.inputNames[0]) || 'input';
        const outputs = await session.run({ [inputName]: tensor });

        // Likewise the first output: u2netp emits several side outputs and d0 comes first.
        const outputName = (session.outputNames && session.outputNames[0]) || Object.keys(outputs)[0];
        const mask = outputs[outputName].data;

        // u2net emits an unnormalised saliency map, not probabilities. Stretching it to 0..1 is the
        // standard post-step (rembg does the same); without it the alpha is flat and washed out.
        let lo = Infinity;
        let hi = -Infinity;
        for (let i = 0; i < mask.length; i++) {
            if (mask[i] < lo) lo = mask[i];
            if (mask[i] > hi) hi = mask[i];
        }

        const span = hi - lo;

        const out = new Uint8ClampedArray(width * height * 4);
        for (let y = 0; y < height; y++) {
            // The mask is INPUT x INPUT regardless of the photo's size, so it has to be sampled back
            // up. Indexing it by the full-resolution pixel number instead ran off the end of a
            // 102,400-entry array on anything bigger than 320x320 and left the rest transparent.
            const my = Math.min(INPUT - 1, Math.floor(y * INPUT / height));
            for (let x = 0; x < width; x++) {
                const mx = Math.min(INPUT - 1, Math.floor(x * INPUT / width));
                const value = span > 0 ? (mask[(my * INPUT) + mx] - lo) / span : 0;
                const p = ((y * width) + x) * 4;

                out[p + 0] = rgba[p + 0];
                out[p + 1] = rgba[p + 1];
                out[p + 2] = rgba[p + 2];
                out[p + 3] = Math.round(Math.max(0, Math.min(1, value)) * 255);
            }
        }

        return out;
    }

    /// Whether this browser can actually run a cutout. It used to answer an unconditional true,
    /// which is how four head shots in a row got as far as reporting a model 404 each: the caller
    /// had already been told the engine was fine. A HEAD against the model is cheap and honest.
    async function isAvailable() {
        if (typeof WebAssembly === 'undefined') {
            return false;
        }

        try {
            const response = await fetch(MODEL_URL, { method: 'HEAD' });
            return response.ok && !(response.headers.get('content-type') || '').includes('text/html');
        } catch {
            return false;
        }
    }

    window.pochopaudio = window.pochopaudio || {};
    window.pochopaudio.cutout = {
        isAvailable,
        removeBackgroundBytes,
    };
})();
