"""Hidden asynchronous invocation of the existing validated CSV diagnosis CLI."""
import asyncio
import json
import os
from pathlib import Path
import subprocess
import sys
from .contracts import DiagnosisResult


class DiagnosisRunner:
    def __init__(self):
        self.root = Path(__file__).resolve().parents[1]
        dedicated = self.root / ".venv-diagnosis/Scripts/python.exe"
        self.python = Path(os.getenv("SHIP_DIAGNOSIS_PYTHON",
            str(dedicated if dedicated.exists() else sys.executable)))
        self.lock = asyncio.Lock()

    async def run(self, inspection_id, source_path):
        root = self.root / "data"
        path = Path(source_path).resolve(strict=True)
        if not path.is_relative_to((root / "vibration").resolve()) or path.suffix.lower() != ".csv":
            raise ValueError("Invalid vibration source")
        async with self.lock:
            process = await asyncio.create_subprocess_exec(str(self.python),
                str(self.root / "tools/diagnosis/diagnose.py"), "--csv", str(path),
                "--data-root", str(root), stdout=asyncio.subprocess.PIPE,
                stderr=asyncio.subprocess.PIPE,
                creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0)
            try:
                stdout, stderr = await asyncio.wait_for(process.communicate(), 45)
            except BaseException:
                if process.returncode is None:
                    process.kill()
                await process.wait()
                raise
            if process.returncode != 0:
                raise ValueError("Diagnosis process failed")
            value = json.loads(stdout)
            return DiagnosisResult(inspection_id=inspection_id, file_name=value["fileName"],
                sample_count=value["sampleCount"], sampling_frequency=value["samplingFrequency"],
                mode=value["mode"], results=[dict(key=r["key"],
                    abnormal_probability=r["abnormalProbability"], threshold=r["threshold"],
                    abnormal=r["abnormal"], model_sha256=r["modelSha256"]) for r in value["results"]])
