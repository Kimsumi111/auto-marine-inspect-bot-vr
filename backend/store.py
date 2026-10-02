"""Single-process SQLite mission storage; retained until explicitly removed."""
import sqlite3
import json
from datetime import datetime, timezone
from pathlib import Path
from .contracts import MissionSnapshot


class Store:
    def __init__(self, path):
        if str(path) != ":memory:":
            Path(path).parent.mkdir(parents=True, exist_ok=True)
        self.db = sqlite3.connect(path, check_same_thread=False)
        self.db.execute("CREATE TABLE IF NOT EXISTS missions (id TEXT PRIMARY KEY, request_id TEXT UNIQUE, command TEXT, snapshot TEXT)")
        self.db.commit()
        self.db.execute("CREATE TABLE IF NOT EXISTS events (mission_id TEXT, kind TEXT, at TEXT, data TEXT)")
        self.db.commit()

    def event(self, mid, kind, data):
        self.db.execute("INSERT INTO events VALUES (?,?,?,?)", (mid,kind,
            datetime.now(timezone.utc).isoformat(),json.dumps(data,ensure_ascii=False)))
        self.db.commit()

    def events(self, mid):
        return [dict(kind=r[0],at=r[1],data=json.loads(r[2])) for r in
                self.db.execute("SELECT kind,at,data FROM events WHERE mission_id=? ORDER BY rowid",(mid,))]

    def save(self, snapshot, request_id=None, command=None):
        if request_id is None:
            self.db.execute("UPDATE missions SET snapshot=? WHERE id=?", (snapshot.model_dump_json(), snapshot.mission_id))
        else:
            self.db.execute("INSERT INTO missions VALUES (?,?,?,?)", (snapshot.mission_id, request_id, command, snapshot.model_dump_json()))
        self.db.commit()

    def get(self, mission_id):
        row = self.db.execute("SELECT snapshot FROM missions WHERE id=?", (mission_id,)).fetchone()
        return MissionSnapshot.model_validate_json(row[0]) if row else None

    def request(self, request_id):
        return self.db.execute("SELECT id,command FROM missions WHERE request_id=?", (request_id,)).fetchone()

    def all(self):
        return [MissionSnapshot.model_validate_json(row[0]) for row in self.db.execute("SELECT snapshot FROM missions")]

    def close(self):
        self.db.close()
