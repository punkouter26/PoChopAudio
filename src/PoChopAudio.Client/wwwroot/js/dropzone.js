// Drag-and-drop a folder of images and turn it into a flat list of File objects. The browser's
// `input.files` accessor doesn't surface the contents of a dropped directory, so we have to walk
// the dropped items via `webkitGetAsEntry`.

(function () {
    'use strict';

    async function readEntry(entry) {
        if (entry.isFile) {
            return await new Promise((resolve, reject) => {
                entry.file(
                    (file) => resolve(file),
                    (err) => reject(err));
            });
        }

        if (entry.isDirectory) {
            const reader = entry.createReader();
            const all = [];
            while (true) {
                const batch = await new Promise((resolve, reject) => {
                    reader.readEntries(
                        (entries) => resolve(entries),
                        (err) => reject(err));
                });
                if (batch.length === 0) break;
                for (const e of batch) all.push(e);
            }
            const files = [];
            for (const e of all) {
                const inner = await readEntry(e);
                if (Array.isArray(inner)) {
                    files.push(...inner);
                } else if (inner) {
                    files.push(inner);
                }
            }
            return files;
        }

        return null;
    }

    async function filesFromDataTransfer(dataTransfer) {
        const items = dataTransfer.items;
        if (!items || items.length === 0) {
            return Array.from(dataTransfer.files || []);
        }

        const all = [];
        for (let i = 0; i < items.length; i++) {
            const item = items[i];
            if (item.kind !== 'file') continue;
            const entry = item.webkitGetAsEntry ? item.webkitGetAsEntry() : null;
            if (!entry) {
                const f = item.getAsFile();
                if (f) all.push(f);
                continue;
            }
            const result = await readEntry(entry);
            if (Array.isArray(result)) {
                all.push(...result);
            } else if (result) {
                all.push(result);
            }
        }
        return all;
    }

    function attachDropZone(element, dotnet) {
        let depth = 0;
        const onEnter = (e) => {
            e.preventDefault();
            depth++;
            element.classList.add('is-drag-over');
        };
        const onLeave = (e) => {
            e.preventDefault();
            depth--;
            if (depth <= 0) {
                depth = 0;
                element.classList.remove('is-drag-over');
            }
        };
        const onOver = (e) => e.preventDefault();
        const onDrop = async (e) => {
            e.preventDefault();
            depth = 0;
            element.classList.remove('is-drag-over');
            try {
                const files = await filesFromDataTransfer(e.dataTransfer);
                const descriptors = files.map(f => ({
                    name: f.name,
                    size: f.size,
                    contentType: f.type || 'application/octet-stream',
                    handle: createFileHandle(f),
                }));
                dotnet.invokeMethodAsync('OnFiles', descriptors);
            } catch (err) {
                console.error('Drop failed', err);
            }
        };

        element.addEventListener('dragenter', onEnter);
        element.addEventListener('dragleave', onLeave);
        element.addEventListener('dragover', onOver);
        element.addEventListener('drop', onDrop);

        return {
            dispose() {
                element.removeEventListener('dragenter', onEnter);
                element.removeEventListener('dragleave', onLeave);
                element.removeEventListener('dragover', onOver);
                element.removeEventListener('drop', onDrop);
            },
        };
    }

    // Wraps a File so the C# side can read it back as a stream via .NET's IJSStreamReference.
    function createFileHandle(file) {
        return {
            async stream() {
                return file.arrayBuffer();
            },
            name() {
                return file.name;
            },
            size() {
                return file.size;
            },
        };
    }

    window.pochopaudio = window.pochopaudio || {};
    window.pochopaudio.dropzone = { attachDropZone, filesFromDataTransfer };
})();
