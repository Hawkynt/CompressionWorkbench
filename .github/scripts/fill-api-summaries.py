#!/usr/bin/env python3
from __future__ import annotations

import re
from pathlib import Path

TARGETS = {
    Path("Hawkynt.Algorithms.Checksums"): "checksum",
    Path("Hawkynt.Algorithms.Hashing"): "hash",
}

TYPE_RE = re.compile(r"\b(public|protected)\s+(?:(?:static|sealed|abstract|readonly|ref|partial)\s+)*(class|struct|record(?:\s+struct|\s+class)?|interface|enum)\s+([A-Za-z_][A-Za-z0-9_]*)")
ANY_TYPE_RE = re.compile(r"\b(?:(?:public|protected|internal|private)\s+)?(?:(?:static|sealed|abstract|readonly|ref|partial)\s+)*(class|struct|record(?:\s+struct|\s+class)?|interface|enum)\s+([A-Za-z_][A-Za-z0-9_]*)")
EXTENSION_LINE_RE = re.compile(
    r"^(?P<indent>\s*)public static class (?P<class>[A-Za-z_][A-Za-z0-9_]*) \{ extension\((?P<receiver>[^)]+)\) \{ public static (?P<type>.+?) (?P<property>Supported(?:Hash|Checksum)Sizes) => (?P<expr>.+); \} \}\s*$"
)
DOC_LINE_RE = re.compile(r"^\s*///")
ATTRIBUTE_LINE_RE = re.compile(r"^\s*\[")
DECLARATION_RE = re.compile(r"\b(public|protected)\b")
IDENTIFIER_RE = re.compile(r"[A-Za-z_][A-Za-z0-9_]*")


def humanize(name: str) -> str:
    name = name.replace("_", " ")
    name = re.sub(r"(?<=[a-z0-9])(?=[A-Z])", " ", name)
    name = re.sub(r"(?<=[A-Z])(?=[A-Z][a-z])", " ", name)
    name = re.sub(r"(?<=[A-Za-z])(?=[0-9])", "-", name)
    name = re.sub(r"(?<=[0-9])(?=[A-Za-z])", " ", name)
    return re.sub(r"\s+", " ", name).strip()


def algorithm_name(type_name: str) -> str:
    value = humanize(type_name)
    replacements = {
        "Md 2": "MD2", "Md 4": "MD4", "Md 5": "MD5",
        "Sha 1": "SHA-1", "Sha 3": "SHA-3", "Sha 256": "SHA-256",
        "Sha 512": "SHA-512", "Sm 3": "SM3", "Crc": "CRC",
        "Crc 16": "CRC-16", "Crc 32": "CRC-32", "Crc 64": "CRC-64",
        "Xx Hash": "xxHash", "Xx Hash-3": "xxHash3", "Blake 2": "BLAKE2",
        "Blake 3": "BLAKE3", "Ripemd": "RIPEMD", "Gost": "GOST",
        "Lsh": "LSH", "Fnv": "FNV", "Iban": "IBAN", "Imei": "IMEI",
        "Iccid": "ICCID", "Isbn": "ISBN", "Isin": "ISIN", "Issn": "ISSN",
        "Nmea": "NMEA", "Npi": "NPI", "Gtin": "GTIN", "Vin": "VIN",
    }
    for old, new in replacements.items():
        if value.startswith(old):
            value = new + value[len(old):]
            break
    return value


def summary_lines(indent: str, text: str) -> list[str]:
    return [f"{indent}/// <summary>", f"{indent}/// {text}", f"{indent}/// </summary>"]


def type_summary(type_name: str, kind: str, domain: str, parent: str | None) -> str:
    if type_name == "Enumerator" and parent:
        return f"Enumerates the bit sizes represented by <see cref=\"{parent}\"/>."
    if type_name.endswith("HashSizeExtensions"):
        receiver = type_name.removesuffix("HashSizeExtensions")
        return f"Provides supported hash-output size metadata for <see cref=\"{receiver}\"/>."
    if type_name.endswith("ChecksumSizeExtensions"):
        receiver = type_name.removesuffix("ChecksumSizeExtensions")
        return f"Provides supported checksum-output size metadata for <see cref=\"{receiver}\"/>."
    if type_name.endswith("RangeExtensions"):
        receiver = type_name.removesuffix("Extensions")
        return f"Provides helpers for collections of <see cref=\"{receiver}\"/> values."
    if kind == "enum":
        return f"Specifies the available {humanize(type_name).lower()} values."
    if kind == "interface":
        return f"Defines the contract implemented by {humanize(type_name).lower()} implementations."
    algo = algorithm_name(type_name)
    if domain == "hash":
        return f"Provides the {algo} hash implementation."
    return f"Provides the {algo} checksum or check-digit implementation."


