#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileFormat.Upx;

/// <summary>
/// Reader for UPX-packed executables. Parses both PE and ELF variants and
/// surfaces the compressed payload regions + UPX packer header so a caller can
/// inspect the metadata, pipe it through <c>upx -d</c>, or feed the compressed
/// stream to a UCL/LZMA decompressor without executing the stub.
/// </summary>
/// <remarks>
/// <para>
/// Detection layers (a tampered binary may have any subset of these wiped):
/// </para>
/// <list type="number">
///   <item>Section names <c>UPX0</c>/<c>UPX1</c>/<c>UPX2</c> — strongest signal but trivial to rename.</item>
///   <item><c>"UPX!"</c> 4-byte packer-header magic — also commonly wiped by anti-detection patches.</item>
///   <item><c>"$Info: This file is packed with the UPX…"</c> tooling banner — almost always stripped.</item>
///   <item>Structural PE fingerprint: small section count, first section with <c>RawSize=0</c> + <c>VirtualSize&gt;0</c>, entry point inside the last section, tiny IAT, RWX section flags.</item>
///   <item>Header-trailer scan: a 32-byte block matching the PackHeader layout (plausible <c>format</c>, <c>method</c>, <c>u_len</c>, <c>c_len</c>) anywhere in the file even with the magic bytes wiped.</item>
/// </list>
/// <para>
/// Detection returns a <see cref="DetectionConfidence"/> level so callers can
/// distinguish a definite UPX hit from a structural-only suspicion.
/// </para>
/// </remarks>
public sealed class UpxReader {

  /// <summary>4-byte magic "UPX!" (0x55 0x50 0x58 0x21) present in untampered packer headers.</summary>
  public static ReadOnlySpan<byte> PackerMagic => [0x55, 0x50, 0x58, 0x21];

  /// <summary>PE-style 8-byte section names used by canonical UPX output.</summary>
  public static readonly string[] SectionNames = ["UPX0", "UPX1", "UPX2"];

  public enum ContainerKind { Pe, Elf, MachO, Unknown }

  /// <summary>Aggregated detection confidence after combining all heuristic layers.</summary>
  public enum DetectionConfidence {
    /// <summary>No UPX evidence found.</summary>
    None,
    /// <summary>Structural shape suggests UPX (renamed sections, packed-binary fingerprint) but no header found.</summary>
    Heuristic,
    /// <summary>Found the PackHeader struct (with or without intact "UPX!" magic) — high confidence.</summary>
    Confirmed,
  }

  public sealed record PeSection(
    string Name, uint VirtualSize, uint VirtualAddress, uint RawSize, uint RawOffset, uint Characteristics);

  public sealed record PackerHeader(
    int Offset,
    bool MagicIntact,
    byte Version,
    byte Format,
    byte Method,
    byte Level,
    uint UncompressedSize,
    uint CompressedSize,
    uint UncompressedAdler32,
    uint CompressedAdler32,
    uint FilterId,
    uint FilterCtoSize,
    byte Filter,
    byte FilterCto,
    byte NumCto,
    byte ChecksumType
  );

  /// <summary>Cumulative evidence collected by individual fingerprint checks.</summary>
  public sealed record DetectionEvidence(
    bool SectionNamesMatch,
    bool ToolingBannerPresent,
    bool PackHeaderFound,
    bool PackHeaderMagicIntact,
    bool StructuralFingerprintMatch,
    int FingerprintScore,
    string FingerprintReasoning
  );

  public sealed record Info(
    ContainerKind Kind,
    DetectionConfidence Confidence,
    DetectionEvidence Evidence,
    IReadOnlyList<PeSection> PeSections,
    PackerHeader? Header,
    string? ToolingString,
    uint? PeEntryPointRva,
    int PeEntryPointSectionIndex,
    byte[] Image
  ) {
    /// <summary>Convenience property for callers that just want a yes/no answer.</summary>
    public bool IsUpxPacked => this.Confidence != DetectionConfidence.None;
  }

