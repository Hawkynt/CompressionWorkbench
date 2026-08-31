#!/usr/bin/env python3
from __future__ import annotations

import re
from pathlib import Path

ROOTS = [Path("Hawkynt.Algorithms.Hashing"), Path("Hawkynt.Algorithms.Checksums")]
SOURCES = [
    (Path("Hawkynt.Algorithms.Hashing/SupportedHashSizes.cs"), "hash"),
    (Path("Hawkynt.Algorithms.Hashing/Checksums/HashOutputSizeExtensions.cs"), "hash"),
    (Path("Hawkynt.Algorithms.Checksums/SupportedChecksumSizes.cs"), "checksum"),
    (Path("Hawkynt.Algorithms.Checksums/Checksums/SupportedChecksumSizes.cs"), "checksum"),
]

EXTENSION_RE = re.compile(
    r"(?ms)(?P<classdocs>(?:^[ \t]*///.*\n)*)"
    r"(?P<indent>^[ \t]*)public static class (?P<class>[A-Za-z_][A-Za-z0-9_]*)\s*\{\s*"
    r"extension\((?P<receiver>[A-Za-z_][A-Za-z0-9_]*)\)\s*\{\s*"
    r"(?P<propdocs>(?:[ \t]*///.*\n)*)[ \t]*"
    r"public static (?P<type>IReadOnlyList<(?P<range>HashSizeRange|ChecksumSizeRange)>) "
    r"(?P<property>Supported(?:Hash|Checksum)Sizes)\s*=>\s*(?P<expr>[^;]+);\s*\}\s*\}"
)

TYPE_RE_TEMPLATE = (
    r"(?m)^(?P<indent>[ \t]*)public[ \t]+"
    r"(?:(?:static|sealed|abstract|readonly|ref|partial)[ \t]+)*"
    r"(?:class|struct|record(?:[ \t]+(?:class|struct))?)[ \t]+{name}\b[^{{;]*\{{"
)


def summary(noun: str) -> str:
    return f"Gets the supported {noun}-output sizes, in bits."


def normalized_helper(match: re.Match[str], noun: str) -> str:
    indent = match.group("indent")
    class_docs = match.group("classdocs")
    if not class_docs:
        class_docs = (
            f'{indent}/// <summary>\n'
            f'{indent}/// Provides supported {noun}-output size metadata for <see cref="{match.group("receiver")}"/>.\n'
            f'{indent}/// </summary>\n'
        )
    prop_docs = (
        f'{indent}  /// <summary>\n'
        f'{indent}  /// {summary(noun)}\n'
        f'{indent}  /// </summary>\n'
    )
    return (
        class_docs
        + f'{indent}public static class {match.group("class")} {{\n'
        + prop_docs
        + f'{indent}  public static {match.group("type")} {match.group("property")} => {match.group("expr").strip()};\n'
        + f'{indent}}}'
    )


def find_receiver(receiver: str, excluded: set[Path]) -> tuple[Path, re.Match[str]]:
    pattern = re.compile(TYPE_RE_TEMPLATE.format(name=re.escape(receiver)))
    hits: list[tuple[Path, re.Match[str]]] = []
    for root in ROOTS:
        for path in root.rglob("*.cs"):
            if path in excluded or any(part in {"bin", "obj"} for part in path.parts):
                continue
            text = path.read_text(encoding="utf-8")
            match = pattern.search(text)
            if match:
                hits.append((path, match))
    if len(hits) != 1:
        raise SystemExit(f"Expected exactly one declaration for {receiver}, found {[str(path) for path, _ in hits]}")
    return hits[0]


def add_direct_property(receiver: str, property_name: str, range_type: str, expr: str, noun: str, excluded: set[Path]) -> None:
    path, match = find_receiver(receiver, excluded)
    text = path.read_text(encoding="utf-8")

    # A receiver only appears in these extension maps when it lacks the direct metadata member.
    # Still make the migration idempotent in case the workflow is rerun after its bot commit.
    declaration_start = match.start()
    declaration_open = match.end() - 1
    next_member_window = text[declaration_open: min(len(text), declaration_open + 1200)]
    if re.search(rf"\bpublic\s+static\s+(?:global::[^\s]+|IReadOnlyList<[^>]+>)\s+{re.escape(property_name)}\b", next_member_window):
        return

    type_namespace = "Hawkynt.Algorithms.Hashing" if range_type == "HashSizeRange" else "Hawkynt.Algorithms.Checksums"
    qualified_type = f"global::System.Collections.Generic.IReadOnlyList<global::{type_namespace}.{range_type}>"
    qualified_expr = expr
    if expr.startswith("HashSizeSets."):
        qualified_expr = "global::Hawkynt.Algorithms.Hashing." + expr
    elif expr.startswith("ChecksumSizeSets."):
        qualified_expr = "global::Hawkynt.Algorithms.Checksums." + expr

    indent = match.group("indent") + "  "
    block = (
        "\n"
        f"{indent}/// <summary>\n"
        f"{indent}/// {summary(noun)}\n"
        f"{indent}/// </summary>\n"
        f"{indent}public static {qualified_type} {property_name} => {qualified_expr};\n"
    )
    text = text[:declaration_open + 1] + block + text[declaration_open + 1:]
    path.write_text(text, encoding="utf-8")
    print(f"materialized {receiver}.{property_name} in {path}")


def process_source(path: Path, noun: str, excluded: set[Path]) -> None:
    text = path.read_text(encoding="utf-8")
    matches = list(EXTENSION_RE.finditer(text))
    if not matches:
        if "extension(" in text:
            raise SystemExit(f"Could not parse extension metadata declarations in {path}")
        return

    mappings = [
        (
            match.group("receiver"),
            match.group("property"),
            match.group("range"),
            match.group("expr").strip(),
        )
        for match in matches
    ]

    text = EXTENSION_RE.sub(lambda match: normalized_helper(match, noun), text)
    path.write_text(text, encoding="utf-8")

    for receiver, property_name, range_type, expr in mappings:
        add_direct_property(receiver, property_name, range_type, expr, noun, excluded)


excluded = {path for path, _ in SOURCES}
for source, noun in SOURCES:
    process_source(source, noun, excluded)
