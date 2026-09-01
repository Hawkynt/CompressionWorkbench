#!/usr/bin/env python3
from __future__ import annotations

import re
from pathlib import Path

ROOTS = [
    Path("Compression.Core"),
    Path("Codecs"),
    Path("FileFormats"),
    Path("FileSystems"),
    Path("Hawkynt.Algorithms.Checksums"),
    Path("Hawkynt.Algorithms.Hashing"),
]

DOC_RE = re.compile(r"^\s*///")
ATTR_RE = re.compile(r"^\s*\[")
ACCESS_RE = re.compile(r"\b(public|protected)\b")
TYPE_RE = re.compile(
    r"\b(public|protected)\s+(?:(?:new|static|sealed|abstract|readonly|ref|partial|unsafe)\s+)*"
    r"(?P<kind>class|struct|interface|enum|record(?:\s+(?:class|struct))?)\s+(?P<name>[A-Za-z_][A-Za-z0-9_]*)"
)
IDENT_RE = re.compile(r"[A-Za-z_][A-Za-z0-9_]*")


def humanize(name: str) -> str:
    name = name.replace("_", " ")
    name = re.sub(r"(?<=[a-z0-9])(?=[A-Z])", " ", name)
    name = re.sub(r"(?<=[A-Z])(?=[A-Z][a-z])", " ", name)
    name = re.sub(r"(?<=[A-Za-z])(?=[0-9])", " ", name)
    return re.sub(r"\s+", " ", name).strip()


def article(word: str) -> str:
    return "an" if word[:1].lower() in "aeiou" else "a"


def type_summary(name: str, kind: str) -> str:
    noun = humanize(name)
    lower = noun.lower()
    if kind == "enum":
        return f"Specifies the supported {lower} values."
    if kind == "interface":
        return f"Defines the contract for {lower} implementations."
    suffixes = [
        ("FormatDescriptor", "Describes and exposes operations for the {0} format."),
        ("Descriptor", "Describes {0}."),
        ("Reader", "Reads and parses {0} data."),
        ("Writer", "Writes {0} data."),
        ("Decoder", "Decodes {0} data."),
        ("Encoder", "Encodes {0} data."),
        ("Codec", "Provides {0} encoding and decoding operations."),
        ("Parser", "Parses {0} data."),
        ("Entry", "Represents {0}."),
        ("Header", "Represents the {0}."),
        ("Options", "Specifies options for {0}."),
        ("Info", "Provides information about {0}."),
        ("Metadata", "Represents metadata for {0}."),
        ("Result", "Represents the result of {0}."),
        ("Stream", "Provides a stream implementation for {0}."),
        ("Exception", "Represents an error reported by {0}."),
        ("Collection", "Represents a collection of {0}."),
        ("Builder", "Builds {0} values."),
        ("LayoutMap", "Enumerates the physical layout of {0}."),
        ("InPlaceModifier", "Provides in-place modification operations for {0}."),
    ]
    for suffix, template in suffixes:
        if name.endswith(suffix):
            subject = humanize(name.removesuffix(suffix)) or noun
            return template.format(subject)
    if kind.startswith("record"):
        return f"Represents {article(lower)} {lower}."
    return f"Provides the {noun} API."


def declaration_name(text: str, current_type: str | None) -> str | None:
    tm = TYPE_RE.search(text)
    if tm:
        return tm.group("name")
    if " operator " in text:
        return "operator " + text.split(" operator ", 1)[1].split("(", 1)[0].strip()
    if "(" in text:
        prefix = text.split("(", 1)[0]
        tokens = IDENT_RE.findall(prefix)
        if tokens:
            return tokens[-1]
    head = text.split("=>", 1)[0].split("=", 1)[0].split("{", 1)[0].rstrip(" ;")
    tokens = IDENT_RE.findall(head)
    if tokens:
        candidate = tokens[-1]
        if candidate not in {"get", "set", "init", "add", "remove", "where"}:
            return candidate
    return None


