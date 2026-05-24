#!/usr/bin/env python3
"""Print line-coverage percent from one or more Cobertura XML files.

Merges multiple reports at the line level (a line counts as covered if
any report shows hits>0), so per-test-project coverlet outputs don't
double-count. Prints a single number like `97.86` to stdout.

Usage: python ci/coverage-percent.py <glob_or_path> [<glob_or_path> ...]
"""
import glob
import sys
import xml.etree.ElementTree as ElementTree


def main(argv: list[str]) -> int:
    patterns = argv[1:] or ["**/coverage.cobertura.xml"]
    paths: list[str] = []
    for pattern in patterns:
        paths.extend(p for p in glob.glob(pattern, recursive=True)
                     if "/bin/" not in p and "/obj/" not in p
                     and "\\bin\\" not in p and "\\obj\\" not in p)
    if not paths:
        print("error: no Cobertura XML matched", file=sys.stderr)
        return 1
    lines: dict[tuple[str, str], bool] = {}
    for path in paths:
        root = ElementTree.parse(path).getroot()
        for class_node in root.iter("class"):
            filename = class_node.get("filename", "")
            for line_node in class_node.iter("line"):
                key = (filename, line_node.get("number", ""))
                lines[key] = lines.get(key, False) or int(line_node.get("hits", "0")) > 0
    total = len(lines)
    if total == 0:
        print("error: no source lines in Cobertura XML", file=sys.stderr)
        return 1
    covered = sum(1 for is_covered in lines.values() if is_covered)
    print(f"{round(100.0 * covered / total, 2)}")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
