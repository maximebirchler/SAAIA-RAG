from __future__ import annotations

import os
from dataclasses import dataclass
from typing import Any, Callable


def _environment_bool(name: str, default: bool) -> bool:
    value = os.getenv(name)
    if value is None:
        return default
    return value.strip().casefold() in {"1", "true", "yes", "on"}


@dataclass(frozen=True)
class HeadingHierarchyConfiguration:
    enabled: bool
    use_bookmarks: bool
    use_numbering: bool
    use_style: bool
    max_level: int
    bookmark_match_threshold: float

    @classmethod
    def from_environment(cls) -> "HeadingHierarchyConfiguration":
        return cls(
            enabled=_environment_bool(
                "SAAIA_DOCLING_HEADING_HIERARCHY_ENABLED",
                True,
            ),
            use_bookmarks=_environment_bool(
                "SAAIA_DOCLING_HEADING_HIERARCHY_USE_BOOKMARKS",
                True,
            ),
            use_numbering=_environment_bool(
                "SAAIA_DOCLING_HEADING_HIERARCHY_USE_NUMBERING",
                True,
            ),
            use_style=_environment_bool(
                "SAAIA_DOCLING_HEADING_HIERARCHY_USE_STYLE",
                True,
            ),
            max_level=min(
                max(
                    int(
                        os.getenv(
                            "SAAIA_DOCLING_HEADING_HIERARCHY_MAX_LEVEL",
                            "6",
                        )
                    ),
                    1,
                ),
                100,
            ),
            bookmark_match_threshold=min(
                max(
                    float(
                        os.getenv(
                            "SAAIA_DOCLING_HEADING_HIERARCHY_BOOKMARK_THRESHOLD",
                            "0.8",
                        )
                    ),
                    0.0,
                ),
                1.0,
            ),
        )


def apply_heading_hierarchy_options(
    pipeline_options: Any,
    configuration: HeadingHierarchyConfiguration,
) -> Any:
    if not configuration.enabled:
        return pipeline_options

    hierarchy = pipeline_options.heading_hierarchy_options
    hierarchy.enabled = True
    hierarchy.use_bookmarks = configuration.use_bookmarks
    hierarchy.use_numbering = configuration.use_numbering
    hierarchy.use_style = configuration.use_style
    hierarchy.max_level = configuration.max_level
    hierarchy.bookmark_match_threshold = (
        configuration.bookmark_match_threshold
    )
    if configuration.use_style:
        pipeline_options.generate_parsed_pages = True
    return pipeline_options


def install_heading_hierarchy_patch(
    configuration: HeadingHierarchyConfiguration | None = None,
) -> HeadingHierarchyConfiguration:
    configuration = (
        configuration or HeadingHierarchyConfiguration.from_environment()
    )
    if not configuration.enabled:
        return configuration

    from docling_jobkit.convert.manager import DoclingConverterManager

    current: Callable[..., Any] = (
        DoclingConverterManager._parse_standard_pdf_opts
    )
    if getattr(current, "_saaia_heading_hierarchy_patch", False):
        return configuration

    def patched(self: Any, request: Any, artifacts_path: Any) -> Any:
        options = current(self, request, artifacts_path)
        return apply_heading_hierarchy_options(options, configuration)

    patched._saaia_heading_hierarchy_patch = True  # type: ignore[attr-defined]
    DoclingConverterManager._parse_standard_pdf_opts = patched
    return configuration
