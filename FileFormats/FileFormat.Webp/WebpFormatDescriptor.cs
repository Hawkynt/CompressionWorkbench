#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Compression.Registry;

namespace FileFormat.Webp;

/// <summary>
/// Exposes a WebP file as a pseudo-archive: <c>FULL.webp</c> (verbatim, Kind="Track")
/// and <c>metadata.ini</c> (Kind="Tag") are always present, plus per-frame
/// <c>frames/frame_NNN.webp</c> (Kind="Frame") for animated WebPs (VP8X + ANMF
/// chunks) and ancillary metadata chunks (EXIF / XMP / ICCP) under
/// <c>metadata/</c> (Kind="Tag"). The metadata summary records the RIFF size,
/// canvas dimensions, animation flag, frame count, and loop count parsed from the
/// VP8X / ANIM chunks. Malformed input never throws from <see cref="List"/> or
/// <see cref="Extract"/> — it falls back to FULL + a partial metadata note.
/// </summary>
public sealed class WebpFormatDescriptor :
  IFormatDescriptor, IArchiveFormatOperations, IArchiveInMemoryExtract {
  public string Id => "Webp";
  public string DisplayName => "WebP";
  public FormatCategory Category => FormatCategory.Image;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".webp";
  public IReadOnlyList<string> Extensions => [".webp"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    // "RIFF" at 0 + "WEBP" at 8. Match the 4-byte "WEBP" at offset 8 for a tighter fit.
    new("WEBP"u8.ToArray(), Offset: 8, Confidence: 0.95),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "VP8/VP8L")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "WebP image container (pseudo-archive): FULL.webp + metadata.ini always; " +
    "animated frames (ANMF) surface as standalone WebPs; EXIF/XMP/ICCP chunks extractable.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var blob = ReadAll(stream);
    return BuildEntries(blob)
      .Select((e, i) => new ArchiveEntryInfo(
        Index: i, Name: e.Name,
        OriginalSize: e.Data.Length, CompressedSize: e.Data.Length,
        Method: "stored", IsDirectory: false, IsEncrypted: false, LastModified: null,
        Kind: e.Kind))
      .ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var blob = ReadAll(stream);
    foreach (var e in BuildEntries(blob)) {
      if (files != null && files.Length > 0 && !FormatHelpers.MatchesFilter(e.Name, files))
        continue;
      FormatHelpers.WriteFile(outputDir, e.Name, e.Data);
    }
  }

  public void ExtractEntry(Stream input, string entryName, Stream output, string? password) {
    var blob = ReadAll(input);
    foreach (var e in BuildEntries(blob)) {
      if (e.Name.Equals(entryName, StringComparison.OrdinalIgnoreCase)) {
        output.Write(e.Data);
        return;
      }
    }
    throw new FileNotFoundException($"Entry not found: {entryName}");
  }

  private static byte[] ReadAll(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return ms.ToArray();
  }

  private static IReadOnlyList<(string Name, string Kind, byte[] Data)> BuildEntries(byte[] blob) {
    var entries = new List<(string Name, string Kind, byte[] Data)> {
      ("FULL.webp", "Track", blob),
    };

    // Parsing can fail on malformed input — never let that take down the listing.
    // Fall back to FULL + a partial metadata note so callers can still recover bytes.
    WebpReader? reader = null;
    try { reader = new WebpReader(blob); }
    catch { /* keep reader null → partial metadata below */ }

    if (reader == null) {
      entries.Add(("metadata.ini", "Tag", Encoding.UTF8.GetBytes(
        "; WebP container metadata\nparse_status=partial\nreason=not_a_valid_riff_webp\n")));
      return entries;
    }

    // Index the chunks we care about for the metadata summary.
    WebpReader.Chunk? vp8x = null, anim = null;
    var frameChunks = new List<WebpReader.Chunk>();
    string? stillCodec = null;
    foreach (var chunk in reader.Chunks) {
      switch (chunk.FourCc) {
        case "VP8X": vp8x = chunk; break;
        case "ANIM": anim = chunk; break;
        case "ANMF": frameChunks.Add(chunk); break;
        case "VP8 ": stillCodec ??= "VP8 (lossy)"; break;
        case "VP8L": stillCodec ??= "VP8L (lossless)"; break;
      }
    }

    var meta = new StringBuilder();
    meta.AppendLine("; WebP container metadata");
    meta.AppendLine("parse_status=ok");
    meta.Append("riff_size=").AppendLine(blob.Length.ToString(CultureInfo.InvariantCulture));
    meta.Append("chunk_count=").AppendLine(reader.Chunks.Count.ToString(CultureInfo.InvariantCulture));

    var animated = false;
    if (vp8x != null) {
      var v = reader.ReadBody(vp8x);
      if (v.Length >= 10) {
        var flags = v[0];
        animated = (flags & 0x02) != 0;
        var hasExif = (flags & 0x08) != 0;
        var hasXmp = (flags & 0x04) != 0;
        var hasIccp = (flags & 0x20) != 0;
        var hasAlpha = (flags & 0x10) != 0;
        // Canvas width/height are 24-bit little-endian, stored as value minus one.
        var width = (v[4] | (v[5] << 8) | (v[6] << 16)) + 1;
        var height = (v[7] | (v[8] << 8) | (v[9] << 16)) + 1;
        meta.Append("width=").AppendLine(width.ToString(CultureInfo.InvariantCulture));
        meta.Append("height=").AppendLine(height.ToString(CultureInfo.InvariantCulture));
        meta.Append("has_alpha=").AppendLine(hasAlpha ? "true" : "false");
        meta.Append("has_exif=").AppendLine(hasExif ? "true" : "false");
        meta.Append("has_xmp=").AppendLine(hasXmp ? "true" : "false");
        meta.Append("has_iccp=").AppendLine(hasIccp ? "true" : "false");
      }
    } else if (stillCodec != null) {
      meta.Append("codec=").AppendLine(stillCodec);
    }

    meta.Append("animated=").AppendLine(animated ? "true" : "false");
    if (animated) {
      meta.Append("frame_count=").AppendLine(frameChunks.Count.ToString(CultureInfo.InvariantCulture));
      if (anim != null) {
        var a = reader.ReadBody(anim);
        if (a.Length >= 6) {
          // ANIM: 4-byte background color (BGRA) + 2-byte loop count (LE). 0 = infinite.
          var loop = BinaryPrimitives.ReadUInt16LittleEndian(a.AsSpan(4));
          meta.Append("loop_count=").AppendLine(loop == 0 ? "0 (infinite)" : loop.ToString(CultureInfo.InvariantCulture));
        }
      }
    }

    entries.Add(("metadata.ini", "Tag", Encoding.UTF8.GetBytes(meta.ToString())));

    // Per-frame standalone WebPs for animated files.
    var frameIndex = 0;
    foreach (var chunk in reader.Chunks) {
      switch (chunk.FourCc) {
        case "ANMF":
          // Animation frame. Body: 16-byte ANMF header + VP8/VP8L sub-chunk.
          // Rebuild a standalone still WebP wrapping that sub-chunk so the extracted
          // bytes open in any viewer.
          var body = reader.ReadBody(chunk);
          if (body.Length > 16) {
            var sub = body.AsSpan(16).ToArray();
            entries.Add(($"frames/frame_{frameIndex:D3}.webp", "Frame", WrapAsWebp(sub)));
            ++frameIndex;
          }
          break;
        case "EXIF":
          entries.Add(("metadata/exif.bin", "Tag", reader.ReadBody(chunk)));
          break;
        case "XMP ":
          entries.Add(("metadata/xmp.xml", "Tag", reader.ReadBody(chunk)));
          break;
        case "ICCP":
          entries.Add(("metadata/icc.bin", "Tag", reader.ReadBody(chunk)));
          break;
      }
    }
    return entries;
  }

  // Wraps a VP8/VP8L/VP8X sub-chunk as a standalone RIFF/WEBP file.
  private static byte[] WrapAsWebp(byte[] vp8Body) {
    using var ms = new MemoryStream();
    ms.Write("RIFF"u8);
    Span<byte> sz = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(sz, (uint)(4 + vp8Body.Length));
    ms.Write(sz);
    ms.Write("WEBP"u8);
    ms.Write(vp8Body);
    return ms.ToArray();
  }
}
