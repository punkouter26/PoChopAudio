// Microphone capture that produces a plain 16-bit PCM WAV.
//
// MediaRecorder would be less code, but it emits WebM/Opus on Chrome and Firefox and MP4/AAC on
// Safari, and the API decodes neither of those off-Windows. Capturing float samples and writing
// the WAV header here means a recording travels the identical path as an uploaded .wav file, with
// no new server dependency and no format that works on one machine and not another.

(function () {
    'use strict';

    // Uncompressed audio adds up fast, so capture stops itself before the upload limit rejects it.
    const BYTES_PER_FRAME = 2;
    const LEVEL_INTERVAL_MS = 50;

    let session = null;

    function decibels(amplitude) {
        return amplitude <= 0 ? -Infinity : 20 * Math.log10(amplitude);
    }

    async function start(dotnetRef, maxBytes) {
        if (session) {
            await stop();
        }

        // Every browser processing option is off on purpose: automatic gain control would ride the
        // level between takes and destroy the very consistency the chopper is trying to measure,
        // and noise suppression would eat the quiet gaps the detector splits on.
        const stream = await navigator.mediaDevices.getUserMedia({
            audio: {
                channelCount: 1,
                echoCancellation: false,
                autoGainControl: false,
                noiseSuppression: false,
            },
        });

        const context = new AudioContext();
        await context.audioWorklet.addModule('js/recorder-worklet.js');

        const source = context.createMediaStreamSource(stream);
        const node = new AudioWorkletNode(context, 'pochop-capture', {
            numberOfInputs: 1,
            numberOfOutputs: 0,
            channelCount: 1,
        });

        const state = {
            dotnetRef,
            stream,
            context,
            source,
            node,
            chunks: [],
            frames: 0,
            sampleRate: context.sampleRate,
            maxFrames: Math.floor(maxBytes / BYTES_PER_FRAME),
            clipped: false,
            blockPeak: 0,
            startedAt: performance.now(),
            lastLevelAt: 0,
            stopping: false,
            ended: null,
        };

        state.ended = new Promise((resolve) => {
            state.resolveEnded = resolve;
        });

        node.port.onmessage = (event) => {
            const message = event.data;

            if (message.type === 'ended') {
                state.resolveEnded();
                return;
            }

            if (message.type !== 'audio' || state.stopping) {
                return;
            }

            const samples = message.samples;
            state.chunks.push(samples);
            state.frames += samples.length;

            for (let i = 0; i < samples.length; i++) {
                const magnitude = Math.abs(samples[i]);
                if (magnitude > state.blockPeak) {
                    state.blockPeak = magnitude;
                }
                // 0.99 rather than 1.0: the browser hands over floats, and anything this close is
                // already against the converter's ceiling once it becomes 16-bit.
                if (magnitude >= 0.99) {
                    state.clipped = true;
                }
            }

            const now = performance.now();
            if (now - state.lastLevelAt >= LEVEL_INTERVAL_MS) {
                state.lastLevelAt = now;
                const peakDb = decibels(state.blockPeak);
                state.blockPeak = 0;

                state.dotnetRef.invokeMethodAsync(
                    'OnLevel',
                    Number.isFinite(peakDb) ? peakDb : -100,
                    state.frames / state.sampleRate,
                    state.clipped);
            }

            if (state.frames >= state.maxFrames) {
                // Hitting the cap is not an error, but it must not be silent either.
                state.dotnetRef.invokeMethodAsync('OnLimitReached');
            }
        };

        source.connect(node);
        session = state;

        return { sampleRate: state.sampleRate };
    }

    async function stop() {
        if (!session) {
            return null;
        }

        const state = session;
        session = null;
        state.stopping = true;

        // Ask the worklet for its partial block before tearing the graph down, or the last ~85 ms
        // of the take is dropped.
        state.node.port.postMessage('flush');
        await Promise.race([state.ended, new Promise((resolve) => setTimeout(resolve, 250))]);

        try {
            state.source.disconnect();
            state.node.disconnect();
        } catch {
            // Already disconnected; nothing to unwind.
        }

        state.stream.getTracks().forEach((track) => track.stop());
        await state.context.close();

        if (state.frames === 0) {
            return null;
        }

        return {
            wav: encodeWav(state.chunks, state.frames, state.sampleRate),
            sampleRate: state.sampleRate,
            durationSeconds: state.frames / state.sampleRate,
            clipped: state.clipped,
        };
    }

    /// Writes a canonical 44-byte RIFF header followed by mono 16-bit PCM.
    function encodeWav(chunks, frames, sampleRate) {
        const dataBytes = frames * BYTES_PER_FRAME;
        const buffer = new ArrayBuffer(44 + dataBytes);
        const view = new DataView(buffer);

        const ascii = (offset, text) => {
            for (let i = 0; i < text.length; i++) {
                view.setUint8(offset + i, text.charCodeAt(i));
            }
        };

        ascii(0, 'RIFF');
        view.setUint32(4, 36 + dataBytes, true);
        ascii(8, 'WAVE');
        ascii(12, 'fmt ');
        view.setUint32(16, 16, true);
        view.setUint16(20, 1, true);                          // PCM
        view.setUint16(22, 1, true);                          // mono
        view.setUint32(24, sampleRate, true);
        view.setUint32(28, sampleRate * BYTES_PER_FRAME, true);
        view.setUint16(32, BYTES_PER_FRAME, true);
        view.setUint16(34, 16, true);
        ascii(36, 'data');
        view.setUint32(40, dataBytes, true);

        let offset = 44;
        for (const chunk of chunks) {
            for (let i = 0; i < chunk.length; i++) {
                const clamped = Math.max(-1, Math.min(1, chunk[i]));
                // Asymmetric scaling: 16-bit PCM runs -32768..32767, so the two directions do not
                // share a multiplier without wrapping the most negative sample around to positive.
                view.setInt16(offset, clamped < 0 ? clamped * 0x8000 : clamped * 0x7fff, true);
                offset += 2;
            }
        }

        return new Uint8Array(buffer);
    }

    async function isSupported() {
        return !!(navigator.mediaDevices
            && navigator.mediaDevices.getUserMedia
            && window.AudioWorkletNode
            && window.isSecureContext);
    }

    window.pochopaudio = window.pochopaudio || {};
    // encodeWav is exposed so it can be exercised outside a browser: it is the one piece here the
    // server has to agree with, and a wrong header would fail every recording at decode time.
    window.pochopaudio.recorder = { start, stop, isSupported, encodeWav };
})();
