"""Pure control-state checks; no server, UI, model or database is started."""
import json
import pathlib
import tempfile
import unittest
from delete_refresh_proxy import DeleteRefreshControl


SESSION = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa"
OTHER_SESSION = "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb"
USER = "cccccccc-cccc-4ccc-8ccc-cccccccccccc"
OTHER_USER = "dddddddd-dddd-4ddd-8ddd-dddddddddddd"


class DeleteRefreshControlTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.path = pathlib.Path(self.directory.name) / "control.json"
        self.path.write_text(json.dumps({"schemaVersion": "saaia-winui-delete-refresh-control.v1",
                                       "enabled": True, "state": "waiting_for_delete_ack",
                                       "targetSessionId": SESSION, "targetUserId": USER}), encoding="utf-8")
        self.control = DeleteRefreshControl(self.path)

    def delete(self, session=SESSION, user=USER, status=200):
        return self.control.observe_delete_result("DELETE", f"/chat/sessions/{session}?userId={user}", status)

    def inventory(self, user=USER):
        return self.control.consume_inventory_failure("GET", f"/chat/sessions?userId={user}&limit=200&offset=0")

    def test_inventory_before_successful_target_delete_is_never_failed(self):
        self.assertFalse(self.inventory())
        self.assertFalse(self.delete(status=500))
        self.assertFalse(self.inventory())

    def test_only_exact_target_session_and_user_can_arm_failure(self):
        self.assertFalse(self.delete(session=OTHER_SESSION))
        self.assertFalse(self.delete(user=OTHER_USER))
        self.assertFalse(self.inventory())
        self.assertTrue(self.delete())

    def test_successful_delete_fails_exactly_one_inventory_then_disables_itself(self):
        self.assertTrue(self.delete())
        self.assertTrue(self.inventory())
        self.assertFalse(self.inventory())
        self.assertFalse(self.delete())
        self.assertFalse(self.inventory())
        control = json.loads(self.path.read_text())
        self.assertEqual("consumed", control["state"])
        self.assertIs(False, control["enabled"])
        self.assertEqual(1, control["inventoryFailuresInjected"])

    def test_wrong_user_messages_and_other_methods_do_not_consume_failure(self):
        self.assertTrue(self.delete())
        self.assertFalse(self.inventory(user=OTHER_USER))
        self.assertFalse(self.control.consume_inventory_failure("GET", f"/chat/sessions/{SESSION}/messages?userId={USER}"))
        self.assertFalse(self.control.consume_inventory_failure("POST", f"/chat/sessions?userId={USER}"))
        self.assertFalse(self.control.consume_inventory_failure("GET", "/ready"))
        self.assertTrue(self.inventory())

    def test_default_disabled_control_never_arms_or_fails(self):
        control = json.loads(self.path.read_text())
        control["enabled"] = False
        self.path.write_text(json.dumps(control))
        self.assertFalse(self.delete())
        self.assertFalse(self.inventory())

    def test_duplicate_user_parameters_or_invalid_target_cannot_match(self):
        duplicate = f"/chat/sessions/{SESSION}?userId={USER}&userId={OTHER_USER}"
        self.assertFalse(self.control.observe_delete_result("DELETE", duplicate, 200))
        control = json.loads(self.path.read_text())
        control["targetSessionId"] = "not-a-guid"
        self.path.write_text(json.dumps(control))
        self.assertFalse(self.delete())
        self.assertFalse(self.inventory())


if __name__ == "__main__":
    unittest.main()
