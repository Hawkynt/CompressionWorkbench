#!/usr/bin/env python3
from pathlib import Path
import re


def replace_once(path: Path, old: str, new: str) -> None:
    text = path.read_text(encoding="utf-8")
    if old not in text:
        if new in text:
            return
        raise SystemExit(f"Expected source shape not found in {path}")
    path.write_text(text.replace(old, new, 1), encoding="utf-8")


def materialize_range_properties(path: Path, type_name: str, noun: str) -> None:
    text = path.read_text(encoding="utf-8")
    if "public int MinimumBits { get; init; } = MinimumBits;" in text:
        return

    marker = f"public readonly record struct {type_name}(int MinimumBits, int MaximumBits, int StepBits = 1) : IEnumerable<int> {{\n"
    insertion = marker + f'''  /// <summary>\n  /// Gets the smallest supported {noun} output size, in bits.\n  /// </summary>\n  public int MinimumBits {{ get; init; }} = MinimumBits;\n\n  /// <summary>\n  /// Gets the largest supported {noun} output size, in bits.\n  /// </summary>\n  public int MaximumBits {{ get; init; }} = MaximumBits;\n\n  /// <summary>\n  /// Gets the increment, in bits, between supported sizes in the range.\n  /// </summary>\n  public int StepBits {{ get; init; }} = StepBits;\n\n'''
    replace_once(path, marker, insertion)


def materialize_crc_parameters(path: Path) -> None:
    crc = '''public readonly record struct CrcParameters(\n  int Width,\n  ulong Polynomial,\n  ulong InitialValue,\n  bool ReflectInput,\n  bool ReflectOutput,\n  ulong FinalXor\n);'''
    crc_documented = '''public readonly record struct CrcParameters(\n  int Width,\n  ulong Polynomial,\n  ulong InitialValue,\n  bool ReflectInput,\n  bool ReflectOutput,\n  ulong FinalXor\n) {\n  /// <summary>Gets the CRC width, in bits.</summary>\n  public int Width { get; init; } = Width;\n\n  /// <summary>Gets the normal-form CRC polynomial.</summary>\n  public ulong Polynomial { get; init; } = Polynomial;\n\n  /// <summary>Gets the initial CRC register value.</summary>\n  public ulong InitialValue { get; init; } = InitialValue;\n\n  /// <summary>Gets whether each input byte is reflected before processing.</summary>\n  public bool ReflectInput { get; init; } = ReflectInput;\n\n  /// <summary>Gets whether the CRC register is reflected before the final XOR.</summary>\n  public bool ReflectOutput { get; init; } = ReflectOutput;\n\n  /// <summary>Gets the value XORed with the CRC register to produce the final checksum.</summary>\n  public ulong FinalXor { get; init; } = FinalXor;\n}'''
    replace_once(path, crc, crc_documented)

    crc128 = '''public readonly record struct Crc128Parameters(\n  UInt128 Polynomial,\n  UInt128 InitialValue,\n  bool ReflectInput,\n  bool ReflectOutput,\n  UInt128 FinalXor\n);'''
    crc128_documented = '''public readonly record struct Crc128Parameters(\n  UInt128 Polynomial,\n  UInt128 InitialValue,\n  bool ReflectInput,\n  bool ReflectOutput,\n  UInt128 FinalXor\n) {\n  /// <summary>Gets the normal-form 128-bit CRC polynomial.</summary>\n  public UInt128 Polynomial { get; init; } = Polynomial;\n\n  /// <summary>Gets the initial 128-bit CRC register value.</summary>\n  public UInt128 InitialValue { get; init; } = InitialValue;\n\n  /// <summary>Gets whether each input byte is reflected before processing.</summary>\n  public bool ReflectInput { get; init; } = ReflectInput;\n\n  /// <summary>Gets whether the CRC register is reflected before the final XOR.</summary>\n  public bool ReflectOutput { get; init; } = ReflectOutput;\n\n  /// <summary>Gets the value XORed with the CRC register to produce the final checksum.</summary>\n  public UInt128 FinalXor { get; init; } = FinalXor;\n}'''
    replace_once(path, crc128, crc128_documented)


def expand_hash_wrappers(path: Path) -> None:
    text = path.read_text(encoding="utf-8")
    pattern = re.compile(
        r"public static class (?P<name>(?:Echo|Groestl|Luffa|Ripemd|Shabal)(?P<bits>\d+)) \{ public static byte\[\] Compute\(ReadOnlySpan<byte> data\) => (?P<body>[^;]+); \}"
    )

    def replacement(match: re.Match[str]) -> str:
        name = match.group("name")
        bits = match.group("bits")
        family = re.sub(r"\d+$", "", name)
        display_family = "RIPEMD" if family == "Ripemd" else family
        return f'''public static class {name} {{\n  /// <summary>\n  /// Computes the {display_family}-{bits} hash of the supplied data.\n  /// </summary>\n  public static byte[] Compute(ReadOnlySpan<byte> data) => {match.group("body")};\n}}'''

    updated, count = pattern.subn(replacement, text)
    if count == 0:
        if re.search(r"/// Computes the (?:Echo|Groestl|Luffa|RIPEMD|Shabal)-\d+ hash", text):
            return
        raise SystemExit(f"No compact hash wrappers found in {path}")
    path.write_text(updated, encoding="utf-8")


materialize_range_properties(Path("Hawkynt.Algorithms.Hashing/HashSizeRange.cs"), "HashSizeRange", "hash")
materialize_range_properties(Path("Hawkynt.Algorithms.Checksums/ChecksumSizeRange.cs"), "ChecksumSizeRange", "checksum")
materialize_crc_parameters(Path("Hawkynt.Algorithms.Checksums/Algorithms/Checksums.cs"))
for name in ("Echo", "Groestl", "Luffa", "Ripemd", "Shabal"):
    expand_hash_wrappers(Path(f"Hawkynt.Algorithms.Hashing/Algorithms/{name}.cs"))