def member_summary(type_name: str, member: str, declaration: str) -> str:
    words = humanize(member)
    lower = words.lower()
    if member == type_name:
        return f"Initializes a new instance of <see cref=\"{type_name}\"/>."
    if member.startswith("TryParse"):
        what = humanize(member.removeprefix("TryParse")) or humanize(type_name)
        return f"Attempts to parse the supplied data as {what}."
    if member.startswith("Parse"):
        what = humanize(member.removeprefix("Parse")) or humanize(type_name)
        return f"Parses the supplied data as {what}."
    if member.startswith("TryRead"):
        what = humanize(member.removeprefix("TryRead")) or "value"
        return f"Attempts to read the {what.lower()} from the supplied data."
    if member.startswith("Read"):
        what = humanize(member.removeprefix("Read")) or "data"
        return f"Reads the {what.lower()} from the supplied input."
    if member.startswith("Write"):
        what = humanize(member.removeprefix("Write")) or "data"
        return f"Writes the {what.lower()} to the supplied output."
    if member.startswith("Decode") or member in {"Decompress", "Extract"}:
        return "Decodes or extracts the supplied encoded data."
    if member.startswith("Encode") or member in {"Compress", "Create"}:
        return "Encodes or creates data using the supplied input and options."
    if member == "List":
        return "Lists the entries contained in the supplied input."
    if member in {"Add", "AddOrReplace", "Append"}:
        return "Adds the supplied data to the target container."
    if member in {"Remove", "Delete"}:
        return "Removes the specified data from the target container."
    if member == "Defragment":
        return "Rewrites the target to reduce fragmentation while preserving its logical contents."
    if member.startswith("Enumerate"):
        return f"Enumerates the {humanize(member.removeprefix('Enumerate')).lower() or 'available values'}."
    if member == "GetEnumerator":
        return "Returns an enumerator for the available values."
    if member == "Dispose":
        return "Releases resources held by this instance."
    if member == "Reset":
        return "Resets this instance to its initial state."
    if member == "Clone":
        return "Creates an independent copy of this instance."
    if member.startswith("Get"):
        what = humanize(member.removeprefix("Get")) or "value"
        return f"Gets the {what.lower()}."
    if member.startswith("Set"):
        what = humanize(member.removeprefix("Set")) or "value"
        return f"Sets the {what.lower()}."
    if member.startswith("Is") or member.startswith("Has") or member.startswith("Can") or member.startswith("Supports"):
        return f"Determines whether {lower}."
    if member.startswith("Compute") or member.startswith("Calculate"):
        what = humanize(re.sub(r"^(Compute|Calculate)", "", member)) or "result"
        return f"Computes the {what.lower()} for the supplied data."
    if member.startswith("Build"):
        what = humanize(member.removeprefix("Build")) or "result"
        return f"Builds the {what.lower()} from the supplied values."
    if member.startswith("Convert"):
        what = humanize(member.removeprefix("Convert")) or "value"
        return f"Converts the supplied data to {what}."
    if member.startswith("Validate") or member.startswith("Verify"):
        return "Validates the supplied data and reports whether it is well-formed."
    if " event " in f" {declaration} ":
        return f"Occurs when {lower}."
    if any(token in declaration for token in (" get;", " set;", " init;", "=>")):
        if declaration.lstrip().startswith("public bool ") or declaration.lstrip().startswith("protected bool "):
            return f"Gets a value indicating whether {lower}."
        writable = " set;" in declaration or " init;" in declaration
        return f"Gets{' or sets' if writable else ''} the {lower}."
    if " const " in f" {declaration} " or " readonly " in f" {declaration} ":
        return f"Provides the {lower} value."
    if member.startswith("operator "):
        return f"Implements the {member.removeprefix('operator ')} operator."
    return f"Performs the {lower} operation for <see cref=\"{type_name}\"/>."


