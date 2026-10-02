"""Run from repo root: python -m backend.export_schemas."""
import json
from pathlib import Path
from . import contracts


def main():
    destination = Path(__file__).parent / "schemas"
    destination.mkdir(exist_ok=True)
    names = ["CreateMissionRequest", "MissionAccepted", "MissionSnapshot", "MissionResult",
             "CancelAccepted", "ErrorDetail", "EquipmentInfo", "DiagnosisResult", "CommandReceipt"]
    models = {name: getattr(contracts, name) for name in names}
    models.update({f"tool_{name}": model for name, model in contracts.TOOL_ARGUMENT_MODELS.items()})
    for name, model in models.items():
        (destination / f"{name}.json").write_text(
            json.dumps(model.model_json_schema(), ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    main()
