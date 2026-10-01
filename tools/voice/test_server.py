import io
import json
import unittest
import urllib.request
import urllib.error
import wave
from server import decode_wav


def wav_bytes(rate=16000, channels=1, seconds=1):
    buffer = io.BytesIO()
    with wave.open(buffer, "wb") as wav:
        wav.setnchannels(channels)
        wav.setsampwidth(2)
        wav.setframerate(rate)
        wav.writeframes(b"\0\0" * int(rate * seconds) * channels)
    return buffer.getvalue()


class SpeechTests(unittest.TestCase):
    def test_decode_silence(self):
        self.assertEqual(len(decode_wav(wav_bytes())), 16000)

    def test_reject_wrong_rate(self):
        with self.assertRaises(ValueError):
            decode_wav(wav_bytes(rate=48000))

    def test_reject_stereo(self):
        with self.assertRaises(ValueError):
            decode_wav(wav_bytes(channels=2))

    def test_reject_truncated(self):
        with self.assertRaises(ValueError):
            decode_wav(wav_bytes()[:-10])

    def test_live_silence(self):
        request = urllib.request.Request("http://127.0.0.1:8766/transcribe", data=wav_bytes(), headers={"Content-Type": "audio/wav"})
        with urllib.request.urlopen(request, timeout=10) as response:
            self.assertEqual(json.load(response)["text"], "")

    def test_live_invalid_audio(self):
        request = urllib.request.Request("http://127.0.0.1:8766/transcribe", data=b"invalid" * 20)
        with self.assertRaises(urllib.error.HTTPError) as error:
            urllib.request.urlopen(request, timeout=10)
        self.assertEqual(error.exception.code, 400)


if __name__ == "__main__":
    unittest.main()