def doc_block(lines: list[str], anchor: int) -> tuple[int, int] | None:
    j = anchor - 1
    if j < 0 or not DOC_RE.match(lines[j]):
        return None
    end = j + 1
    while j >= 0 and DOC_RE.match(lines[j]):
        j -= 1
    return j + 1, end


def attribute_anchor(lines: list[str], i: int) -> int:
    anchor = i
    while anchor > 0 and ATTR_RE.match(lines[anchor - 1]):
        anchor -= 1
    return anchor


def add_summary(lines: list[str], anchor: int, indent: str, summary: str) -> int:
    block = doc_block(lines, anchor)
    if block:
        start, end = block
        existing = "\n".join(lines[start:end])
        if "<summary" in existing:
            return 0
        insert = start
    else:
        insert = anchor
    docs = [f"{indent}/// <summary>", f"{indent}/// {summary}", f"{indent}/// </summary>"]
    lines[insert:insert] = docs
    return len(docs)


def annotate(path: Path) -> bool:
    original = path.read_text(encoding="utf-8")
    lines = original.splitlines()
    trailing = original.endswith("\n")
    depth = 0
    stack: list[tuple[str, str, int]] = []
    i = 0
    while i < len(lines):
        line = lines[i]
        stripped = line.strip()
        while stack and depth < stack[-1][2]:
            stack.pop()

        tm = TYPE_RE.search(stripped)
        if tm:
            anchor = attribute_anchor(lines, i)
            indent = line[: len(line) - len(line.lstrip())]
            added = add_summary(lines, anchor, indent, type_summary(tm.group("name"), tm.group("kind")))
            if added:
                i += added
                line = lines[i]
                stripped = line.strip()
            body_depth = depth + max(1, line.count("{") - line.count("}"))
            stack.append((tm.group("name"), tm.group("kind"), body_depth))
        elif stack and ACCESS_RE.search(stripped) and not stripped.startswith("//") and not DOC_RE.match(line):
            current_type, _, _ = stack[-1]
            member = declaration_name(stripped, current_type)
            if member:
                anchor = attribute_anchor(lines, i)
                indent = line[: len(line) - len(line.lstrip())]
                added = add_summary(lines, anchor, indent, member_summary(current_type, member, stripped))
                if added:
                    i += added
                    line = lines[i]
                    stripped = line.strip()

        # Public enum values have no access modifier of their own.
        if stack and stack[-1][1] == "enum" and depth >= stack[-1][2] and stripped and not stripped.startswith(("//", "///", "[", "{", "}")):
            candidate = stripped.split("=", 1)[0].rstrip(", ").strip()
            if re.fullmatch(r"[A-Za-z_][A-Za-z0-9_]*", candidate):
                anchor = attribute_anchor(lines, i)
                indent = line[: len(line) - len(line.lstrip())]
                added = add_summary(lines, anchor, indent, f"Specifies the {humanize(candidate).lower()} value.")
                if added:
                    i += added
                    line = lines[i]
                    stripped = line.strip()

        # Ignore braces inside simple strings/comments only approximately; the source style keeps declarations predictable.
        code = re.sub(r'"(?:\\.|[^"\\])*"', '""', line.split("//", 1)[0])
        depth += code.count("{") - code.count("}")
        i += 1

    updated = "\n".join(lines) + ("\n" if trailing else "")
    if updated != original:
        path.write_text(updated, encoding="utf-8")
        return True
    return False


def main() -> None:
    changed: list[Path] = []
    for root in ROOTS:
        if not root.exists():
            continue
        for path in sorted(root.rglob("*.cs")):
            if any(part in {"bin", "obj"} for part in path.parts):
                continue
            if annotate(path):
                changed.append(path)
    print(f"Annotated {len(changed)} source files")
    for path in changed:
        print(path)


if __name__ == "__main__":
    main()