  public static Info Read(ReadOnlySpan<byte> data) {
    var image = data.ToArray();
    var kind = DetectContainer(data);

    var peSections = kind == ContainerKind.Pe ? ParsePeSections(data) : [];
    var entryRva = kind == ContainerKind.Pe ? ParsePeEntryPoint(data) : null;
    var entryIdx = entryRva is { } rva ? FindSectionContainingRva(peSections, rva) : -1;

    var sectionNamesMatch = peSections.Any(s => s.Name is "UPX0" or "UPX1" or "UPX2");
    var (_, header, headerMagicIntact) = ScanPackerHeader(data);
    var toolingString = ScanToolingString(data);

    var (structural, score, reasoning) = ScoreStructuralFingerprint(peSections, entryIdx, data);

    var evidence = new DetectionEvidence(
      SectionNamesMatch: sectionNamesMatch,
      ToolingBannerPresent: toolingString != null,
      PackHeaderFound: header != null,
      PackHeaderMagicIntact: headerMagicIntact,
      StructuralFingerprintMatch: structural,
      FingerprintScore: score,
      FingerprintReasoning: reasoning);

    var confidence = ClassifyConfidence(evidence);

    return new Info(
      Kind: kind,
      Confidence: confidence,
      Evidence: evidence,
      PeSections: peSections,
      Header: header,
      ToolingString: toolingString,
      PeEntryPointRva: entryRva,
      PeEntryPointSectionIndex: entryIdx,
      Image: image);
  }

  /// <summary>
  /// Locates the compressed payload bytes inside a UPX-packed image based on the
  /// PackHeader trailer. Returns null when no header is available — callers
  /// without a header have no anchor to identify the start of the compressed
  /// region (UPX places it immediately before the trailer).
  /// </summary>
  /// <remarks>
  /// UPX layout convention: <c>[stub code][compressed payload][PackHeader (32 B)][padding/overlays]</c>.
  /// The compressed region runs from <c>header.Offset - header.CompressedSize</c>
  /// to <c>header.Offset</c>.
  /// </remarks>
  public static byte[]? LocateCompressedPayload(Info info) {
    if (info.Header is not { } h) return null;
    var start = h.Offset - (int)h.CompressedSize;
    if (start < 0 || start + h.CompressedSize > info.Image.Length) return null;
    var slice = new byte[h.CompressedSize];
    Array.Copy(info.Image, start, slice, 0, (int)h.CompressedSize);
    return slice;
  }

  // ── Container detection ────────────────────────────────────────────────

  private static ContainerKind DetectContainer(ReadOnlySpan<byte> data) {
    if (data.Length < 4) return ContainerKind.Unknown;
    if (data[0] == 'M' && data[1] == 'Z') return ContainerKind.Pe;
    if (data[0] == 0x7F && data[1] == 'E' && data[2] == 'L' && data[3] == 'F') return ContainerKind.Elf;
    var m = BinaryPrimitives.ReadUInt32LittleEndian(data);
    if (m is 0xFEEDFACEu or 0xFEEDFACFu or 0xCEFAEDFEu or 0xCFFAEDFEu or 0xCAFEBABEu or 0xBEBAFECAu)
      return ContainerKind.MachO;
    return ContainerKind.Unknown;
  }

  // ── PE section table walk ──────────────────────────────────────────────

  private static IReadOnlyList<PeSection> ParsePeSections(ReadOnlySpan<byte> data) {
    if (data.Length < 0x40) return [];
    var eLfanew = BinaryPrimitives.ReadUInt32LittleEndian(data[0x3C..]);
    if (eLfanew == 0 || eLfanew + 24 > (uint)data.Length) return [];
    if (data[(int)eLfanew] != 'P' || data[(int)eLfanew + 1] != 'E') return [];

    var coff = data[((int)eLfanew + 4)..];
    var numSections = BinaryPrimitives.ReadUInt16LittleEndian(coff[2..]);
    var optHdrSize = BinaryPrimitives.ReadUInt16LittleEndian(coff[16..]);

    var sectionTableOffset = (int)eLfanew + 24 + optHdrSize;
    if (sectionTableOffset + numSections * 40 > data.Length) return [];

    var sections = new List<PeSection>(numSections);
    for (var i = 0; i < numSections; i++) {
      var off = sectionTableOffset + i * 40;
      var nameSpan = data.Slice(off, 8);
      var terminator = nameSpan.IndexOf((byte)0);
      if (terminator < 0) terminator = 8;
      var name = Encoding.ASCII.GetString(nameSpan[..terminator]);

      var vsize = BinaryPrimitives.ReadUInt32LittleEndian(data[(off + 8)..]);
      var vaddr = BinaryPrimitives.ReadUInt32LittleEndian(data[(off + 12)..]);
      var rsize = BinaryPrimitives.ReadUInt32LittleEndian(data[(off + 16)..]);
      var roff = BinaryPrimitives.ReadUInt32LittleEndian(data[(off + 20)..]);
      var chars = BinaryPrimitives.ReadUInt32LittleEndian(data[(off + 36)..]);

      sections.Add(new PeSection(name, vsize, vaddr, rsize, roff, chars));
    }
    return sections;
  }