def member_name_from_declaration(stripped: str, current_type: str | None) -> str | None:
    # Type declaration itself.
    match = TYPE_RE.search(stripped)
    if match:
        return match.group(3)

    # Operators are public API too, though none are currently expected in these packages.
    if " operator " in stripped:
        tail = stripped.split(" operator ", 1)[1]
        return "operator " + tail.split("(", 1)[0].strip()

    if "(" in stripped:
        prefix = stripped.split("(", 1)[0].strip()
        tokens = IDENTIFIER_RE.findall(prefix)
        if tokens:
            return tokens[-1]

    # Property/field/event declaration. Ignore accessor-only lines.
    head = stripped.split("=>", 1)[0].split("=", 1)[0].split("{", 1)[0].rstrip(" ;")
    tokens = IDENTIFIER_RE.findall(head)
    if tokens:
        candidate = tokens[-1]
        if candidate not in {"get", "set", "init", "add", "remove"}:
            return candidate
    return None


def bits_from_compute_name(member: str) -> int | None:
    match = re.fullmatch(r"Compute(\d+)(?:Bytes)?", member)
    return int(match.group(1)) if match else None


def member_summary(type_name: str, member: str, declaration: str, domain: str, parent: str | None) -> str:
    algo = algorithm_name(type_name)

    if member == type_name:
        return f"Initializes a new instance of <see cref=\"{type_name}\"/>."

    if member == "SupportedHashSizes" or member == "get_SupportedHashSizes":
        return "Gets the supported hash-output sizes, in bits."
    if member == "SupportedChecksumSizes" or member == "get_SupportedChecksumSizes":
        return "Gets the supported checksum-output sizes, in bits."

    range_properties = {
        "MinimumBits": "Gets the smallest supported output size, in bits.",
        "MaximumBits": "Gets the largest supported output size, in bits.",
        "StepBits": "Gets the increment, in bits, between supported sizes in the range.",
        "Current": "Gets the current bit size in the enumeration.",
    }
    if member in range_properties:
        return range_properties[member]

    if member == "Contains":
        return "Determines whether the range contains the specified bit size."
    if member == "Exact":
        return f"Creates a <see cref=\"{type_name}\"/> containing exactly one bit size."
    if member == "GetEnumerator":
        return "Returns an enumerator over the bit sizes represented by the range."
    if member == "MoveNext":
        return "Advances the enumerator to the next supported bit size."
    if member == "Dispose" and type_name == "Enumerator":
        return "Releases resources associated with the enumerator."
    if member == "Reset" and type_name == "Enumerator":
        return "Resets the enumerator to its initial position."
    if member == "Supports":
        return "Determines whether any range in the collection contains the specified bit size."
    if member == "EnumerateSizes":
        return "Enumerates every supported bit size represented by the supplied ranges."

    if domain == "hash":
        bits = bits_from_compute_name(member)
        if bits is not None:
            if member.endswith("Bytes"):
                return f"Computes the {bits}-bit {algo} hash and returns its encoded bytes."
            return f"Computes the {bits}-bit {algo} hash of the supplied data."
        if member == "Compute":
            return f"Computes the {algo} hash of the supplied data."
        if member == "Compute128" and "ValueTuple" in declaration:
            return f"Computes the 128-bit {algo} hash of the supplied data."
        if member == "Clone":
            return f"Creates an independent copy of the current <see cref=\"{type_name}\"/> state."
        if member == "Finish":
            return f"Finalizes the {algo} hash computation."
        if member == "Update":
            return f"Adds the supplied data to the current {algo} hash computation."
        if member == "Reset":
            return f"Resets the {algo} hash state to its initial value."
        if member == "Hash":
            return "Gets the finalized hash bytes."
        if member == "Value":
            return "Gets the hash value for the data processed so far."
        if member.endswith("HashSizeExtensions"):
            return "Provides supported hash-output size metadata."

    else:
        bits = bits_from_compute_name(member)
        if bits is not None:
            return f"Computes the {bits}-bit {algo} checksum of the supplied data."
        if member == "Compute":
            return f"Computes the {algo} checksum of the supplied data."
        if member == "Value":
            return "Gets the current checksum value."
        if member == "Value64":
            return "Gets the current 64-bit checksum value."
        if member == "Reset":
            return "Resets the checksum to its initial state."
        if member == "Update":
            return "Updates the checksum with the supplied data."
        if member.startswith("Generate"):
            target = humanize(member.removeprefix("Generate"))
            if member == "GenerateCheckDigit":
                target = f"{algo} check digit"
            return f"Generates the {target} for the supplied value."
        if member.startswith("Validate"):
            target = humanize(member.removeprefix("Validate")) or algo
            return f"Determines whether the supplied value has a valid {target}."
        if member == "Verify":
            return "Determines whether the supplied data, including its checksum, is valid."
        if member == "OnesComplement16":
            return "Computes a 16-bit one's-complement checksum of the supplied data."
        if member == "TwosComplement16":
            return "Computes a 16-bit two's-complement checksum of the supplied data."
        if member == "TwosComplement8":
            return "Computes an 8-bit two's-complement checksum of the supplied data."
        if member == "BitParity":
            return "Computes the parity of the set bits in the supplied byte."
        if member == "BlockParity":
            return "Computes the longitudinal parity byte for the supplied data."
        if member == "EvenParityBit":
            return "Computes the parity bit required to give the supplied byte even parity."
        if member == "OddParityBit":
            return "Computes the parity bit required to give the supplied byte odd parity."

    # Preset fields and record properties benefit from a noun phrase instead of an "operation" fallback.
    if " const " in f" {declaration} " or " readonly " in f" {declaration} ":
        return f"Provides the {humanize(member)} value used by <see cref=\"{type_name}\"/>."
    if "{" in declaration and any(token in declaration for token in (" get;", " init;", " set;")):
        access = "Gets" if " set;" not in declaration and " init;" not in declaration else "Gets or initializes"
        return f"{access} the {humanize(member).lower()} value."

    return f"Performs the {humanize(member).lower()} operation provided by <see cref=\"{type_name}\"/>."


