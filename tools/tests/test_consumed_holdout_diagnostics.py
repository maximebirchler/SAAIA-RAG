import importlib.util
from pathlib import Path
import unittest

spec = importlib.util.spec_from_file_location("holdout_diagnostics", Path(__file__).parents[1] / "inspect-consumed-holdout.py")
diagnostics = importlib.util.module_from_spec(spec)
spec.loader.exec_module(diagnostics)


class ConsumedHoldoutDiagnosticsTests(unittest.TestCase):
    def test_successful_server_job_can_finish_with_clarification(self):
        row = {"answerSource": "advanced_analysis:server", "advancedStatus": "Succeeded",
               "advancedResultOutcome": "clarification_required"}
        self.assertEqual("advanced", diagnostics.execution_route(row))
        self.assertEqual("clarification", diagnostics.final_outcome(row))

    def test_successful_server_job_can_finish_with_insufficiency(self):
        row = {"answerSource": "advanced_analysis:server", "advancedStatus": "succeeded",
               "advancedResultOutcome": "insufficient_documentation"}
        self.assertEqual("insufficiency", diagnostics.final_outcome(row))

    def test_failed_or_unknown_server_result_is_never_assumed_to_be_an_answer(self):
        for status, outcome in (("failed", "answered"), ("succeeded", ""), ("succeeded", "unexpected")):
            row = {"answerSource": "advanced_analysis:server", "advancedStatus": status,
                   "advancedResultOutcome": outcome}
            self.assertEqual("unknown", diagnostics.final_outcome(row))

    def test_local_clarification_and_insufficiency_are_distinct_from_answers(self):
        for terminal, expected in (("clarification", "clarification"), ("insufficient_evidence", "insufficiency")):
            self.assertEqual(expected, diagnostics.final_outcome({"answerSource": "source_backed_pipeline_terminal:router_plan:" + terminal}))
        self.assertEqual("answer", diagnostics.final_outcome({"answerSource": "source_backed_pipeline:router_plan:rag.answer"}))

    def test_correct_server_answer_does_not_hide_a_local_route_mismatch(self):
        payload = {"candidateCommit": "frozen", "cases": [{"id": "X1", "expectedTerminal": "local_direct"}],
                   "results": [{"row": {"id": "X1", "answerSource": "advanced_analysis:server",
                                         "advancedStatus": "succeeded", "advancedResultOutcome": "answered"}}],
                   "semanticDecisions": [{"id": "X1", "pass": False}]}
        result = diagnostics.inspect(payload)
        self.assertEqual(1, result["finalOutcomeMatchesDeclaredOracle"])
        self.assertEqual(0, result["answerRouteMatchesDeclaredOracle"])
        self.assertEqual(0, result["originalEvaluatorTrueCounts"]["pass"])
        self.assertFalse(result["oracleFairnessAudited"])

    def test_incomplete_or_duplicate_associations_are_rejected(self):
        payload = {"cases": [{"id": "X1"}], "results": []}
        with self.assertRaises(ValueError):
            diagnostics.inspect(payload)
        payload["results"] = [{"row": {"id": "X1"}}, {"row": {"id": "X1"}}]
        with self.assertRaises(ValueError):
            diagnostics.inspect(payload)


if __name__ == "__main__":
    unittest.main()
