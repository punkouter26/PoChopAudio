// Capture side of the recorder, running on the audio thread.
//
// The worklet is handed audio in 128-frame quanta, which at 48 kHz is 375 callbacks a second.
// Posting each one across to the main thread would spend more time in structured-clone overhead
// than in the recording itself, so frames are gathered into ~85 ms blocks first.

const BLOCK_FRAMES = 4096;

class CaptureProcessor extends AudioWorkletProcessor {
    constructor() {
        super();
        this._block = new Float32Array(BLOCK_FRAMES);
        this._filled = 0;
        this._running = true;

        this.port.onmessage = (event) => {
            if (event.data === 'flush') {
                this._flush();
                this._running = false;
                this.port.postMessage({ type: 'ended' });
            }
        };
    }

    _flush() {
        if (this._filled === 0) {
            return;
        }

        // Slice rather than send the whole block: the tail of a recording is rarely a round number
        // of frames, and padding it with zeroes would append silence to the take.
        const chunk = this._block.slice(0, this._filled);
        this.port.postMessage({ type: 'audio', samples: chunk }, [chunk.buffer]);
        this._block = new Float32Array(BLOCK_FRAMES);
        this._filled = 0;
    }

    process(inputs) {
        if (!this._running) {
            return false;
        }

        const channel = inputs[0] && inputs[0][0];
        if (!channel) {
            // No input connected yet. Keep the node alive rather than tearing the graph down.
            return true;
        }

        let offset = 0;
        while (offset < channel.length) {
            const room = BLOCK_FRAMES - this._filled;
            const take = Math.min(room, channel.length - offset);
            this._block.set(channel.subarray(offset, offset + take), this._filled);
            this._filled += take;
            offset += take;

            if (this._filled === BLOCK_FRAMES) {
                this._flush();
            }
        }

        return true;
    }
}

registerProcessor('pochop-capture', CaptureProcessor);
