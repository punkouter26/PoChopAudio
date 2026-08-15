// Browser-side background removal using onnxruntime-web and the u2netp model.
// Loaded once via <script src="js/cutout.js"> from the Blazor WASM shell.
// The model is downloaded on first use and cached in IndexedDB.

(function () {
    'use strict';

    const MODEL_URL = '_content/cutout-models/u2netp.onnx';
    const MODEL_BYTES_KEY = 'pochopaudio:u2netp-model';
    const MODEL_VERSION = 1;

    let sessionPromise = null;

    async function loadModelFromCache() {
        const db = await openDb();
        const tx = db.transaction(MODEL_BYTES_KEY, 'readonly');
        const store = tx.objectStore(MODEL_BYTES_KEY);
        const meta = await reqAsPromise(store.get('meta'));
        const data = await reqAsPromise(store.get('data'));
        if (!meta || !data || meta.version !== MODEL_VERSION) return null;
        return data;
    }

    async function saveModelToCache(bytes) {
        const db = await openDb();
        const tx = db.transaction(MODEL_BYTES_KEY, 'readwrite');
        const store = tx.objectStore(MODEL_BYTES_KEY);
        store.put({ id: 'meta', version: MODEL_VERSION, bytes: bytes.byteLength }, 'meta');
        store.put(bytes, 'data');
        await txDone(tx);
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
            const ort = await import('https://cdn.jsdelivr.net/npm/onnxruntime-web@1.20.0/dist/ort.min.mjs');
            ort.env.wasm.wasmPaths = 'https://cdn.jsdelivr.net/npm/onnxruntime-web@1.20.0/dist/';

            let modelBytes = await loadModelFromCache();
            if (!modelBytes) {
                const response = await fetch(MODEL_URL);
                if (!response.ok) throw new Error(`Model download failed: ${response.status}`);
                modelBytes = new Uint8Array(await response.arrayBuffer());
                await saveModelToCache(modelBytes);
            }

            return ort.InferenceSession.create(modelBytes, { executionProviders: ['wasm'] });
        })();

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

        const mean = 0.485, std = 0.229;
        const input = new Float32Array(1 * 3 * INPUT * INPUT);

        const stepX = width / INPUT;
        const stepY = height / INPUT;

        for (let y = 0; y < INPUT; y++) {
            for (let x = 0; x < INPUT; x++) {
                const sx = Math.min(width - 1, Math.floor(x * stepX));
                const sy = Math.min(height - 1, Math.floor(y * stepY));
                const src = (sy * width + sx) * 4;
                const base = y * INPUT + x;
                input[0 * INPUT * INPUT + base] = (rgba[src + 0] / 255 - mean) / std;
                input[1 * INPUT * INPUT + base] = (rgba[src + 1] / 255 - mean) / std;
                input[2 * INPUT * INPUT + base] = (rgba[src + 2] / 255 - mean) / std;
            }
        }

        const tensor = new ort.Tensor('float32', input, [1, 3, INPUT, INPUT]);
        const outputs = await session.run({ input: tensor });
        const mask = outputs[Object.keys(outputs)[0]].data;

        const out = new Uint8ClampedArray(width * height * 4);
        for (let i = 0; i < width * height; i++) {
            const src = i * 4;
            const dst = i * 4;
            out[dst + 0] = rgba[src + 0];
            out[dst + 1] = rgba[src + 1];
            out[dst + 2] = rgba[src + 2];
            out[dst + 3] = Math.round(Math.max(0, Math.min(1, mask[i])) * 255);
        }

        return out;
    }

    window.pochopaudio = window.pochopaudio || {};
    window.pochopaudio.cutout = {
        isAvailable: () => true,
        removeBackgroundBytes,
    };
})();
