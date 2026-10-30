"""Pure regression for independent selectedItem identity and documentary claim text."""
import copy
from pathlib import Path
import sys
import unittest

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
import verify_planning_result as planning
import test_planning_result as fixtures


class ClaimTextContractTests(unittest.TestCase):

    def fixture(self):
        value = fixtures.fixture(20)
        for claim in value['api']['result']['claims']:
            claim['text'] = 'Ingredients and preparation are documented.'
        fixtures.rebind(value)
        return value

    def evaluate(self, value):
        return planning.evaluate(*(value[k] for k in
            ('profile', 'corpus', 'submission', 'api', 'sql', 'traces', 'probes')))

    def test_short_supported_claim_text_does_not_repeat_selected_title(self):
        value = self.fixture(); before = copy.deepcopy(value)
        result = self.evaluate(value)
        self.assertEqual(20, result['gridCellCount'])
        self.assertFalse(result['semanticPlanningApproved'])
        self.assertFalse(result['functionalFiveDayAccepted'])
        self.assertEqual(before, value)


    def test_empty_wrong_typed_and_8001_character_claim_text_refuse(self):
        for text in (None, True, 3, {}, [], '', ' \n\t ', 'x' * 8001):
            value = self.fixture(); value['api']['result']['claims'][0]['text'] = text
            fixtures.rebind(value)
            with self.subTest(text_type=type(text).__name__), self.assertRaisesRegex(ValueError, 'planning_empty_or_misbound_grid_cell'):
                self.evaluate(value)

    def test_trimmed_8000_character_claim_text_boundary_is_retained(self):
        value = self.fixture(); value['api']['result']['claims'][0]['text'] = '  ' + 'x' * 8000 + '  '
        fixtures.rebind(value)
        self.assertTrue(self.evaluate(value)['passed'])

    def test_wrong_selected_name_in_grid_cell_still_refuses(self):
        value = self.fixture()
        value['api']['result']['answerText'] = value['api']['result']['answerText'].replace('Recette 1 [C1]', 'Wrong name [C1]')
        fixtures.rebind(value)
        with self.assertRaisesRegex(ValueError, 'planning_empty_or_misbound_grid_cell'):
            self.evaluate(value)

    def test_wrong_cell_claim_marker_still_refuses(self):
        value = self.fixture()
        value['api']['result']['answerText'] = value['api']['result']['answerText'].replace('[C20]', '[C19]')
        fixtures.rebind(value)
        with self.assertRaisesRegex(ValueError, 'planning_unknown_or_repeated_visible_citation'):
            self.evaluate(value)

    def test_wrong_claim_evidence_id_still_refuses(self):
        value = self.fixture(); value['api']['result']['claims'][0]['evidenceIds'] = ['unknown-proof']
        fixtures.rebind(value)
        with self.assertRaisesRegex(ValueError, 'planning_unknown_citation_or_unused_card'):
            self.evaluate(value)

    def test_sql_canonical_generation_change_still_refuses(self):
        value = self.fixture(); value['sql']['canonicalHashes'][0]['sha256'] = 'f' * 64
        with self.assertRaisesRegex(ValueError, 'planning_sql_canonical_generation_changed'):
            self.evaluate(value)

    def test_changed_visible_body_still_refuses(self):
        value = self.fixture(); value['payload']['evidence'][0]['content'] = 'Invented source text.'
        fixtures.rebind(value)
        with self.assertRaisesRegex(ValueError, 'planning_visible_body_not_canonical_exact'):
            self.evaluate(value)

    def test_missing_final_visible_body_still_refuses(self):
        value = self.fixture(); value['payload']['evidence'].pop()
        fixtures.rebind(value)
        with self.assertRaisesRegex(ValueError, 'planning_cited_body_missing_from_final_model_input'):
            self.evaluate(value)

    def test_unread_result_reference_still_refuses(self):
        value = self.fixture(); value['sql']['events'][0]['evidence'].pop()
        with self.assertRaisesRegex(ValueError, 'planning_result_citation_not_read_by_real_tools'):
            self.evaluate(value)


if __name__ == '__main__': unittest.main(verbosity=2)