  private static uint? ParsePeEntryPoint(ReadOnlySpan<byte> data) {
    if (data.Length < 0x40) return null;
    var eLfanew = BinaryPrimitives.ReadUInt32LittleEndian(data[0x3C..]);
    if (eLfanew == 0 || eLfanew + 40 > (uint)data.Length) return null;
    if (data[(int)eLfanew] != 'P' || data[(int)eLfanew + 1] != 'E') return null;
    // OptionalHeader.AddressOfEntryPoint is at offset 16 inside the OptionalHeader,
    // which immediately follows the COFF header at eLfanew + 24.
    var optBase = (int)eLfanew + 24;
    if (optBase + 20 > data.Length) return null;
    return BinaryPrimitives.ReadUInt32LittleEndian(data[(optBase + 16)..]);
  }

  private static int FindSectionContainingRva(IReadOnlyList<PeSection> sections, uint rva) {
    for (var i = 0; i < sections.Count; i++) {
      var s = sections[i];
      if (rva >= s.VirtualAddress && rva < s.VirtualAddress + Math.Max(s.VirtualSize, s.RawSize))
        return i;
    }
    return -1;
  }

  // ── Packer-header scan ─────────────────────────────────────────────────

  /// <summary>
  /// Scans the file for a plausible <c>PackHeader</c> struct. First tries the
  /// canonical "UPX!" magic; if that fails, sweeps every 4-byte boundary and
  /// validates the trailing 28 bytes against the structural constraints (sane
  /// format/method enum values, c_len ≤ u_len ≤ 256 MB, both non-zero,
  /// extra-field byte ranges). Catches binaries where the magic was wiped.
  /// </summary>
  private static (int Offset, PackerHeader? Header, bool MagicIntact) ScanPackerHeader(ReadOnlySpan<byte> data) {
    var (off, hdr) = ScanForExactMagic(data);
    if (hdr != null) return (off, hdr, MagicIntact: true);

    // Tampered binaries: brute-force search for a header-shaped 32-byte block.
    // We require sane field values to avoid false positives.
    for (var i = 0; i + 32 <= data.Length; i += 4) {
      if (TryParsePackHeader(data, i, requireMagic: false) is { } maybe)
        return (i, maybe, MagicIntact: false);
    }
    return (-1, null, MagicIntact: false);
  }

  private static (int Offset, PackerHeader? Header) ScanForExactMagic(ReadOnlySpan<byte> data) {
    var pos = data.IndexOf(PackerMagic);
    while (pos >= 0) {
      if (TryParsePackHeader(data, pos, requireMagic: true) is { } h) return (pos, h);
      var next = data[(pos + 4)..].IndexOf(PackerMagic);
      if (next < 0) break;
      pos += 4 + next;
    }
    return (-1, null);
  }

  private static PackerHeader? TryParsePackHeader(ReadOnlySpan<byte> data, int pos, bool requireMagic) {
    if (pos < 0 || pos + 32 > data.Length) return null;
    var h = data.Slice(pos, 32);

    var magicIntact = h[0] == 'U' && h[1] == 'P' && h[2] == 'X' && h[3] == '!';
    if (requireMagic && !magicIntact) return null;

    var version = h[4];
    var format = h[5];
    var method = h[6];
    var level = h[7];
    var uLen = BinaryPrimitives.ReadUInt32LittleEndian(h[8..]);
    var cLen = BinaryPrimitives.ReadUInt32LittleEndian(h[12..]);
    var uAdler = BinaryPrimitives.ReadUInt32LittleEndian(h[16..]);
    var cAdler = BinaryPrimitives.ReadUInt32LittleEndian(h[20..]);
    var filterId = BinaryPrimitives.ReadUInt32LittleEndian(h[24..]);
    var filterCtoSize = BinaryPrimitives.ReadUInt32LittleEndian(h[28..]);

    // Sanity gate — required regardless of magic intact flag.
    if (uLen == 0 || cLen == 0) return null;
    if (cLen > uLen) return null;
    if (uLen > 0x10000000) return null;        // 256 MB cap
    if (level == 0 || level > 13) return null; // UPX levels are 1..13
    if (version == 0 || version > 64) return null;
    if (!IsKnownFormat(format)) return null;
    if (!IsKnownMethod(method)) return null;
    if (filterId > 0x100) return null;          // filter ids are small

    return new PackerHeader(
      Offset: pos,
      MagicIntact: magicIntact,
      Version: version,
      Format: format,
      Method: method,
      Level: level,
      UncompressedSize: uLen,
      CompressedSize: cLen,
      UncompressedAdler32: uAdler,
      CompressedAdler32: cAdler,
      FilterId: filterId,
      FilterCtoSize: filterCtoSize,
      Filter: 0, FilterCto: 0, NumCto: 0, ChecksumType: 0);
  }

