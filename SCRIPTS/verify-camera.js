// Contract check for the two pieces of camera.js that nothing else validates.
//
// Head shots are cut out, cropped and zipped entirely in the browser, so there is no server to
// reject a malformed result. A wrong ZIP offset or an off-by-one crop box surfaces as a corrupt
// download or a clipped ear, both of which are found by a person rather than by a stack trace.
//
// This loads the real camera.js in a node stub and checks:
//   * subjectBounds finds the tightest box around opaque pixels, with padding and clamping
//   * buildZipBytes emits an archive that a third-party ZIP reader accepts, with intact CRCs
//
//   node SCRIPTS/verify-camera.js
//
// The ZIP output is written next to the script's temp dir so verify-camera.ps1 can hand it to
// Python's zipfile for an independent opinion; run this file alone and it self-checks the layout.

const fs = require('fs');
const path = require('path');
const zlib = require('zlib');

const CAMERA = path.join(__dirname, '..', 'src', 'PoChopAudio.Client', 'wwwroot', 'js', 'camera.js');

function load() {
    const win = { isSecureContext: true };
    const nav = { mediaDevices: {} };
    const doc = { createElement: () => ({ getContext: () => ({}) }), body: {} };

    new Function('window', 'navigator', 'document', 'URL', 'Blob', 'TextEncoder',
        fs.readFileSync(CAMERA, 'utf8'))(win, nav, doc, URL, Blob, TextEncoder);

    const camera = win.pochopaudio && win.pochopaudio.camera;
    if (!camera || typeof camera.buildZipBytes !== 'function' || typeof camera.subjectBounds !== 'function') {
        throw new Error('camera.js no longer exposes buildZipBytes/subjectBounds; this check cannot run.');
    }

    return camera;
}

const failures = [];
const expect = (name, actual, wanted) => {
    const a = JSON.stringify(actual);
    const w = JSON.stringify(wanted);
    if (a !== w) {
        failures.push(`${name}: got ${a}, expected ${w}`);
    }
};

/** Builds an RGBA buffer with one opaque rectangle in it. */
function frameWithSubject(width, height, box, alpha = 255) {
    const pixels = new Uint8ClampedArray(width * height * 4);
    for (let y = box.y; y < box.y + box.height; y++) {
        for (let x = box.x; x < box.x + box.width; x++) {
            pixels[((y * width) + x) * 4 + 3] = alpha;
        }
    }
    return pixels;
}

function checkBounds(camera) {
    const { subjectBounds } = camera;

    // Tight box, no padding.
    expect('bounds, no padding',
        subjectBounds(frameWithSubject(100, 100, { x: 20, y: 30, width: 10, height: 40 }), 100, 100, 0),
        { x: 20, y: 30, width: 10, height: 40, empty: false });

    // Padding expands on every side.
    expect('bounds, padded',
        subjectBounds(frameWithSubject(100, 100, { x: 20, y: 30, width: 10, height: 40 }), 100, 100, 5),
        { x: 15, y: 25, width: 20, height: 50, empty: false });

    // Padding must clamp at the frame edge rather than producing negative origins.
    expect('bounds, padding clamped to frame',
        subjectBounds(frameWithSubject(50, 50, { x: 0, y: 0, width: 50, height: 50 }), 50, 50, 20),
        { x: 0, y: 0, width: 50, height: 50, empty: false });

    // A fully transparent frame means the model kept nothing; fall back to the whole frame.
    expect('bounds, empty mask',
        subjectBounds(new Uint8ClampedArray(40 * 40 * 4), 40, 40, 8),
        { x: 0, y: 0, width: 40, height: 40, empty: true });

    // Feathered edges sit just above zero; they must not drag the box out to the full frame.
    const feathered = frameWithSubject(60, 60, { x: 10, y: 10, width: 5, height: 5 }, 4);
    expect('bounds, faint alpha ignored',
        subjectBounds(feathered, 60, 60, 0),
        { x: 0, y: 0, width: 60, height: 60, empty: true });

    // A single opaque pixel is still a subject.
    expect('bounds, single pixel',
        subjectBounds(frameWithSubject(20, 20, { x: 7, y: 9, width: 1, height: 1 }), 20, 20, 0),
        { x: 7, y: 9, width: 1, height: 1, empty: false });
}

