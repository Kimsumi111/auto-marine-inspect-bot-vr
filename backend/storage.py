import json
import sqlite3
from pathlib import Path


class Storage:
    """Single-event-loop access; a lifetime owner transaction excludes second processes."""
    def __init__(self, path):
        path = Path(path)
        path.parent.mkdir(parents=True, exist_ok=True)
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
