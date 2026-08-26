#!/usr/bin/env python3
"""Enforce coverage for the domain and application layers only."""

from __future__ import annotations

import re
import sys
import xml.etree.ElementTree as ET
from pathlib import Path


def main() -> int:
    if len(sys.argv) != 2:
        raise SystemExit("usage: check_coverage.py COVERAGE_COBERTURA_XML")
    root = ET.parse(Path(sys.argv[1])).getroot()
    lines: dict[tuple[str, str], ET.Element] = {}
    for klass in root.findall("./packages/package/classes/class"):
        filename = (klass.get("filename") or "").replace("\\", "/")
        if not re.search(r"src/AniSync\.Next/(Domain|Application)/", filename):
            continue
        for line in klass.findall("./lines/line"):
            key = (filename, line.get("number", ""))
            if key not in lines or int(line.get("hits", "0")) > int(lines[key].get("hits", "0")):
                lines[key] = line

    if not lines:
        raise SystemExit("coverage report contains no AniSync Next domain/application lines")
    covered_lines = sum(int(line.get("hits", "0")) > 0 for line in lines.values())
    branch_covered = branch_total = 0
    for line in lines.values():
        match = re.search(r"\((\d+)/(\d+)\)", line.get("condition-coverage", ""))
        if match:
            branch_covered += int(match.group(1))
            branch_total += int(match.group(2))
    line_rate = covered_lines / len(lines)
    branch_rate = branch_covered / branch_total if branch_total else 1.0
    print(f"domain/application coverage: lines {line_rate:.1%} ({covered_lines}/{len(lines)}), "
          f"branches {branch_rate:.1%} ({branch_covered}/{branch_total})")
    if line_rate < 0.80 or branch_rate < 0.70:
        raise SystemExit("coverage is below the required 80% line / 70% branch threshold")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
