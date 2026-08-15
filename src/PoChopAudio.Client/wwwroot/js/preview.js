// Browser-side preview helpers. Capped image previews keep the UI snappy on big head shots —
// the original 12 MP JPEG and the post-cutout PNG both decode to tens of MBs in memory, which
// is fine for processing but too heavy to render in a thumbnail-sized <img> tag.

(function () {
    'use strict';

    const MAX_PREVIEW_EDGE = 512;
    const JPEG_QUALITY = 0.85;

    // Decode arbitrary image bytes (Uint8Array) and re-encode as a small JPEG data URL whose
    // longest edge is at most maxEdge px. Accepting raw bytes (not a data URL) keeps
    // the C# -> JS interop payload small: a 2 MB JPEG stays a 2 MB Uint8Array, whereas a
    // data URL would be 2.6 MB of base64 and would push through the JS interop as a string.
    async function shrinkBytes(bytes, mimeType, maxEdge) {
        if (!bytes || bytes.byteLength === 0) return '';
        const cap = Math.max(8, maxEdge || MAX_PREVIEW_EDGE);
        const blob = new Blob([bytes], { type: mimeType || 'image/jpeg' });
        const objectUrl = URL.createObjectURL(blob);
        try {
            const img = await new Promise((resolve, reject) => {
                const i = new Image();
                i.onload = () => resolve(i);
                i.onerror = () => reject(new Error('Could not decode preview bytes.'));
                i.src = objectUrl;
            });

            const longest = Math.max(img.naturalWidth, img.naturalHeight);
            if (longest <= cap) {
                // Already small. Read the file back as a data URL so the <img> tag has a
                // self-contained source.
                return await blobToDataUrl(blob);
            }

            const scale = cap / longest;
            const w = Math.max(1, Math.round(img.naturalWidth * scale));
            const h = Math.max(1, Math.round(img.naturalHeight * scale));

            const canvas = document.createElement('canvas');
            canvas.width = w;
            canvas.height = h;
            const ctx = canvas.getContext('2d');
            ctx.imageSmoothingQuality = 'high';
            ctx.drawImage(img, 0, 0, w, h);

            return canvas.toDataURL('image/jpeg', JPEG_QUALITY);
        } finally {
            URL.revokeObjectURL(objectUrl);
        }
    }

    // Fetch a URL, decode the image, re-encode as a small JPEG data URL.
    async function shrinkUrl(url, maxEdge) {
        if (!url) return '';
        const response = await fetch(url);
        if (!response.ok) throw new Error(`Preview fetch failed: ${response.status}`);
        const blob = await response.blob();
        return shrinkBytes(new Uint8Array(await blob.arrayBuffer()), blob.type, maxEdge);
    }

    function blobToDataUrl(blob) {
        return new Promise((resolve, reject) => {
            const reader = new FileReader();
            reader.onload = () => resolve(reader.result);
            reader.onerror = () => reject(new Error('Could not read blob.'));
            reader.readAsDataURL(blob);
        });
    }

    window.pochopaudio = window.pochopaudio || {};
    window.pochopaudio.preview = { shrinkBytes, shrinkUrl, MAX_PREVIEW_EDGE };
})();
