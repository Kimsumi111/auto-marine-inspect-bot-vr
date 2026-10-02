import json
import threading
import unittest
import urllib.error
import urllib.request
import uuid
from http.server import ThreadingHTTPServer
from server import Store, handler


class ContractTests(unittest.TestCase):
    def setUp(self):
        self.now = 0
        self.store = Store(clock=lambda: self.now)
        self.server = ThreadingHTTPServer(("127.0.0.1", 0), handler(self.store))
        self.thread = threading.Thread(target=self.server.serve_forever, daemon=True)
        self.thread.start()
        self.base = "http://127.0.0.1:%d" % self.server.server_port
        self.body = dict(api_version=1, request_id=str(uuid.uuid4()), text="설비 A와 B를 점검해줘", execution_mode="simulation")

    def tearDown(self):
        self.server.shutdown()
        self.server.server_close()
        self.thread.join()

    def call(self, path, body=None):
        req = urllib.request.Request(self.base + path, data=None if body is None else json.dumps(body).encode(), headers={"Content-Type": "application/json"})
        try:
            with urllib.request.urlopen(req, timeout=3) as response:
                return response.status, json.load(response)
        except urllib.error.HTTPError as response:
            return response.code, json.load(response)

    def test_success_and_independent_results(self):
        code, first = self.call("/mission", self.body)
        self.assertEqual(code, 202)
        self.assertEqual(first["state"], "PENDING")
        self.now = 14
        _, final = self.call("/mission/" + first["mission_id"])
        self.assertEqual(final["state"], "COMPLETED")
        _, report = self.call("/mission/" + first["mission_id"] + "/result")
        self.assertEqual(len(report["points"]), 4)
        self.assertTrue(report["points"][2]["models"][1]["abnormal"])
        self.assertEqual(report["transport_mode"], "mock")

    def test_duplicate_and_recovery(self):
        _, first = self.call("/mission", self.body)
        code, again = self.call("/mission", self.body)
        self.assertEqual(code, 200)
        self.assertEqual(first["mission_id"], again["mission_id"])
        _, recovered = self.call("/mission/by-request/" + self.body["request_id"])
        self.assertEqual(first["mission_id"], recovered["mission_id"])
        self.assertEqual(len(self.store.jobs), 1)

    def test_id_conflict(self):
        self.call("/mission", self.body)
        self.body["text"] = "다른 요청"
        self.assertEqual(self.call("/mission", self.body)[1]["error"]["code"], "id_conflict")

    def test_busy(self):
        self.call("/mission", self.body)
        self.body["request_id"] = str(uuid.uuid4())
        self.assertEqual(self.call("/mission", self.body)[1]["error"]["code"], "mission_busy")

    def test_cancel_is_not_immediate_stop(self):
        _, first = self.call("/mission", self.body)
        path = "/mission/" + first["mission_id"]
        code, cancel = self.call(path + "/cancel", {})
        self.assertEqual((code, cancel["state"], cancel["stop_confirmed"]), (202, "CANCELLING", False))
        self.now = 3
        _, final = self.call(path)
        self.assertEqual((final["state"], final["stop_confirmed"]), ("CANCELLED", True))
        self.assertEqual(self.call(path + "/cancel", {})[1]["revision"], final["revision"])

    def test_stop_unknown_locks_new_mission(self):
        self.store.scenario = "stop-unconfirmed"
        _, first = self.call("/mission", self.body)
        path = "/mission/" + first["mission_id"]
        self.call(path + "/cancel", {})
        self.now = 3
        _, final = self.call(path)
        self.assertTrue(final["requires_attention"])
        self.assertFalse(final["stop_confirmed"])
        self.body["request_id"] = str(uuid.uuid4())
        self.assertEqual(self.call("/mission", self.body)[0], 409)

    def test_failure_not_normal(self):
        self.store.scenario = "failure"
        _, first = self.call("/mission", self.body)
        self.now = 10
        _, report = self.call("/mission/" + first["mission_id"] + "/result")
        self.assertEqual(report["state"], "FAILED")
        self.assertEqual(report["points"][1]["models"], [])
        self.assertEqual(report["points"][1]["status"], "NOT_EVALUATED")

    def test_validation(self):
        self.body["execution_mode"] = "physical"
        self.assertEqual(self.call("/mission", self.body)[0], 422)
        self.assertEqual(len(self.store.jobs), 0)

    def test_missing_and_not_ready(self):
        self.assertEqual(self.call("/mission/by-request/" + self.body["request_id"])[0], 404)
        _, first = self.call("/mission", self.body)
        self.assertEqual(self.call("/mission/" + first["mission_id"] + "/result")[0], 409)


if __name__ == "__main__":
    unittest.main()