  private static bool IsKnownMethod(byte method) =>
    method is >= 2 and <= 10 || method == 14 || method == 15;

  private static bool IsKnownFormat(byte format) =>
    format is >= 1 and <= 38;

  private static string? ScanToolingString(ReadOnlySpan<byte> data) {
    ReadOnlySpan<byte> tooling = "$Info: This file is packed with the UPX"u8;
    var pos = data.IndexOf(tooling);
    if (pos < 0) return null;
    var end = data[(pos + 1)..].IndexOf((byte)'$');
    var span = end < 0
      ? data[pos..Math.Min(pos + 120, data.Length)]
      : data.Slice(pos, end + 2);
    return Encoding.ASCII.GetString(span);
  }

  // ── Structural fingerprinting ──────────────────────────────────────────

  /// <summary>
  /// Scores how closely the PE section layout resembles a canonical UPX-packed
  /// binary even when section names have been renamed. The BSS-style first
  /// section (RawSize=0 + VirtualSize&gt;0) is treated as a hard prerequisite —
  /// every UPX-packed binary has it because that's where the decompressed code
  /// gets written at runtime. Without it, no amount of other evidence
  /// constitutes a structural match.
  /// </summary>
  private static (bool Match, int Score, string Reasoning)
      ScoreStructuralFingerprint(IReadOnlyList<PeSection> sections, int entryIdx, ReadOnlySpan<byte> data) {
    if (sections.Count == 0) return (false, 0, "no PE sections");

    // Hard prerequisite: BSS-style first section. UPX0 always has VirtualSize > 0
    // and RawSize == 0; this is the runtime decompression target. A normal PE
    // never has this shape (the .text/.data sections always have RawSize > 0).
    var firstIsBss = sections[0].RawSize == 0 && sections[0].VirtualSize > 0;
    if (!firstIsBss) return (false, 0, "section[0] is not BSS-style (RawSize>0); not UPX-shaped");

    var score = 30;
    var notes = new List<string> { "section[0] vsize>0 rsize=0" };

    if (sections.Count is >= 2 and <= 4) {
      score += 10;
      notes.Add($"section_count={sections.Count}");
    }

    // RWX section flags — UPX0/UPX1 are typically RWX so the unpacked code can run in place.
    // IMAGE_SCN_MEM_EXECUTE = 0x20000000, MEM_READ = 0x40000000, MEM_WRITE = 0x80000000.
    const uint Rwx = 0x20000000u | 0x40000000u | 0x80000000u;
    var rwxCount = sections.Count(s => (s.Characteristics & Rwx) == Rwx);
    if (rwxCount >= 1) {
      score += 15;
      notes.Add($"rwx_sections={rwxCount}");
    }

    // Entry point in the LAST section (where the UPX stub lives).
    if (entryIdx >= 0 && entryIdx == sections.Count - 1) {
      score += 15;
      notes.Add("entry_in_last_section");
    } else if (entryIdx >= 0 && entryIdx == sections.Count - 2 && sections.Count >= 3) {
      score += 10;
      notes.Add("entry_in_penultimate_section");
    }

    // Largest section payload entropy — high entropy → compressed data.
    var biggest = sections.OrderByDescending(s => s.RawSize).FirstOrDefault();
    if (biggest is { RawSize: > 0, RawOffset: > 0 } &&
        biggest.RawOffset + biggest.RawSize <= (uint)data.Length) {
      var sample = data.Slice((int)biggest.RawOffset, (int)Math.Min(biggest.RawSize, 0x4000));
      var entropy = ShannonEntropy(sample);
      if (entropy >= 7.5) {
        score += 15;
        notes.Add($"entropy={entropy:F2}");
      }
    }

    var match = score >= 50;
    return (match, score, string.Join(", ", notes));
  }

