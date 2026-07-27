from __future__ import annotations

import logging

from .heading_hierarchy import install_heading_hierarchy_patch


def main() -> None:
    configuration = install_heading_hierarchy_patch()
    logging.getLogger("saaia_docling").info(
        "Docling heading hierarchy enabled=%s bookmarks=%s numbering=%s "
        "style=%s max_level=%s bookmark_threshold=%s",
        configuration.enabled,
        configuration.use_bookmarks,
        configuration.use_numbering,
        configuration.use_style,
        configuration.max_level,
        configuration.bookmark_match_threshold,
    )

    from docling_serve.__main__ import main as docling_serve_main

    docling_serve_main()


if __name__ == "__main__":
    main()
