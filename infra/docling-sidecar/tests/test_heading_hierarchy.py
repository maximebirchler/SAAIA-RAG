from __future__ import annotations

import os
import unittest
from types import SimpleNamespace
from unittest.mock import patch

from saaia_docling.heading_hierarchy import (
    HeadingHierarchyConfiguration,
    apply_heading_hierarchy_options,
)


class HeadingHierarchyTests(unittest.TestCase):
    def test_enables_official_docling_hierarchy_and_parsed_pages(self) -> None:
        hierarchy = SimpleNamespace(
            enabled=False,
            use_bookmarks=False,
            use_numbering=False,
            use_style=False,
            max_level=1,
            bookmark_match_threshold=0.1,
        )
        pipeline = SimpleNamespace(
            heading_hierarchy_options=hierarchy,
            generate_parsed_pages=False,
        )
        configuration = HeadingHierarchyConfiguration(
            enabled=True,
            use_bookmarks=True,
            use_numbering=True,
            use_style=True,
            max_level=6,
            bookmark_match_threshold=0.8,
        )

        result = apply_heading_hierarchy_options(
            pipeline,
            configuration,
        )

        self.assertIs(pipeline, result)
        self.assertTrue(hierarchy.enabled)
        self.assertTrue(hierarchy.use_bookmarks)
        self.assertTrue(hierarchy.use_numbering)
        self.assertTrue(hierarchy.use_style)
        self.assertEqual(6, hierarchy.max_level)
        self.assertEqual(0.8, hierarchy.bookmark_match_threshold)
        self.assertTrue(pipeline.generate_parsed_pages)

    def test_disabled_configuration_leaves_pipeline_unchanged(self) -> None:
        pipeline = SimpleNamespace(marker=object())
        configuration = HeadingHierarchyConfiguration(
            enabled=False,
            use_bookmarks=True,
            use_numbering=True,
            use_style=True,
            max_level=6,
            bookmark_match_threshold=0.8,
        )

        result = apply_heading_hierarchy_options(
            pipeline,
            configuration,
        )

        self.assertIs(pipeline, result)
        self.assertFalse(hasattr(pipeline, "generate_parsed_pages"))

    def test_environment_values_are_bounded(self) -> None:
        values = {
            "SAAIA_DOCLING_HEADING_HIERARCHY_ENABLED": "true",
            "SAAIA_DOCLING_HEADING_HIERARCHY_USE_BOOKMARKS": "false",
            "SAAIA_DOCLING_HEADING_HIERARCHY_USE_NUMBERING": "true",
            "SAAIA_DOCLING_HEADING_HIERARCHY_USE_STYLE": "false",
            "SAAIA_DOCLING_HEADING_HIERARCHY_MAX_LEVEL": "999",
            "SAAIA_DOCLING_HEADING_HIERARCHY_BOOKMARK_THRESHOLD": "-2",
        }

        with patch.dict(os.environ, values, clear=False):
            configuration = (
                HeadingHierarchyConfiguration.from_environment()
            )

        self.assertTrue(configuration.enabled)
        self.assertFalse(configuration.use_bookmarks)
        self.assertTrue(configuration.use_numbering)
        self.assertFalse(configuration.use_style)
        self.assertEqual(100, configuration.max_level)
        self.assertEqual(0.0, configuration.bookmark_match_threshold)