  /// <summary>Shannon entropy in bits/byte for the supplied sample (max 8).</summary>
  private static double ShannonEntropy(ReadOnlySpan<byte> sample) {
    if (sample.Length == 0) return 0;
    Span<int> counts = stackalloc int[256];
    foreach (var b in sample) counts[b]++;
    var total = (double)sample.Length;
    var h = 0.0;
    foreach (var c in counts) {
      if (c == 0) continue;
      var p = c / total;
      h -= p * Math.Log2(p);
    }
    return h;
  }

  private static DetectionConfidence ClassifyConfidence(DetectionEvidence e) {
    if (e.PackHeaderFound) return DetectionConfidence.Confirmed;
    if (e.SectionNamesMatch || e.ToolingBannerPresent) return DetectionConfidence.Confirmed;
    if (e.StructuralFingerprintMatch) return DetectionConfidence.Heuristic;
    return DetectionConfidence.None;
  }

  // ── Method/format name tables (unchanged from prior version) ───────────

  /// <summary>
  /// Decodes the <c>method</c> byte from a UPX packer header into a human-readable
  /// compression algorithm name. Unrecognised values are returned as
  /// <c>method_&lt;n&gt;</c> so callers can still surface them in metadata.
  /// </summary>
  public static string MethodName(byte method) => method switch {
    2 => "NRV2B_LE32",
    3 => "NRV2D_LE32",
    4 => "NRV2B_LE16",
    5 => "NRV2D_LE16",
    6 => "NRV2B_8",
    7 => "NRV2D_8",
    8 => "NRV2E_LE32",
    9 => "NRV2E_LE16",
    10 => "NRV2E_8",
    14 => "LZMA",
    15 => "DEFLATE",
    _ => $"method_{method}",
  };

  /// <summary>Decodes the <c>format</c> byte from a UPX header to a human-readable name.</summary>
  public static string FormatName(byte format) => format switch {
    1 => "UPX_F_DOS_COM",
    2 => "UPX_F_DOS_SYS",
    3 => "UPX_F_DOS_EXE",
    4 => "UPX_F_DJGPP2_COFF",
    5 => "UPX_F_WATCOM_LE",
    6 => "UPX_F_VXD_LE",
    7 => "UPX_F_DOS_EXEH",
    8 => "UPX_F_TMT_ADAM",
    9 => "UPX_F_WIN32_PE",
    10 => "UPX_F_LINUX_i386",
    11 => "UPX_F_WIN16_NE",
    12 => "UPX_F_LINUX_ELF_i386",
    13 => "UPX_F_LINUX_SEP_i386",
    14 => "UPX_F_LINUX_SH_i386",
    15 => "UPX_F_VMLINUZ_i386",
    16 => "UPX_F_BVMLINUZ_i386",
    17 => "UPX_F_ELKS_8086",
    18 => "UPX_F_PS1_EXE",
    19 => "UPX_F_VMLINUX_i386",
    20 => "UPX_F_LINUX_ELFI_i386",
    21 => "UPX_F_WINCE_ARM_PE",
    22 => "UPX_F_LINUX_ELF64_AMD",
    23 => "UPX_F_LINUX_ELF32_ARMEL",
    24 => "UPX_F_MACH_PPC32",
    25 => "UPX_F_LINUX_ELFPPC32",
    26 => "UPX_F_LINUX_ELF32_ARMEB",
    27 => "UPX_F_MACH_i386",
    28 => "UPX_F_DYLIB_i386",
    29 => "UPX_F_MACH_AMD64",
    30 => "UPX_F_DYLIB_AMD64",
    31 => "UPX_F_WIN64_PEP",
    32 => "UPX_F_MACH_ARMEL",
    33 => "UPX_F_LINUX_ELF32_MIPSEL",
    34 => "UPX_F_LINUX_ELF32_MIPSEB",
    35 => "UPX_F_LINUX_ELFPPC64LE",
    36 => "UPX_F_MACH_PPC64",
    37 => "UPX_F_MACH_ARM64",
    38 => "UPX_F_LINUX_ELF64_ARM64",
    _ => $"format_{format}",
  };
}
