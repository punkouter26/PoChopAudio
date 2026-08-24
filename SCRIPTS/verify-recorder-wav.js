// Contract check for the browser's WAV encoder.
//
// The recorder writes its own RIFF header so a recording can travel the same path as an uploaded
// .wav — no server-side Opus decode, no codec that works on one platform and not another. That
// makes the header the single point where the browser and NAudio have to agree, and a wrong field
// shows up as an opaque 422 at upload time rather than anything a stack trace would explain.
//
// This loads the real recorder.js (not a copy) in a minimal window stub and asserts every header
// field. Needs node only — no browser, no microphone, so it runs anywhere.
//
//   node SCRIPTS/verify-recorder-wav.js

const fs = require('fs');
const path = require('path');

const RECORDER = path.join(__dirname, '..', 'src', 'PoChopAudio.Client', 'wwwroot', 'js', 'recorder.js');
const SAMPLE_RATE = 48000;

function loadEncoder() {
    const win = { isSecureContext: true };
    const nav = { mediaDevices: {} };
    const perf = { now: () => 0 };

    new Function('window', 'navigator', 'performance', fs.readFileSync(RECORDER, 'utf8'))(win, nav, perf);

    const encodeWav = win.pochopaudio && win.pochopaudio.recorder && win.pochopaudio.recorder.encodeWav;
    if (typeof encodeWav !== 'function') {
        throw new Error('recorder.js no longer exposes encodeWav; this check cannot run.');
    }

    return encodeWav;
}

function buildTakes() {
    const amplitude = Math.pow(10, -12 / 20);
    let seed = 12345;
    const noise = () => {
        seed = (seed * 1664525 + 1013904223) >>> 0;
        return ((seed >>> 8) / (1 << 24) * 2 - 1) * 0.0005;
    };

    const chunks = [];
    let frames = 0;
    const push = (values) => {
        chunks.push(Float32Array.from(values));
        frames += values.length;
    };

    const quiet = (seconds) => push(Array.from({ length: Math.round(SAMPLE_RATE * seconds) }, noise));
    const tone = (seconds) => push(Array.from(
        { length: Math.round(SAMPLE_RATE * seconds) },
        (_, i) => (amplitude * Math.sin(2 * Math.PI * 1000 * i / SAMPLE_RATE)) + noise()));

    quiet(0.3);
    for (let take = 0; take < 5; take++) {
        tone(0.4);
        quiet(0.4);
    }

    return { chunks, frames };
}

function main() {
    const encodeWav = loadEncoder();
    const { chunks, frames } = buildTakes();
    const wav = Buffer.from(encodeWav(chunks, frames, SAMPLE_RATE));

    const failures = [];
    const expect = (name, actual, wanted) => {
        if (actual !== wanted) {
            failures.push(`${name}: got ${actual}, expected ${wanted}`);
        }
    };

    expect('RIFF tag', wav.toString('ascii', 0, 4), 'RIFF');
    expect('riff chunk size', wav.readUInt32LE(4), 36 + (frames * 2));
    expect('WAVE tag', wav.toString('ascii', 8, 12), 'WAVE');
    expect('fmt tag', wav.toString('ascii', 12, 16), 'fmt ');
    expect('fmt chunk size', wav.readUInt32LE(16), 16);
    expect('audio format (1 = PCM)', wav.readUInt16LE(20), 1);
    expect('channels', wav.readUInt16LE(22), 1);
    expect('sample rate', wav.readUInt32LE(24), SAMPLE_RATE);
    expect('byte rate', wav.readUInt32LE(28), SAMPLE_RATE * 2);
    expect('block align', wav.readUInt16LE(32), 2);
    expect('bits per sample', wav.readUInt16LE(34), 16);
    expect('data tag', wav.toString('ascii', 36, 40), 'data');
    expect('data chunk size', wav.readUInt32LE(40), frames * 2);
    expect('total length', wav.length, 44 + (frames * 2));

    // The two directions of 16-bit PCM do not share a multiplier. Getting this wrong wraps the
    // most negative sample around to positive and puts a click in every take.
    let min = 0;
    let max = 0;
    for (let offset = 44; offset < wav.length; offset += 2) {
        const value = wav.readInt16LE(offset);
        if (value < min) min = value;
        if (value > max) max = value;
    }

    if (min < -32768 || max > 32767) {
        failures.push(`sample out of int16 range: min=${min} max=${max}`);
    }

    if (failures.length > 0) {
        console.error('recorder WAV header is wrong:');
        failures.forEach((line) => console.error(`  - ${line}`));
        process.exit(1);
    }

    const seconds = (frames / SAMPLE_RATE).toFixed(2);
    console.log(`recorder WAV header OK — ${seconds}s, ${(wav.length / 1024).toFixed(0)} KB, peak int16 ${max}/${min}`);
}

main();
