import json
import sqlite3
import logging
from logging.handlers import RotatingFileHandler
from pathlib import Path


class Storage:
    """Single-event-loop access; a lifetime owner transaction excludes second processes."""
    def __init__(self, path):
        path = Path(path)
        path.parent.mkdir(parents=True, exist_ok=True)
        self.audit_logger = logging.getLogger("metamarine.audit." + str(path.resolve()))
        self.audit_logger.setLevel(logging.INFO)
        self.audit_logger.propagate = False
        self.audit_handler = RotatingFileHandler(path.parent / "agent-events.jsonl", maxBytes=5_000_000, backupCount=3, encoding="utf-8")
        self.audit_handler.setFormatter(logging.Formatter("%(message)s"))
        self.audit_logger.addHandler(self.audit_handler)
        # Separate DB means durable mission commits do not release the owner lock.
        self.owner = sqlite3.connect(str(path) + ".owner", timeout=0)
        try:
            self.owner.execute("BEGIN EXCLUSIVE")
            self.db = sqlite3.connect(path, timeout=5)
            self.db.execute("PRAGMA journal_mode=WAL")
            self.db.execute("PRAGMA synchronous=FULL")
            self.db.execute("CREATE TABLE IF NOT EXISTS missions (id TEXT PRIMARY KEY, request_id TEXT UNIQUE NOT NULL, document TEXT NOT NULL)")
            self.db.execute("CREATE TABLE IF NOT EXISTS events (id INTEGER PRIMARY KEY, mission_id TEXT, at TEXT, state TEXT, message TEXT)")
            self.db.commit()
        except Exception:
            self.owner.close()
            self.audit_logger.removeHandler(self.audit_handler)
            self.audit_handler.close()
            raise

    def load(self):
        return {mid: json.loads(doc) for mid, doc in self.db.execute("SELECT id,document FROM missions")}

    def save(self, job):
        state = job["snapshot"]
        with self.db:
            self.db.execute("INSERT INTO missions VALUES(?,?,?) ON CONFLICT(id) DO UPDATE SET document=excluded.document",
                            (state["mission_id"], state["request_id"], json.dumps(job, ensure_ascii=False, allow_nan=False)))
            self.db.execute("INSERT INTO events(mission_id,at,state,message) VALUES(?,?,?,?)",
                            (state["mission_id"], state["updated_at"], state["state"], state["message"]))

    def close(self):
        self.db.close()
        self.owner.close()
        self.audit_logger.removeHandler(self.audit_handler)
        self.audit_handler.close()

    def audit(self, mission_id, event):
        self.audit_logger.info(json.dumps(dict(mission_id=mission_id, **event), ensure_ascii=False, allow_nan=False))