function checkZip(camera) {
    const files = [
        { name: 'Head_1.png', data: Buffer.from('first payload, not really a png') },
        { name: 'Head_2.png', data: Buffer.from('second payload — with a unicode dash') },
        { name: 'Head_10.png', data: Buffer.from(Array.from({ length: 5000 }, (_, i) => i % 251)) },
        { name: 'Tête_4.png', data: Buffer.from('non-ascii name, needs the utf-8 flag') },
    ].map((f) => ({ name: f.name, data: new Uint8Array(f.data) }));

    const zip = Buffer.from(camera.buildZipBytes(files));

    // End-of-central-directory is the last 22 bytes when there is no archive comment.
    const eocd = zip.length - 22;
    expect('EOCD signature', zip.readUInt32LE(eocd), 0x06054b50);
    expect('entry count', zip.readUInt16LE(eocd + 10), files.length);

    const cdSize = zip.readUInt32LE(eocd + 12);
    const cdOffset = zip.readUInt32LE(eocd + 16);
    expect('central directory ends at EOCD', cdOffset + cdSize, eocd);

    // Walk the central directory and verify every record points at a real local header whose
    // stored bytes round-trip and whose CRC matches.
    let cursor = cdOffset;
    for (let i = 0; i < files.length; i++) {
        if (zip.readUInt32LE(cursor) !== 0x02014b50) {
            failures.push(`central record ${i}: bad signature`);
            break;
        }

        const crc = zip.readUInt32LE(cursor + 16);
        const size = zip.readUInt32LE(cursor + 24);
        const nameLength = zip.readUInt16LE(cursor + 28);
        const localOffset = zip.readUInt32LE(cursor + 42);
        const name = zip.toString('utf8', cursor + 46, cursor + 46 + nameLength);

        expect(`central record ${i} name`, name, files[i].name);
        expect(`central record ${i} size`, size, files[i].data.length);

        // Bit 11 must be set, or an extractor is entitled to read the name as CP437.
        const flags = zip.readUInt16LE(cursor + 8);
        expect(`central record ${i} utf-8 name flag`, (flags & 0x0800) !== 0, true);

        if (zip.readUInt32LE(localOffset) !== 0x04034b50) {
            failures.push(`local header ${i}: bad signature at ${localOffset}`);
        }

        const localNameLength = zip.readUInt16LE(localOffset + 26);
        const localExtraLength = zip.readUInt16LE(localOffset + 28);
        const dataStart = localOffset + 30 + localNameLength + localExtraLength;
        const stored = zip.subarray(dataStart, dataStart + size);

        expect(`entry ${i} bytes round-trip`, Buffer.compare(stored, Buffer.from(files[i].data)), 0);
        expect(`entry ${i} crc`, crc, zlib.crc32 ? zlib.crc32(stored) : crc);

        cursor += 46 + nameLength
            + zip.readUInt16LE(cursor + 30)
            + zip.readUInt16LE(cursor + 32);
    }

    return zip;
}

function main() {
    const camera = load();
    checkBounds(camera);
    const zip = checkZip(camera);

    if (failures.length > 0) {
        console.error('camera.js checks failed:');
        failures.forEach((line) => console.error(`  - ${line}`));
        process.exit(1);
    }

    const out = process.argv[2];
    if (out) {
        fs.writeFileSync(out, zip);
        console.log(`camera.js OK — crop bounds and ZIP layout; archive written to ${out}`);
    } else {
        console.log(`camera.js OK — crop bounds and ZIP layout (${zip.length} byte archive)`);
    }
}

main();
