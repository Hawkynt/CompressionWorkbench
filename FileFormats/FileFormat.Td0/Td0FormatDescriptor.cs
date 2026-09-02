#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Td0;

/// <summary>
/// TeleDisk (TD0) floppy image. 12-byte header: signature ("TD" normal /
/// "td" advanced LZH-compressed), sequence (u8), check-sequence (u8), version
/// (u8, BCD-ish), data rate / density (u8), drive type (u8), stepping/flags (u8),
/// DOS-allocation flag (u8), sides (u8), CRC (u16 LE over the first 10 bytes).
/// When the flags byte has bit 7 set (0x80) an optional comment block follows the
/// header: CRC (u16), length (u16 LE), year-since-1900 (u8), month (u8), day (u8),
/// hour (u8), minute (u8), second (u8), then <c>length</c> bytes of NUL-separated
/// comment text. After that come per-track records.
///
/// <para>This descriptor surfaces <c>FULL.td0</c> verbatim plus a
/// <c>metadata.ini</c> (version, sides, density, drive type, advanced-compression
/// flag, comment). For "TD" (uncompressed) images the per-track/sector layout is
/// walked and surfaced; for the "td" advanced (TeleDisk LZH) variant the body is
/// LZH-compressed and its track inventory is not decoded — that is recorded in the
/// metadata as <c>compression=advanced-lzh</c> with <c>parse_status=partial</c>.
/// Read-only; malformed input degrades to FULL + partial metadata without
/// throwing.</para>
///
/// References:
/// <list type="bullet">
///   <item><description>Dave Dunfield's TeleDisk .TD0 format notes — the de-facto public spec (Sydex never published one)</description></item>
///   <item><description><c>https://github.com/brouhaha/wteledsk</c> — wteledsk — open TeleDisk image extractor</description></item>
/// </list>
/// </summary>
public sealed class Td0FormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "Td0";
  /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "TeleDisk (TD0)";
  /// <summary>
  /// Gets the category.
  /// </summary>
public FormatCategory Category => FormatCategory.Archive;
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  /// <summary>
  /// Gets the default extension.
  /// </summary>
public string DefaultExtension => ".td0";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
public IReadOnlyList<string> Extensions => [".td0"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("TD"u8.ToArray(), Confidence: 0.80),
    new("td"u8.ToArray(), Confidence: 0.80),
  ];
  /// <summary>
  /// Gets the methods.
  /// </summary>
public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  /// <summary>
  /// Gets the tar compression format id.
  /// </summary>
public string? TarCompressionFormatId => null;
  /// <summary>
  /// Gets the family.
  /// </summary>
public AlgorithmFamily Family => AlgorithmFamily.Archive;
  /// <summary>
  /// Gets the description.
  /// </summary>
