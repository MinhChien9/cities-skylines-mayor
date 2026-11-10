#!/usr/bin/env python3
"""Check that the documented bridge endpoints match the route table in the source.

The API reference (docs/04-api-reference.md) and the route table
(src/Bridge/ApiRoutes.cs) must agree. It is easy for one to gain an endpoint and the
other to miss it, and a stale API reference is worse than none.

Exits 0 when they agree, 1 otherwise.

Usage:
    python scripts/check-api-docs.py
"""

from __future__ import annotations

import os
import re
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DOCS = os.path.join(ROOT, "docs", "04-api-reference.md")
ROUTES = os.path.join(ROOT, "src", "Bridge", "ApiRoutes.cs")

# Endpoints that are part of the protocol rather than the documented API.
IGNORED_PATHS = {"/health"}


def documented_paths(text: str) -> set[str]:
    """Paths listed in the endpoint tables of the API reference.

    Only table rows are considered, so prose examples and URLs do not create false
    positives.
    """
    paths = set()
    row = re.compile(r"^\|\s*(GET|POST)\s*\|\s*`(/[^`]+)`\s*\|", re.MULTILINE)
    for match in row.finditer(text):
        paths.add(match.group(2))
    return paths


def routed_paths(text: str) -> set[str]:
    """Paths registered in the route table."""
    return set(re.findall(r'Map(?:Get|Post)\("(/[^"]+)"', text))


def main() -> int:
    for path in (DOCS, ROUTES):
        if not os.path.exists(path):
            print(f"error: {path} not found", file=sys.stderr)
            return 1

    with open(DOCS, encoding="utf-8") as handle:
        doc_text = handle.read()
    with open(ROUTES, encoding="utf-8") as handle:
        route_text = handle.read()

    docs = documented_paths(doc_text)
    routes = routed_paths(route_text)

    missing_in_docs = sorted(routes - docs)
    missing_in_routes = sorted(docs - routes - IGNORED_PATHS)

    failed = False

    if missing_in_docs:
        failed = True
        print("These endpoints exist in the route table but are not documented:", file=sys.stderr)
        for path in missing_in_docs:
            print(f"  {path}", file=sys.stderr)

    if missing_in_routes:
        failed = True
        print("These endpoints are documented but not in the route table:", file=sys.stderr)
        for path in missing_in_routes:
            print(f"  {path}", file=sys.stderr)

    if failed:
        print("", file=sys.stderr)
        print("Update docs/04-api-reference.md and src/Bridge/ApiRoutes.cs so they agree.", file=sys.stderr)
        return 1

    print(f"API reference and route table agree ({len(routes)} endpoints).")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
