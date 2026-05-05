#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.ZxSnapshot;

/// <summary>
/// ZX Spectrum snapshot and tape formats: <c>.sna</c>, <c>.z80</c>, <c>.tap</c>, <c>.tzx</c>.
/// Detects TZX by magic, the rest by size / extension heuristics. Read-only;
/// compressed Z80 pages are surfaced verbatim, not decompressed.
/// </summary>
public sealed class ZxSnapshotFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  public string Id => "ZxSnapshot";
  public string DisplayName => "ZX Spectrum Snapshot / Tape";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".z80";
  public IReadOnlyList<string> Extensions => [".sna", ".z80", ".tap", ".tzx"];
  public IReadOnlyList<string> CompoundExtensions => [];

  // Only TZX has a reliable magic. TZX: "ZXTape!\x1A"
  public static readonly byte[] TzxMagic = [0x5A, 0x58, 0x54, 0x61, 0x70, 0x65, 0x21, 0x1A];

  public IReadOnlyList<MagicSignature> MagicSignatures =>
    [new(TzxMagic, Offset: 0, Confidence: 0.98)];

  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "ZX Spectrum snapshot (.sna/.z80) and tape (.tap/.tzx) formats";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) =>
    BuildEntries(stream).Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Data.Length, e.Data.Length, "Stored", false, false, null)).ToList();

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    foreach (var e in BuildEntries(stream)) {
      if (files != null && files.Length > 0 && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, e.Data);
    }
  }

  private enum Kind { Unknown, Sna, Z80, Tap, Tzx }

  private static Kind DetectKind(byte[] blob) {
    if (blob.Length >= TzxMagic.Length) {
      var isTzx = true;
      for (var i = 0; i < TzxMagic.Length; i++)
        if (blob[i] != TzxMagic[i]) { isTzx = false; break; }
      if (isTzx) return Kind.Tzx;
    }
    // SNA: 49179 (48K) or 131103+ (128K).
    if (blob.Length == 49179) return Kind.Sna;
    if (blob.Length >= 131103 && (blob.Length - 49179) % 16384 == 0) return Kind.Sna;
    // Z80: v1 header is 30 bytes; byte 6 / 7 encode PC (zero for v2/v3 + extended header follows).
    // Heuristic: if length > 30 and byte 6 and 7 are zero (non-v1) or PC nonzero (v1), call it z80.
    if (blob.Length >= 30) {
      // v1: PC at bytes 6..7; if PC != 0 it's v1. If PC == 0 and bytes 30..31 length > 0 it's v2/v3.
      var pc = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(6, 2));
      if (pc != 0) {
        // Extra plausibility: .z80 almost always 30 + 49152 bytes for v1 48K (= 49182) or
        // 30 + compressed; accept freely since we have no better signal.
        return Kind.Z80;
      }
      if (blob.Length >= 32) {
        var extLen = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(30, 2));
        if (extLen == 23 || extLen == 54) return Kind.Z80;
      }
    }
    // TAP: walk blocks.
    if (LooksLikeTap(blob)) return Kind.Tap;
    return Kind.Unknown;
  }

  private static bool LooksLikeTap(byte[] blob) {
    var pos = 0;
    var blocks = 0;
    while (pos + 2 <= blob.Length) {
      var len = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(pos, 2));
      pos += 2;
      if (len == 0 || pos + len > blob.Length) return false;
      pos += len;
      blocks++;
      if (blocks > 1000) break;
    }
    return pos == blob.Length && blocks > 0;
  }

  private static IReadOnlyList<(string Name, byte[] Data)> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var blob = ms.ToArray();

    var kind = DetectKind(blob);
    var ext = kind switch {
      Kind.Sna => ".sna",
      Kind.Z80 => ".z80",
      Kind.Tap => ".tap",
      Kind.Tzx => ".tzx",
      _ => ".bin",
    };

    var entries = new List<(string Name, byte[] Data)> {
      ("FULL" + ext, blob),
    };

    var meta = new StringBuilder();
    meta.AppendLine("; ZX Spectrum snapshot/tape metadata");
    meta.Append("format=").AppendLine(kind switch {
      Kind.Sna => "sna",
      Kind.Z80 => "z80",
      Kind.Tap => "tap",
      Kind.Tzx => "tzx",
      _ => "unknown",
    });

    try {
      switch (kind) {
        case Kind.Sna: BuildSna(blob, entries, meta); break;
        case Kind.Z80: BuildZ80(blob, entries, meta); break;
        case Kind.Tap: BuildTap(blob, entries, meta); break;
        case Kind.Tzx: BuildTzx(blob, entries, meta); break;
        default:
          meta.AppendLine("parse_status=partial");
          meta.AppendLine("reason=unknown_format");
          break;
      }
    } catch {
      meta.AppendLine("parse_status=partial");
      meta.AppendLine("reason=parse_exception");
    }

    entries.Insert(1, ("metadata.ini", Encoding.UTF8.GetBytes(meta.ToString())));
    return entries;
  }

  private static void BuildSna(byte[] blob, List<(string Name, byte[] Data)> entries, StringBuilder meta) {
    if (blob.Length < 27 + 49152) {
      meta.AppendLine("parse_status=partial");
      meta.AppendLine("reason=sna_too_small");
      return;
    }
    meta.AppendLine("parse_status=ok");
    var model = blob.Length == 49179 ? "48K" : "128K";
    meta.Append("model=").AppendLine(model);
    meta.Append("register_block_bytes=").AppendLine("27");

    var regs = new byte[27];
    Array.Copy(blob, 0, regs, 0, 27);
    entries.Add(("registers.bin", regs));

    // 48K memory: 16K (0x4000-0x7FFF), 32K (0x8000-0xBFFF), 48K (0xC000-0xFFFF).
    var mem16 = new byte[16384];
    var mem32 = new byte[16384];
    var mem48 = new byte[16384];
    Array.Copy(blob, 27, mem16, 0, 16384);
    Array.Copy(blob, 27 + 16384, mem32, 0, 16384);
    Array.Copy(blob, 27 + 32768, mem48, 0, 16384);
    entries.Add(("memory_16k.bin", mem16));
    entries.Add(("memory_32k.bin", mem32));
    entries.Add(("memory_48k.bin", mem48));

    if (model == "128K") {
      // 4 extra bytes of state + additional 16K pages.
      var extraStart = 27 + 49152;
      if (blob.Length >= extraStart + 4) {
        var extra = new byte[4];
        Array.Copy(blob, extraStart, extra, 0, 4);
        entries.Add(("registers_128k.bin", extra));
        var pageStart = extraStart + 4;
        var pageIdx = 0;
        while (pageStart + 16384 <= blob.Length) {
          var page = new byte[16384];
          Array.Copy(blob, pageStart, page, 0, 16384);
          entries.Add(($"pages/page_{pageIdx:D2}.bin", page));
          pageStart += 16384;
          pageIdx++;
        }
        meta.Append("memory_pages_count=").AppendLine(pageIdx.ToString(CultureInfo.InvariantCulture));
      }
    } else {
      meta.AppendLine("memory_pages_count=3");
    }
  }

  private static void BuildZ80(byte[] blob, List<(string Name, byte[] Data)> entries, StringBuilder meta) {
    if (blob.Length < 30) {
      meta.AppendLine("parse_status=partial");
      meta.AppendLine("reason=z80_too_small");
      return;
    }
    var pc = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(6, 2));
    var isV1 = pc != 0;
    var headerLen = 30;
    var version = 1;
    if (!isV1 && blob.Length >= 32) {
      var extLen = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(30, 2));
      headerLen = 32 + extLen;
      version = extLen switch { 23 => 2, 54 => 3, 55 => 3, _ => 2 };
    }
    meta.AppendLine("parse_status=ok");
    meta.Append("z80_version=").AppendLine(version.ToString(CultureInfo.InvariantCulture));
    meta.Append("register_block_bytes=").AppendLine(headerLen.ToString(CultureInfo.InvariantCulture));
    meta.Append("model=").AppendLine(version == 1 ? "48K" : "varies");

    if (headerLen > blob.Length) headerLen = blob.Length;
    var hdr = new byte[headerLen];
    Array.Copy(blob, 0, hdr, 0, headerLen);
    entries.Add(("header.bin", hdr));

    // Body: for v1, the body is just compressed/uncompressed 48K memory.
    // For v2/v3 there's a sequence of page-prefix + data blocks.
    if (isV1) {
      if (blob.Length > headerLen) {
        var body = new byte[blob.Length - headerLen];
        Array.Copy(blob, headerLen, body, 0, body.Length);
        entries.Add(("pages/page_00.bin", body));
      }
      meta.AppendLine("memory_pages_count=1");
    } else {
      var pos = headerLen;
      var pageIdx = 0;
      while (pos + 3 <= blob.Length) {
        var plen = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(pos, 2));
        var pnum = blob[pos + 2];
        pos += 3;
        // Length 0xFFFF = raw uncompressed 16384 (old extension semantic).
        var dataLen = plen == 0xFFFF ? 16384 : plen;
        if (pos + dataLen > blob.Length) break;
        // Emit the 3-byte page header followed by raw bytes (not decompressed).
        var pageBlob = new byte[3 + dataLen];
        BinaryPrimitives.WriteUInt16LittleEndian(pageBlob.AsSpan(0, 2), plen);
        pageBlob[2] = pnum;
        Array.Copy(blob, pos, pageBlob, 3, dataLen);
        entries.Add(($"pages/page_{pnum:D2}.bin", pageBlob));
        pos += dataLen;
        pageIdx++;
        if (pageIdx > 32) break; // safety
      }
      meta.Append("memory_pages_count=").AppendLine(pageIdx.ToString(CultureInfo.InvariantCulture));
    }
  }

  private static void BuildTap(byte[] blob, List<(string Name, byte[] Data)> entries, StringBuilder meta) {
    var pos = 0;
    var idx = 0;
    while (pos + 2 <= blob.Length) {
      var len = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(pos, 2));
      pos += 2;
      if (len == 0 || pos + len > blob.Length) break;
      var block = new byte[len];
      Array.Copy(blob, pos, block, 0, len);
      entries.Add(($"blocks/block_{idx:D3}.bin", block));
      pos += len;
      idx++;
      if (idx > 4096) break;
    }
    meta.AppendLine("parse_status=ok");
    meta.Append("memory_pages_count=").AppendLine(idx.ToString(CultureInfo.InvariantCulture));
  }

  private static void BuildTzx(byte[] blob, List<(string Name, byte[] Data)> entries, StringBuilder meta) {
    if (blob.Length < 10) {
      meta.AppendLine("parse_status=partial");
      meta.AppendLine("reason=tzx_too_small");
      return;
    }
    var major = blob[8];
    var minor = blob[9];
    meta.AppendLine("parse_status=ok");
    meta.Append("tzx_version=").Append(major.ToString(CultureInfo.InvariantCulture))
      .Append('.').AppendLine(minor.ToString(CultureInfo.InvariantCulture));

    var pos = 10;
    var idx = 0;
    while (pos < blob.Length) {
      var id = blob[pos];
      var (length, headerSkip) = TzxBlockLength(blob, pos);
      if (length < 0 || pos + length > blob.Length) break;
      var total = length;
      var block = new byte[total];
      Array.Copy(blob, pos, block, 0, total);
      entries.Add(($"blocks/block_{idx:D3}_id{id:X2}.bin", block));
      pos += total;
      idx++;
      if (idx > 4096) break;
      _ = headerSkip;
    }
    meta.Append("memory_pages_count=").AppendLine(idx.ToString(CultureInfo.InvariantCulture));
  }

  // Returns (totalBlockLength from id to end-of-data, headerSkipFromId).
  // For unknown blocks returns (-1,-1).
  private static (int Length, int HeaderSkip) TzxBlockLength(byte[] blob, int p) {
    if (p >= blob.Length) return (-1, -1);
    var id = blob[p];
    int len;
    switch (id) {
      case 0x10: // Standard speed data
        if (p + 5 > blob.Length) return (-1, -1);
        len = 1 + 4 + BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(p + 3, 2));
        return (len, 5);
      case 0x11: // Turbo speed data
        if (p + 19 > blob.Length) return (-1, -1);
        // Data length = 3 bytes at +0x0F
        var dl11 = blob[p + 0x10] | (blob[p + 0x11] << 8) | (blob[p + 0x12] << 16);
        len = 1 + 0x12 + dl11;
        return (len, 0x13);
      case 0x12: return (1 + 4, 5); // Pure tone
      case 0x13: // Pulse sequence
        if (p + 2 > blob.Length) return (-1, -1);
        return (1 + 1 + blob[p + 1] * 2, 2);
      case 0x14: // Pure data
        if (p + 11 > blob.Length) return (-1, -1);
        var dl14 = blob[p + 8] | (blob[p + 9] << 8) | (blob[p + 10] << 16);
        len = 1 + 10 + dl14;
        return (len, 11);
      case 0x15: // Direct recording
        if (p + 9 > blob.Length) return (-1, -1);
        var dl15 = blob[p + 6] | (blob[p + 7] << 8) | (blob[p + 8] << 16);
        len = 1 + 8 + dl15;
        return (len, 9);
      case 0x18: // CSW recording
      case 0x19: // Generalized data
      case 0x35: // Custom info
        if (p + 5 > blob.Length) return (-1, -1);
        var lu = BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(p + 1, 4));
        len = 1 + 4 + (int)lu;
        return (len, 5);
      case 0x20: // Pause
      case 0x23: // Jump
      case 0x24: // Loop start
      case 0x27: // Return from call
        return (1 + 2, 3);
      case 0x21: // Group start
        if (p + 2 > blob.Length) return (-1, -1);
        return (1 + 1 + blob[p + 1], 2);
      case 0x22: // Group end
      case 0x25: // Loop end
        return (1, 1);
      case 0x26: // Call sequence
        if (p + 3 > blob.Length) return (-1, -1);
        return (1 + 2 + BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(p + 1, 2)) * 2, 3);
      case 0x28: // Select block
        if (p + 3 > blob.Length) return (-1, -1);
        return (1 + 2 + BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(p + 1, 2)), 3);
      case 0x2A: return (1 + 4, 5); // Stop the tape if 48K
      case 0x2B: return (1 + 4 + 1, 6); // Set signal level (len=1)
      case 0x30: // Text description
        if (p + 2 > blob.Length) return (-1, -1);
        return (1 + 1 + blob[p + 1], 2);
      case 0x31: // Message
        if (p + 3 > blob.Length) return (-1, -1);
        return (1 + 2 + blob[p + 2], 3);
      case 0x32: // Archive info
      case 0x33: // Hardware type
      case 0x34: // Emulation info (old)
        if (p + 3 > blob.Length) return (-1, -1);
        return (1 + 2 + BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(p + 1, 2)), 3);
      case 0x40: // Snapshot block
        if (p + 5 > blob.Length) return (-1, -1);
        var sl = blob[p + 2] | (blob[p + 3] << 8) | (blob[p + 4] << 16);
        len = 1 + 4 + sl;
        return (len, 5);
      case 0x5A: // "Glue" block
        return (1 + 9, 10);
      default:
        return (-1, -1);
    }
  }
}
