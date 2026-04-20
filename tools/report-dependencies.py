"""Emit a markdown dependency report for the WindowStream repo.

The csproj files and the viewer's `libs.versions.toml` are the source of truth.
This script reads them and produces a consolidated view, so there is no hand-
maintained doc to keep in sync. Pipe to a file if you want to capture a
snapshot:

    python tools/report-dependencies.py > docs/dependencies.md
"""

from __future__ import annotations

import sys
import tomllib
import xml.etree.ElementTree as ElementTree
from dataclasses import dataclass, field
from pathlib import Path


REPO_ROOT = Path(__file__).resolve().parent.parent


@dataclass
class PackageReference:
    name: str
    version: str


@dataclass
class CsProjectReport:
    path: Path
    target_frameworks: str
    references: list[PackageReference] = field(default_factory=list)
    native_copies: list[str] = field(default_factory=list)

    @property
    def display_name(self) -> str:
        return self.path.relative_to(REPO_ROOT).as_posix()


def parse_csproj(path: Path) -> CsProjectReport:
    tree = ElementTree.parse(path)
    root = tree.getroot()

    target_frameworks = ""
    for tfm_element in root.iter("TargetFramework"):
        if tfm_element.text:
            target_frameworks = tfm_element.text.strip()
            break
    for tfms_element in root.iter("TargetFrameworks"):
        if tfms_element.text:
            target_frameworks = tfms_element.text.strip()
            break

    references: list[PackageReference] = []
    for package_reference_element in root.iter("PackageReference"):
        include_attribute = package_reference_element.get("Include")
        version_attribute = package_reference_element.get("Version", "")
        if include_attribute:
            references.append(PackageReference(include_attribute, version_attribute))

    native_copies: list[str] = []
    for copy_element in root.iter("Copy"):
        source = copy_element.get("SourceFiles", "")
        if source and ("ProgramFiles" in source or ".dll" in source):
            native_copies.append(source)

    return CsProjectReport(path, target_frameworks, references, native_copies)


def find_all_csproj_paths() -> list[Path]:
    search_roots = [REPO_ROOT / "src", REPO_ROOT / "tests"]
    discovered: list[Path] = []
    for search_root in search_roots:
        if search_root.exists():
            discovered.extend(sorted(search_root.rglob("*.csproj")))
    return discovered


def parse_libs_versions_toml(path: Path) -> dict[str, object]:
    with path.open("rb") as file_handle:
        return tomllib.load(file_handle)


def render_csproj_section(reports: list[CsProjectReport], heading: str) -> list[str]:
    if not reports:
        return []
    lines: list[str] = [f"## {heading}", ""]
    for report in reports:
        lines.append(f"### `{report.display_name}`")
        lines.append("")
        lines.append(f"- Target framework(s): `{report.target_frameworks or '(unspecified)'}`")
        if report.references:
            lines.append("- Packages:")
            for reference in report.references:
                version_suffix = f" @ `{reference.version}`" if reference.version else ""
                lines.append(f"    - `{reference.name}`{version_suffix}")
        else:
            lines.append("- Packages: none")
        if report.native_copies:
            lines.append("- Native copy tasks:")
            for source in report.native_copies:
                lines.append(f"    - `{source}`")
        lines.append("")
    return lines


def render_kotlin_catalog(catalog: dict[str, object]) -> list[str]:
    lines: list[str] = ["## Viewer (Kotlin / Android) — `viewer/WindowStreamViewer/gradle/libs.versions.toml`", ""]

    versions = catalog.get("versions", {})
    libraries = catalog.get("libraries", {})
    plugins = catalog.get("plugins", {})

    if isinstance(versions, dict) and versions:
        lines.append("### Versions")
        lines.append("")
        for version_key, version_value in sorted(versions.items()):
            lines.append(f"- `{version_key}` = `{version_value}`")
        lines.append("")

    def resolve_version(entry_dict: dict[str, object]) -> str:
        # Gradle's version catalog allows `version.ref = "foo"` (dotted key becomes
        # a nested dict under `version`) or a bare string `version = "1.2.3"`.
        version_field = entry_dict.get("version")
        if isinstance(version_field, dict):
            reference_key = version_field.get("ref")
            if isinstance(reference_key, str) and isinstance(versions, dict):
                resolved = versions.get(reference_key)
                if isinstance(resolved, str):
                    return resolved
                return f"(ref:{reference_key})"
        if isinstance(version_field, str):
            return version_field
        return "(via BOM or plugin)"

    if isinstance(libraries, dict) and libraries:
        lines.append("### Libraries")
        lines.append("")
        for library_key, entry in sorted(libraries.items()):
            if isinstance(entry, dict):
                module = entry.get("module", "(unknown)")
                lines.append(f"- `{library_key}` → `{module}` @ `{resolve_version(entry)}`")
        lines.append("")

    if isinstance(plugins, dict) and plugins:
        lines.append("### Plugins")
        lines.append("")
        for plugin_key, entry in sorted(plugins.items()):
            if isinstance(entry, dict):
                plugin_id = entry.get("id", "(unknown)")
                lines.append(f"- `{plugin_key}` → `{plugin_id}` @ `{resolve_version(entry)}`")
        lines.append("")

    return lines


def partition_csproj_reports(all_reports: list[CsProjectReport]) -> tuple[list[CsProjectReport], list[CsProjectReport]]:
    production: list[CsProjectReport] = []
    tests: list[CsProjectReport] = []
    for report in all_reports:
        if "tests" in report.path.parts or report.path.stem.endswith(".Tests"):
            tests.append(report)
        else:
            production.append(report)
    return production, tests


def main() -> None:
    # Windows consoles default to cp1252; force utf-8 so arrows and similar render.
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8")

    csproj_reports = [parse_csproj(path) for path in find_all_csproj_paths()]
    production_reports, test_reports = partition_csproj_reports(csproj_reports)

    libs_toml_path = REPO_ROOT / "viewer" / "WindowStreamViewer" / "gradle" / "libs.versions.toml"

    out: list[str] = ["# WindowStream — dependency report", ""]
    out.append(
        "Auto-generated by `tools/report-dependencies.py`. "
        "The source of truth is each project's csproj and the viewer's version catalog; "
        "regenerate this report on demand."
    )
    out.append("")

    out.extend(render_csproj_section(production_reports, "Server (.NET) — production"))
    out.extend(render_csproj_section(test_reports, "Server (.NET) — tests"))

    if libs_toml_path.exists():
        catalog = parse_libs_versions_toml(libs_toml_path)
        out.extend(render_kotlin_catalog(catalog))
    else:
        out.append("## Viewer (Kotlin / Android)")
        out.append("")
        out.append(f"- `{libs_toml_path.relative_to(REPO_ROOT).as_posix()}` not found")
        out.append("")

    sys.stdout.write("\n".join(out))
    sys.stdout.write("\n")


if __name__ == "__main__":
    main()
