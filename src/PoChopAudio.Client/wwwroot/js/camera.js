// Head-shot capture that never touches the network.
//
// Everything here — the frame grab, the background removal, the crop, and the ZIP — happens in the
// page. Photographs of someone's face are the one kind of input where "we only upload it to process
// it" is a bad trade, so the pixels stay in this tab and the only bytes that ever leave are the
// ones the user explicitly saves to disk.
//
// Shots live in a map keyed by id. C# holds ids and object URLs; the pixel data never crosses the
// interop boundary, which also keeps a dozen 2 MP images out of the .NET heap.

(function () {
    'use strict';

    // Alpha above this counts as subject when working out the crop box. Feathered edges trail off
    // to nothing, so a hard zero test would leave a halo of near-transparent pixels in the crop.
    const ALPHA_FLOOR = 8;

    const shots = new Map();
    let stream = null;
    let video = null;
    let nextId = 1;

    function revoke(url) {
        if (url) {
            try {
                URL.revokeObjectURL(url);
            } catch {
                // Already revoked.
            }
        }
    }

    async function isSupported() {
        return !!(navigator.mediaDevices
            && navigator.mediaDevices.getUserMedia
            && window.isSecureContext);
    }

    async function start(videoElement, facingMode) {
        await stop();

        stream = await navigator.mediaDevices.getUserMedia({
            video: {
                facingMode: facingMode || 'user',
                width: { ideal: 1920 },
                height: { ideal: 1080 },
            },
            audio: false,
        });

        video = videoElement;
        video.srcObject = stream;
        video.muted = true;
        video.playsInline = true;
        await video.play();

        const track = stream.getVideoTracks()[0];
        const settings = track ? track.getSettings() : {};

        return {
            width: settings.width || video.videoWidth,
            height: settings.height || video.videoHeight,
            label: (track && track.label) || 'camera',
        };
    }

    async function stop() {
        if (stream) {
            stream.getTracks().forEach((track) => track.stop());
            stream = null;
        }

        if (video) {
            video.srcObject = null;
            video = null;
        }
    }

    function canvasToBlob(canvas) {
        return new Promise((resolve, reject) => {
            canvas.toBlob(
                (blob) => (blob ? resolve(blob) : reject(new Error('Could not encode the frame as PNG.'))),
                'image/png');
        });
    }

    /// Grabs the current video frame exactly as the camera sees it. The preview is flipped in CSS
    /// so it reads like a mirror, which is what makes a self-portrait easy to line up — but the
    /// flip is deliberately not applied here, because saving it would hand back a reversed photo.
    async function capture() {
        if (!video) {
            throw new Error('The camera is not running.');
        }

        const width = video.videoWidth;
        const height = video.videoHeight;
        if (!width || !height) {
            throw new Error('The camera has not produced a frame yet.');
        }

        const canvas = document.createElement('canvas');
        canvas.width = width;
        canvas.height = height;
        canvas.getContext('2d').drawImage(video, 0, 0, width, height);

        const blob = await canvasToBlob(canvas);
        const bytes = new Uint8Array(await blob.arrayBuffer());
        const id = `shot-${nextId++}`;

        shots.set(id, {
            id,
            original: bytes,
            originalUrl: URL.createObjectURL(blob),
            cutout: null,
            cutoutUrl: null,
            width,
            height,
        });

        return { id, originalUrl: shots.get(id).originalUrl, width, height };
    }

    function decodePng(bytes) {
        return new Promise((resolve, reject) => {
            const url = URL.createObjectURL(new Blob([bytes], { type: 'image/png' }));
            const image = new Image();
            image.onload = () => {
                const canvas = document.createElement('canvas');
                canvas.width = image.naturalWidth;
                canvas.height = image.naturalHeight;
                const context = canvas.getContext('2d');
                context.drawImage(image, 0, 0);
                revoke(url);
                resolve({ context, width: canvas.width, height: canvas.height });
            };
            image.onerror = () => {
                revoke(url);
                reject(new Error('Could not decode the cutout.'));
            };
            image.src = url;
        });
    }

    /// Crops to the tightest box that still contains the subject, plus padding. This is what turns
    /// "background removed" into "head shot" without any face detection: after the mask is applied
    /// the only opaque pixels left ARE the head, so its bounding box is the crop.
    function subjectBounds(pixels, width, height, padding) {
        let minX = width;
        let minY = height;
        let maxX = -1;
        let maxY = -1;

        for (let y = 0; y < height; y++) {
            const row = y * width * 4;
            for (let x = 0; x < width; x++) {
                if (pixels[row + (x * 4) + 3] > ALPHA_FLOOR) {
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
            }
        }

        if (maxX < 0) {
            // The mask kept nothing. Return the frame untouched rather than a zero-sized image.
            return { x: 0, y: 0, width, height, empty: true };
        }

        minX = Math.max(0, minX - padding);
        minY = Math.max(0, minY - padding);
        maxX = Math.min(width - 1, maxX + padding);
        maxY = Math.min(height - 1, maxY + padding);

        return { x: minX, y: minY, width: maxX - minX + 1, height: maxY - minY + 1, empty: false };
    }

    /// Removes the background with the browser ONNX engine, then crops to the subject.
    async function cutout(id, padding) {
        const shot = shots.get(id);
        if (!shot) {
            throw new Error('That shot no longer exists.');
        }

        const engine = window.pochopaudio && window.pochopaudio.cutout;
        if (!engine || typeof engine.removeBackgroundBytes !== 'function') {
            throw new Error('The in-browser cutout engine is unavailable.');
        }

        const base64 = await engine.removeBackgroundBytes(shot.original);
        if (!base64) {
            throw new Error('The cutout engine returned nothing.');
        }

        const binary = atob(base64);
        const masked = new Uint8Array(binary.length);
        for (let i = 0; i < binary.length; i++) {
            masked[i] = binary.charCodeAt(i);
        }

        const decoded = await decodePng(masked);
        const pixels = decoded.context.getImageData(0, 0, decoded.width, decoded.height).data;
        const box = subjectBounds(pixels, decoded.width, decoded.height, padding);

        const canvas = document.createElement('canvas');
        canvas.width = box.width;
        canvas.height = box.height;
        canvas.getContext('2d').drawImage(
            decoded.context.canvas,
            box.x, box.y, box.width, box.height,
            0, 0, box.width, box.height);

        const blob = await canvasToBlob(canvas);
        revoke(shot.cutoutUrl);
        shot.cutout = new Uint8Array(await blob.arrayBuffer());
        shot.cutoutUrl = URL.createObjectURL(blob);

        return {
            cutoutUrl: shot.cutoutUrl,
            width: box.width,
            height: box.height,
            emptyMask: box.empty,
        };
    }

    function remove(id) {
        const shot = shots.get(id);
        if (shot) {
            revoke(shot.originalUrl);
            revoke(shot.cutoutUrl);
            shots.delete(id);
        }
    }

    function clear() {
        for (const id of [...shots.keys()]) {
            remove(id);
        }
        nextId = 1;
    }

    // ---- Store-only ZIP -----------------------------------------------------------------
    // PNGs are already deflated, so storing them costs nothing and saves pulling a compression
    // library over the network — which would defeat the point of keeping this page self-contained.

    // General-purpose bit 11 says "the name is UTF-8". Names come from a text field the user types
    // into, so without this a shot called Tête extracts as mojibake on anything that would
    // otherwise fall back to CP437.
    const UTF8_NAME_FLAG = 0x0800;

    let crcTable = null;

    function crc32(bytes) {
        if (!crcTable) {
            crcTable = new Uint32Array(256);
            for (let n = 0; n < 256; n++) {
                let c = n;
                for (let k = 0; k < 8; k++) {
                    c = (c & 1) ? (0xedb88320 ^ (c >>> 1)) : (c >>> 1);
                }
                crcTable[n] = c >>> 0;
            }
        }

        let crc = 0xffffffff;
        for (let i = 0; i < bytes.length; i++) {
            crc = crcTable[(crc ^ bytes[i]) & 0xff] ^ (crc >>> 8);
        }

        return (crc ^ 0xffffffff) >>> 0;
    }

    function dosDateTime(date) {
        const time = ((date.getHours() & 0x1f) << 11)
            | ((date.getMinutes() & 0x3f) << 5)
            | ((date.getSeconds() / 2) & 0x1f);
        const day = (((date.getFullYear() - 1980) & 0x7f) << 9)
            | (((date.getMonth() + 1) & 0x0f) << 5)
            | (date.getDate() & 0x1f);
        return { time, day };
    }

    /// Builds the ZIP bytes from [{name, data}] pairs. Split out from the object-URL plumbing so
    /// the byte layout can be checked outside a browser — a wrong offset here is a corrupt archive
    /// that only shows up when someone tries to open it.
    function buildZipBytes(files) {
        const encoder = new TextEncoder();
        const stamp = dosDateTime(new Date());
        const entries = files.map((file) => ({
            name: encoder.encode(file.name),
            data: file.data,
            crc: crc32(file.data),
        }));

        if (entries.length === 0) {
            throw new Error('There are no finished cutouts to save.');
        }

        let size = 0;
        for (const entry of entries) {
            size += 30 + entry.name.length + entry.data.length;   // local header + payload
            size += 46 + entry.name.length;                        // central directory record
        }
        size += 22;                                                // end of central directory

        const buffer = new ArrayBuffer(size);
        const view = new DataView(buffer);
        const out = new Uint8Array(buffer);

        let offset = 0;
        const offsets = [];

        for (const entry of entries) {
            offsets.push(offset);

            view.setUint32(offset, 0x04034b50, true);
            view.setUint16(offset + 4, 20, true);
            view.setUint16(offset + 6, UTF8_NAME_FLAG, true);
            view.setUint16(offset + 8, 0, true);            // stored, not deflated
            view.setUint16(offset + 10, stamp.time, true);
            view.setUint16(offset + 12, stamp.day, true);
            view.setUint32(offset + 14, entry.crc, true);
            view.setUint32(offset + 18, entry.data.length, true);
            view.setUint32(offset + 22, entry.data.length, true);
            view.setUint16(offset + 26, entry.name.length, true);
            view.setUint16(offset + 28, 0, true);
            offset += 30;

            out.set(entry.name, offset);
            offset += entry.name.length;
            out.set(entry.data, offset);
            offset += entry.data.length;
        }

        const centralStart = offset;

        for (let i = 0; i < entries.length; i++) {
            const entry = entries[i];

            view.setUint32(offset, 0x02014b50, true);
            view.setUint16(offset + 4, 20, true);
            view.setUint16(offset + 6, 20, true);
            view.setUint16(offset + 8, UTF8_NAME_FLAG, true);
            view.setUint16(offset + 10, 0, true);
            view.setUint16(offset + 12, stamp.time, true);
            view.setUint16(offset + 14, stamp.day, true);
            view.setUint32(offset + 16, entry.crc, true);
            view.setUint32(offset + 20, entry.data.length, true);
            view.setUint32(offset + 24, entry.data.length, true);
            view.setUint16(offset + 28, entry.name.length, true);
            view.setUint16(offset + 30, 0, true);
            view.setUint16(offset + 32, 0, true);
            view.setUint16(offset + 34, 0, true);
            view.setUint16(offset + 36, 0, true);
            view.setUint32(offset + 38, 0, true);
            view.setUint32(offset + 42, offsets[i], true);
            offset += 46;

            out.set(entry.name, offset);
            offset += entry.name.length;
        }

        view.setUint32(offset, 0x06054b50, true);
        view.setUint16(offset + 4, 0, true);
        view.setUint16(offset + 6, 0, true);
        view.setUint16(offset + 8, entries.length, true);
        view.setUint16(offset + 10, entries.length, true);
        view.setUint32(offset + 12, offset - centralStart, true);
        view.setUint32(offset + 16, centralStart, true);
        view.setUint16(offset + 20, 0, true);

        return out;
    }

    /// Collects the finished cutouts named by ids/names and returns an object URL for the archive.
    function zip(ids, names) {
        const files = [];

        for (let i = 0; i < ids.length; i++) {
            const shot = shots.get(ids[i]);
            if (shot && shot.cutout) {
                files.push({ name: names[i], data: shot.cutout });
            }
        }

        const bytes = buildZipBytes(files);
        return URL.createObjectURL(new Blob([bytes], { type: 'application/zip' }));
    }

    /// Saves an object URL to disk under a given name, then releases it if we made it here.
    function save(url, fileName, revokeAfter) {
        const link = document.createElement('a');
        link.href = url;
        link.download = fileName;
        document.body.appendChild(link);
        link.click();
        link.remove();

        if (revokeAfter) {
            // Give the browser a moment to start the download before the URL disappears.
            setTimeout(() => revoke(url), 30000);
        }
    }

    window.pochopaudio = window.pochopaudio || {};
    // buildZipBytes and subjectBounds are exposed so they can be exercised outside a browser: they
    // are the two pieces here whose output nothing else validates until a user opens a broken file.
    window.pochopaudio.camera = {
        isSupported, start, stop, capture, cutout, remove, clear, zip, save,
        buildZipBytes, subjectBounds,
    };
})();