def enum_value_summary(type_name: str, value: str) -> str:
    if type_name == "ComplementKind":
        return {
            "OnesComplement": "Selects one's-complement arithmetic.",
            "TwosComplement": "Selects two's-complement arithmetic.",
        }.get(value, f"Selects the {humanize(value).lower()} mode.")
    return f"Selects the {humanize(value).lower()} option."


def expand_extension_lines(lines: list[str], domain: str) -> list[str]:
    output: list[str] = []
    for line in lines:
        match = EXTENSION_LINE_RE.match(line)
        if not match:
            output.append(line)
            continue

        indent = match.group("indent")
        class_name = match.group("class")
        receiver = match.group("receiver").strip()
        prop_type = match.group("type").strip()
        prop_name = match.group("property")
        expression = match.group("expr").strip()
        noun = "hash" if domain == "hash" else "checksum"
        output.extend(summary_lines(indent, f"Provides supported {noun}-output size metadata for <see cref=\"{receiver}\"/>."))
        output.append(f"{indent}public static class {class_name} {{")
        output.append(f"{indent}  extension({receiver}) {{")
        output.extend(summary_lines(indent + "    ", f"Gets the supported {noun}-output sizes, in bits."))
        output.append(f"{indent}    public static {prop_type} {prop_name} => {expression};")
        output.append(f"{indent}  }}")
        output.append(f"{indent}}}")
    return output


def previous_doc_block(lines: list[str], anchor: int) -> tuple[int, int] | None:
    j = anchor - 1
    if j < 0 or not DOC_LINE_RE.match(lines[j]):
        return None
    end = j + 1
    while j >= 0 and DOC_LINE_RE.match(lines[j]):
        j -= 1
    return j + 1, end


def attribute_anchor(lines: list[str], declaration_index: int) -> int:
    anchor = declaration_index
    while anchor > 0 and ATTRIBUTE_LINE_RE.match(lines[anchor - 1]):
        anchor -= 1
    return anchor


def add_summary_before(lines: list[str], anchor: int, text: str, indent: str) -> int:
    block = previous_doc_block(lines, anchor)
    new_lines = summary_lines(indent, text)
    if block is None:
        lines[anchor:anchor] = new_lines
        return len(new_lines)

    start, end = block
    body = "\n".join(lines[start:end])
    if "<summary" in body:
        return 0
    lines[start:start] = new_lines
    return len(new_lines)


