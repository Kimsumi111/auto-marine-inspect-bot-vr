"""Bounded, serial subprocess adapter for the existing offline CSV diagnosis tool."""
import asyncio
import hashlib
import json
import math
import os
from pathlib import Path
import subprocess


class DiagnosisError(Exception):
    pass


class DiagnosisRunner:
    def __init__(self, root=None, python=None, timeout=45):
        self.root = Path(root or Path(__file__).resolve().parents[1]).resolve()
        self.python = Path(python or os.environ.get("SHIP_DIAGNOSIS_PYTHON", self.root / ".venv-diagnosis" / "Scripts" / "python.exe"))
        self.timeout = timeout
        self.gate = asyncio.Semaphore(1)

    async def run(self, csv_path):
        async with self.gate:
            try:
                if not isinstance(csv_path, str) or not csv_path:
                    raise ValueError()
                path = Path(csv_path)
                if not path.is_absolute():
                    raise ValueError()
                path = path.resolve(strict=True)
                allowed = (self.root / "data" / "vibration").resolve(strict=True)
                if not path.is_relative_to(allowed) or path.suffix.lower() != ".csv" or not path.is_file():
                    raise ValueError()
            except (ValueError, OSError):
                raise DiagnosisError("invalid_csv: 허용된 진동 CSV 경로가 아니거나 파일이 없습니다.") from None
            if not self.python.is_file():
                raise DiagnosisError("python_missing: 진단 Python 환경을 설치하세요.")
            # Hash the input to detect modification during inference; never expose its absolute path.
            digest = await asyncio.to_thread(self.digest, path)
            process = None
            try:
                env = {**os.environ, "PYTHONIOENCODING": "utf-8", "OMP_NUM_THREADS": "2"}
                process = await asyncio.create_subprocess_exec(
                    str(self.python), str(self.root / "tools" / "diagnosis" / "diagnose.py"),
                    "--csv", str(path), "--data-root", str(self.root / "data"),
                    cwd=self.root, env=env, stdout=asyncio.subprocess.PIPE, stderr=asyncio.subprocess.PIPE,
                    creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0)
                stdout, _ = await asyncio.wait_for(process.communicate(), self.timeout)
                if process.returncode != 0:
                    raise DiagnosisError("inference_failed: CSV 형식·유효 샘플·모델 검증 또는 추론에 실패했습니다.")
                if digest != await asyncio.to_thread(self.digest, path):
                    raise DiagnosisError("csv_changed: 진단 중 CSV가 변경되어 결과를 채택하지 않습니다.")
                result = self.validate(json.loads(stdout))
                result.update(source_file_name=path.name, source_sha256=digest)
                return result
            except asyncio.TimeoutError:
                raise DiagnosisError("diagnosis_timeout: 진단 실행 제한 시간을 초과했습니다.") from None
            except (OSError, ValueError, KeyError, TypeError):
                raise DiagnosisError("invalid_result: 진단 실행 또는 결과 형식 검증에 실패했습니다.") from None
            finally:
                if process is not None and process.returncode is None:
                    try:
                        process.kill()
                    except ProcessLookupError:
                        pass
                    await process.communicate()

    @staticmethod
    def digest(path):
        with path.open("rb") as stream:
            return hashlib.file_digest(stream, "sha256").hexdigest()

    def validate(self, output):
        if output["mode"] != "offline_csv_replay" or type(output["sampleCount"]) is not int or output["sampleCount"] < 100:
            raise ValueError()
        fs = output["samplingFrequency"]
        if type(fs) not in (int, float) or not math.isfinite(fs) or fs <= 0:
            raise ValueError()
        models = output["results"]
        keys = {"axis", "bearing", "belt", "rotating"}
        if len(models) != 4 or {m["key"] for m in models} != keys:
            raise ValueError()
        manifest = json.loads((self.root / "tools" / "diagnosis" / "models" / "manifest.json").read_text(encoding="utf-8"))
        result = []
        for model in models:
            p = model["abnormalProbability"]
            if type(p) not in (int, float) or not math.isfinite(p) or not 0 <= p <= 1 or model["threshold"] != 0.5 or type(model["abnormal"]) is not bool or model["abnormal"] != (p >= .5) or model["modelSha256"] != manifest[model["key"]]["sha256"]:
                raise ValueError()
            result.append(dict(key=model["key"], abnormal_probability=p, threshold=.5, abnormal=model["abnormal"], model_sha256=model["modelSha256"]))
        return dict(models=result, sample_count=output["sampleCount"], sampling_frequency=fs)
