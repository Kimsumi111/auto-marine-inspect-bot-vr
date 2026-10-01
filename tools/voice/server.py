"""Loopback-only Korean speech transcription. Audio is held in memory, never saved."""
import io
import json
import threading
import wave
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
import numpy as np
from faster_whisper import WhisperModel

MODEL_DIR = Path(__file__).parent / "models" / "base"
lock = threading.Lock()
model = None


def decode_wav(body):
    with wave.open(io.BytesIO(body), "rb") as wav:
        if (wav.getnchannels(), wav.getsampwidth(), wav.getframerate()) != (1, 2, 16000):
            raise ValueError("Expected mono PCM16, 16000 Hz WAV")
        if not 1600 <= wav.getnframes() <= 16000 * 20:
            raise ValueError("Audio duration must be 0.1 to 20 seconds")
        frames = wav.readframes(wav.getnframes())
        if len(frames) != wav.getnframes() * 2:
            raise ValueError("Truncated WAV")
    return np.frombuffer(frames, dtype="<i2").astype(np.float32) / 32768.0


class Handler(BaseHTTPRequestHandler):
    def setup(self):
        super().setup()
        self.connection.settimeout(10)

    def log_message(self, *_):
        pass  # Do not log audio or transcripts.

    def reply(self, code, payload):
        data = json.dumps(payload, ensure_ascii=False).encode("utf-8")
        self.send_response(code)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(data)))
        self.end_headers()
        try:
            self.wfile.write(data)
        except (BrokenPipeError, ConnectionResetError):
            pass

    def do_GET(self):
        if self.path != "/health":
            return self.reply(404, {"error": "Not found"})
        self.reply(200, {"service": "metamarine-speech", "ready": model is not None, "language": "ko"})

    def do_POST(self):
        if self.path != "/transcribe":
            return self.reply(404, {"error": "Not found"})
        if self.headers.get("Origin"):
            return self.reply(403, {"error": "Browser requests are not allowed"})
        try:
            length = int(self.headers.get("Content-Length", "0"))
            if not 44 < length <= 640044:
                return self.reply(413, {"error": "Invalid audio size"})
            audio = decode_wav(self.rfile.read(length))
        except (ValueError, wave.Error, EOFError, OSError):
            return self.reply(400, {"error": "Invalid WAV audio"})
        if model is None:
            return self.reply(503, {"error": "Speech model is loading"})
        if not lock.acquire(blocking=False):
            return self.reply(409, {"error": "Speech service is busy"})
        try:
            if np.max(np.abs(audio)) < 0.002:
                text = ""
            else:
                segments, _ = model.transcribe(audio, language="ko", beam_size=3,
                    vad_filter=True, condition_on_previous_text=False)
                text = " ".join(s.text.strip() for s in segments).strip()
            self.reply(200, {"text": text, "language": "ko"})
        except Exception:
            self.reply(500, {"error": "Transcription failed"})
        finally:
            lock.release()


if __name__ == "__main__":
    try:
        server = ThreadingHTTPServer(("127.0.0.1", 8766), Handler)
    except OSError:
        raise SystemExit("Port 8766 is already in use; no second server was started.")
    # No downloads at runtime: install/model preparation is a separate explicit step.
    model = WhisperModel(str(MODEL_DIR), device="cpu", compute_type="int8", cpu_threads=4,
                         local_files_only=True)
    print("MetaMarine Korean speech ready on 127.0.0.1:8766", flush=True)
    server.serve_forever()