public string Description =>
    "TeleDisk (TD0) floppy image: header + optional comment + track/sector inventory; advanced LZH body deferred.";

  private sealed record Td0Header(
    bool Advanced,
    byte Sequence,
    byte Version,
    byte DataRate,
    byte DriveType,
    byte Stepping,
    byte DosFlag,
    byte Sides,
    bool HasComment,
    string? Comment,
    int TrackDataOffset,
    bool Valid);

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var fullSize = SafeLength(stream);
    var hdr = TryReadHeader(stream);
    var entries = new List<ArchiveEntryInfo> {
      new(0, "FULL.td0", fullSize, fullSize, "Stored", false, false, null),
      new(1, "metadata.ini", 0, 0, "Stored", false, false, null),
    };

    if (hdr.Valid && !hdr.Advanced) {
      var idx = 2;
      foreach (var (name, len) in EnumerateTrackSectors(stream, hdr.TrackDataOffset))
        entries.Add(new ArchiveEntryInfo(idx++, name, len, len, "Stored", false, false, null, "Sector"));
    }
    return entries;
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    if (Wants(files, "FULL.td0"))
      WriteFile(outputDir, "FULL.td0", ReadAll(stream));

    var hdr = TryReadHeader(stream);
    if (Wants(files, "metadata.ini"))
      WriteFile(outputDir, "metadata.ini", Encoding.UTF8.GetBytes(BuildMetadataIni(hdr)));

    if (hdr.Valid && !hdr.Advanced) {
      foreach (var (name, data) in ExtractTrackSectors(stream, hdr.TrackDataOffset)) {
        if (Wants(files, name))
          WriteFile(outputDir, name, data);
      }
    }
  }

  private static bool Wants(string[]? files, string name)
    => files == null || files.Length == 0 || MatchesFilter(name, files);

  private static Td0Header TryReadHeader(Stream stream) {
    try {
      if (!stream.CanSeek) return Invalid();
      stream.Position = 0;
      Span<byte> h = stackalloc byte[12];
      if (!TryReadExact(stream, h)) return Invalid();

      bool advanced;
      if (h[0] == (byte)'T' && h[1] == (byte)'D') advanced = false;
      else if (h[0] == (byte)'t' && h[1] == (byte)'d') advanced = true;
      else return Invalid();

      var sequence = h[2];
      var version = h[4];
      var dataRate = h[5];
      var driveType = h[6];
      var stepping = h[7];
      var dosFlag = h[8];
      var sides = h[9];
      var hasComment = (stepping & 0x80) != 0;

      var offset = 12;
      string? comment = null;
      if (hasComment) {
        // Comment header: CRC(2), length(2 LE), 6 date bytes, then length bytes.
        Span<byte> ch = stackalloc byte[10];
        stream.Position = 12;
        if (TryReadExact(stream, ch)) {
          var len = BinaryPrimitives.ReadUInt16LittleEndian(ch[2..4]);
          offset = 12 + 10 + len;
          if (len is > 0 and < 65535 && 22L + len <= stream.Length) {
            var cbuf = new byte[len];
            if (TryReadExact(stream, cbuf)) {
              // Comment fields are NUL-separated; flatten to newline-separated text.
              comment = Encoding.Latin1.GetString(cbuf).Replace('\0', '\n').Trim();
            }
          }
        }
      }

      return new Td0Header(advanced, sequence, version, dataRate, driveType, stepping,
        dosFlag, sides, hasComment, comment, offset, Valid: true);
    } catch {
      return Invalid();
    }
  }

  private static Td0Header Invalid()
    => new(false, 0, 0, 0, 0, 0, 0, 0, false, null, 0, Valid: false);

  // Walk uncompressed track records. Each track header: sectors(u8), cyl(u8),
  // head(u8), CRC(u8). 0xFF sector count terminates. Each sector: cyl, head,
  // sectorNumber, sizeCode, flags, dataCRC, then a data block: dataLen(u16 LE) +
  // encoding(u8) + payload.
  private static IEnumerable<(string Name, long Length)> EnumerateTrackSectors(Stream stream, int start) {
    foreach (var (name, data) in ExtractTrackSectors(stream, start))
      yield return (name, data.Length);
  }

  private static IEnumerable<(string Name, byte[] Data)> ExtractTrackSectors(Stream stream, int start) {
    var result = new List<(string, byte[])>();
    try {
      if (!stream.CanSeek || start <= 0 || start >= stream.Length) return result;
      stream.Position = start;
      Span<byte> th = stackalloc byte[4];
      Span<byte> sh = stackalloc byte[6];
      Span<byte> dl = stackalloc byte[3];
      var guard = 0;
      while (stream.Position + 4 <= stream.Length && guard++ < 4096) {
        if (!TryReadExact(stream, th)) break;
        var sectorCount = th[0];
        var cyl = th[1];
        var head = th[2];
        if (sectorCount == 0xFF) break; // end of tracks
        if (sectorCount == 0) continue;

        for (var s = 0; s < sectorCount; ++s) {
          if (!TryReadExact(stream, sh)) return result;
          var sCyl = sh[0];
          var sHead = sh[1];
          var sNum = sh[2];
          var sizeCode = sh[3];
          var flags = sh[4];
          // Sector size: 128 << sizeCode (clamped).
          var sectorSize = sizeCode <= 6 ? 128 << sizeCode : 0;

          byte[] sectorData;
          // Flags bit 0x30 set => no data block present for this sector.
          if ((flags & 0x30) != 0) {
            sectorData = [];
          } else {
            if (!TryReadExact(stream, dl)) return result;
            var blockLen = BinaryPrimitives.ReadUInt16LittleEndian(dl[..2]); // includes encoding byte
            var encoding = dl[2];
            var payloadLen = blockLen >= 1 ? blockLen - 1 : 0;
            if (payloadLen < 0 || stream.Position + payloadLen > stream.Length) return result;
            var payload = new byte[payloadLen];
            if (!TryReadExact(stream, payload)) return result;
            sectorData = DecodeSectorPayload(encoding, payload, sectorSize);
          }

          var name = string.Create(CultureInfo.InvariantCulture,
            $"tracks/c{cyl:D2}_h{head}_s{sNum:D2}.bin");
          _ = sCyl; _ = sHead;
          result.Add((name, sectorData));
          if (result.Count > 4096) return result;
        }
      }
    } catch {
      // best-effort
    }
    return result;
  }

  // TeleDisk sector encodings: 0 = raw, 1 = repeated 2-byte pattern (count u16 + 2
  // bytes), 2 = RLE block list. Unknown encodings yield the raw payload.
  private static byte[] DecodeSectorPayload(byte encoding, byte[] payload, int sectorSize) {
    try {
      switch (encoding) {
        case 0:
          return payload;
        case 1: {
          // count(u16 LE) of 2-byte repeats.
          if (payload.Length < 4) return payload;
          var count = BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(0, 2));
          var b0 = payload[2];
          var b1 = payload[3];
          var outLen = count * 2;
          if (outLen <= 0 || outLen > 1 << 20) return payload;
          var outBuf = new byte[outLen];
          for (var i = 0; i < count; ++i) { outBuf[i * 2] = b0; outBuf[i * 2 + 1] = b1; }
          return outBuf;
        }
        case 2: {
          // Sequence of blocks: kind(u8). kind 0: literal len(u8) + bytes.
          // kind 1: repeat count(u8) + 2-byte pattern.
          using var ms = new MemoryStream();
          var p = 0;
          while (p < payload.Length) {
            var kind = payload[p++];
            if (kind == 0) {
              if (p >= payload.Length) break;
              var len = payload[p++];
              if (p + len > payload.Length) break;
              ms.Write(payload, p, len);
              p += len;
            } else if (kind == 1) {
              if (p + 3 > payload.Length) break;
              var count = payload[p++];
              var b0 = payload[p++];
              var b1 = payload[p++];
              for (var i = 0; i < count; ++i) { ms.WriteByte(b0); ms.WriteByte(b1); }
            } else {
              break;
            }
            if (ms.Length > 1 << 20) break;
          }
          var decoded = ms.ToArray();
          return decoded.Length > 0 ? decoded : payload;
        }
        default:
          return payload;
      }
    } catch {
      return payload;
    }
  }

  private static string BuildMetadataIni(Td0Header h) {
    var sb = new StringBuilder();
    sb.Append("[Td0]\n");
    sb.Append(CultureInfo.InvariantCulture, $"valid={(h.Valid ? 1 : 0)}\n");
    if (!h.Valid) {
      sb.Append("parse_status=partial\n");
      return sb.ToString();
    }
    sb.Append(CultureInfo.InvariantCulture, $"compression={(h.Advanced ? "advanced-lzh" : "none")}\n");
    sb.Append(CultureInfo.InvariantCulture, $"version=0x{h.Version:X2}\n");
    sb.Append(CultureInfo.InvariantCulture, $"sides={h.Sides}\n");
    sb.Append(CultureInfo.InvariantCulture, $"data_rate=0x{h.DataRate:X2}\n");
    sb.Append(CultureInfo.InvariantCulture, $"drive_type=0x{h.DriveType:X2}\n");
    sb.Append(CultureInfo.InvariantCulture, $"dos_allocation_flag={h.DosFlag}\n");
    sb.Append(CultureInfo.InvariantCulture, $"has_comment={(h.HasComment ? 1 : 0)}\n");
    if (h.Comment != null)
      sb.Append(CultureInfo.InvariantCulture, $"comment={h.Comment.Replace('\n', ' ').Replace('\r', ' ')}\n");
    // Advanced bodies are LZH-compressed; their track inventory is not decoded.
    sb.Append(CultureInfo.InvariantCulture, $"parse_status={(h.Advanced ? "partial" : "ok")}\n");
    return sb.ToString();
  }

  private static bool TryReadExact(Stream stream, Span<byte> buffer) {
    var read = 0;
    while (read < buffer.Length) {
      var n = stream.Read(buffer[read..]);
      if (n <= 0) return false;
      read += n;
    }
    return true;
  }

  private static long SafeLength(Stream s) => s.CanSeek ? s.Length : 0;

  private static byte[] ReadAll(Stream stream) {
    if (stream.CanSeek) stream.Position = 0;
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return ms.ToArray();
  }
}