def annotate_file(path: Path, domain: str) -> bool:
    original = path.read_text(encoding="utf-8")
    had_trailing_newline = original.endswith("\n")
    lines = expand_extension_lines(original.splitlines(), domain)

    depth = 0
    type_stack: list[tuple[str, str, int]] = []  # name, kind, body depth
    i = 0
    while i < len(lines):
        line = lines[i]
        stripped = line.strip()

        # Pop types closed by prior lines.
        while type_stack and depth < type_stack[-1][2]:
            type_stack.pop()

        parent_name = type_stack[-1][0] if type_stack else None
        current_type = parent_name

        type_match = TYPE_RE.search(stripped) if not stripped.startswith("//") else None
        if type_match:
            type_kind = type_match.group(2).replace("record ", "")
            type_name = type_match.group(3)
            anchor = attribute_anchor(lines, i)
            indent = re.match(r"\s*", lines[anchor]).group(0)
            added = add_summary_before(lines, anchor, type_summary(type_name, type_kind, domain, parent_name), indent)
            if added:
                i += added
                line = lines[i]
                stripped = line.strip()
            current_type = type_name

        # Enum values have no explicit public modifier but are public API.
        if type_stack and type_stack[-1][1] == "enum" and stripped and not stripped.startswith(("//", "///", "[", "}")):
            enum_match = re.match(r"(?P<indent>\s*)(?P<name>[A-Za-z_][A-Za-z0-9_]*)\s*(?:=\s*[^,]+)?\s*,?\s*$", line)
            if enum_match and enum_match.group("name") not in {"public", "private", "internal", "protected"}:
                anchor = attribute_anchor(lines, i)
                block = previous_doc_block(lines, anchor)
                if block is None or "<summary" not in "\n".join(lines[block[0]:block[1]]):
                    added = add_summary_before(lines, anchor, enum_value_summary(type_stack[-1][0], enum_match.group("name")), enum_match.group("indent"))
                    if added:
                        i += added
                        line = lines[i]
                        stripped = line.strip()

        # Public/protected member declaration, excluding the type declaration already handled.
        if current_type and not type_match and DECLARATION_RE.search(stripped) and not stripped.startswith(("//", "///")):
            member = member_name_from_declaration(stripped, current_type)
            if member:
                anchor = attribute_anchor(lines, i)
                indent = re.match(r"\s*", lines[anchor]).group(0)
                text = member_summary(current_type, member, stripped, domain, type_stack[-2][0] if len(type_stack) > 1 else None)
                added = add_summary_before(lines, anchor, text, indent)
                if added:
                    i += added
                    line = lines[i]
                    stripped = line.strip()

        # Track type scopes using simple brace depth. The package source style keeps declarations and
        # braces straightforward; braces in strings are rare and do not occur on public declaration lines.
        opens = line.count("{")
        closes = line.count("}")
        if type_match and opens > closes:
            type_stack.append((type_match.group(3), type_match.group(2).replace("record ", ""), depth + 1))
        depth += opens - closes
        while type_stack and depth < type_stack[-1][2]:
            type_stack.pop()

        i += 1

    updated = "\n".join(lines) + ("\n" if had_trailing_newline else "")
    if updated == original:
        return False
    path.write_text(updated, encoding="utf-8")
    return True


def add_explicit_default_constructor(path: Path, type_name: str, domain: str) -> bool:
    text = path.read_text(encoding="utf-8")
    if re.search(rf"\b(public|protected)\s+{re.escape(type_name)}\s*\(", text):
        return False
    declaration = re.search(rf"^(?P<indent>\s*)public\s+(?:(?:sealed|partial)\s+)*class\s+{re.escape(type_name)}[^\n]*\{{\s*$", text, re.MULTILINE)
    if not declaration or "static class" in declaration.group(0):
        return False
    indent = declaration.group("indent") + "  "
    insert = declaration.end()
    summary = "\n" + "\n".join(summary_lines(indent, f"Initializes a new instance of <see cref=\"{type_name}\"/>.")) + f"\n{indent}public {type_name}() {{ }}\n"
    text = text[:insert] + summary + text[insert:]
    path.write_text(text, encoding="utf-8")
    return True


def missing_default_constructors(reference: Path) -> set[str]:
    result: set[str] = set()
    current: str | None = None
    for line in reference.read_text(encoding="utf-8").splitlines():
        match = re.match(r"#### `([^`]+)`", line)
        if match:
            current = match.group(1)
            continue
        if not current or "." in current:
            continue
        if re.match(rf"\| `{re.escape(current)}` \| `{re.escape(current)}\([^`]*\)` \|\s*\|", line):
            result.add(current)
    return result


def locate_type_file(root: Path, type_name: str) -> Path | None:
    pattern = re.compile(rf"\b(class|struct|record(?:\s+struct|\s+class)?|interface|enum)\s+{re.escape(type_name)}\b")
    for path in root.rglob("*.cs"):
        if any(part in {"bin", "obj"} for part in path.parts):
            continue
        if pattern.search(path.read_text(encoding="utf-8")):
            return path
    return None


def main() -> None:
    changed = 0
    for root, domain in TARGETS.items():
        for path in sorted(root.rglob("*.cs")):
            if any(part in {"bin", "obj"} for part in path.parts):
                continue
            if annotate_file(path, domain):
                changed += 1

        reference = root / "REFERENCE.md"
        if reference.exists():
            for type_name in missing_default_constructors(reference):
                path = locate_type_file(root, type_name)
                if path and add_explicit_default_constructor(path, type_name, domain):
                    changed += 1

    print(f"Annotated {changed} source files.")


if __name__ == "__main__":
    main()
