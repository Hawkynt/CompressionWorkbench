#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Compression.Registry;
using FileFormat.Core;

namespace FileFormat.Webp;

/// <summary>
/// Exposes a WebP file as a pseudo-archive while delegating RIFF/WebP structure parsing
/// to <c>Hawkynt.FileFormats.Images</c>. Workbench owns only archive naming and the
/// reconstruction of standalone ANMF frame payloads.
/// </summary>
public sealed class WebpFormatDescriptor :
  IFormatDescriptor, IArchiveFormatOperations, IArchiveInMemoryExtract {
  /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "Webp";
  /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "WebP";
  /// <summary>
  /// Gets the category.
  /// </summary>
public FormatCategory Category => FormatCategory.Image;
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  /// <summary>
  /// Gets the default extension.
  /// </summary>
public string DefaultExtension => ".webp";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
public IReadOnlyList<string> Extensions => [".webp"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("WEBP"u8.ToArray(), Offset: 8, Confidence: 0.95),
  ];
  /// <summary>
  /// Gets the methods.
  /// </summary>
public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "VP8/VP8L")];
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
    "WebP image container (pseudo-archive): FULL.webp + metadata.ini always; " +
    "animated frames (ANMF) surface as standalone WebPs; EXIF/XMP/ICCP chunks extractable.";

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
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

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var blob = ReadAll(stream);
    foreach (var e in BuildEntries(blob)) {
      if (files != null && files.Length > 0 && !FormatHelpers.MatchesFilter(e.Name, files))
        continue;
      FormatHelpers.WriteFile(outputDir, e.Name, e.Data);
    }
  }

  /// <summary>
  /// Performs the extract entry operation.
  /// </summary>
public void ExtractEntry(Stream input, string entryName, Stream output, string? password) {
    var blob = ReadAll(input);
    foreach (var e in BuildEntries(blob)) {
      if (!e.Name.Equals(entryName, StringComparison.OrdinalIgnoreCase)) continue;
      output.Write(e.Data);
      return;
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

    IReadOnlyList<ChunkSpan> chunks;
    try { chunks = Hawkynt.FileFormats.Images.FormatRegistry.EnumerateChunks(blob); }
    catch { chunks = []; }

    if (chunks.Count <= 1) {
      entries.Add(("metadata.ini", "Tag", Encoding.UTF8.GetBytes(
        "; WebP container metadata\nparse_status=partial\nreason=not_a_valid_riff_webp\n")));
      return entries;
    }

    ChunkSpan? vp8x = null, anim = null;
    var frameChunks = new List<ChunkSpan>();
    string? stillCodec = null;
    foreach (var chunk in chunks) {
      switch (chunk.Name) {
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
    meta.Append("chunk_count=").AppendLine((chunks.Count - 1).ToString(CultureInfo.InvariantCulture));

    var animated = false;
    if (vp8x is { } x) {
      var body = ReadBody(blob, x);
      if (body.Length >= 10) {
        var flags = body[0];
        animated = (flags & 0x02) != 0;
        var hasExif = (flags & 0x08) != 0;
        var hasXmp = (flags & 0x04) != 0;
        var hasIccp = (flags & 0x20) != 0;
        var hasAlpha = (flags & 0x10) != 0;
        var width = (body[4] | (body[5] << 8) | (body[6] << 16)) + 1;
        var height = (body[7] | (body[8] << 8) | (body[9] << 16)) + 1;
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
      if (anim is { } a) {
        var body = ReadBody(blob, a);
        if (body.Length >= 6) {
          var loop = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(4));
          meta.Append("loop_count=").AppendLine(loop == 0 ? "0 (infinite)" : loop.ToString(CultureInfo.InvariantCulture));
        }
      }
    }

    entries.Add(("metadata.ini", "Tag", Encoding.UTF8.GetBytes(meta.ToString())));

    var frameIndex = 0;
    foreach (var chunk in chunks) {
      switch (chunk.Name) {
        case "ANMF": {
          var body = ReadBody(blob, chunk);
          if (body.Length <= 16) break;
          var subChunk = body.AsSpan(16).ToArray();
          entries.Add(($"frames/frame_{frameIndex:D3}.webp", "Frame", WrapAsWebp(subChunk)));
          ++frameIndex;
          break;
        }
        case "EXIF":
          entries.Add(("metadata/exif.bin", "Tag", ReadBody(blob, chunk)));
          break;
        case "XMP ":
          entries.Add(("metadata/xmp.xml", "Tag", ReadBody(blob, chunk)));
          break;
        case "ICCP":
          entries.Add(("metadata/icc.bin", "Tag", ReadBody(blob, chunk)));
          break;
      }
    }
    return entries;
  }

  private static byte[] ReadBody(byte[] blob, ChunkSpan chunk) {
    if (chunk.Offset < 0 || chunk.Offset + 8 > blob.Length || chunk.Length < 8)
      return [];
    var offset = checked((int)chunk.Offset);
    var declared = BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(offset + 4, 4));
    var available = Math.Min((long)declared, Math.Min(chunk.Length - 8, blob.Length - (offset + 8L)));
    return available <= 0 ? [] : blob.AsSpan(offset + 8, checked((int)available)).ToArray();
  }

  private static byte[] WrapAsWebp(byte[] subChunk) {
    using var ms = new MemoryStream();
    ms.Write("RIFF"u8);
    Span<byte> size = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(size, checked((uint)(4 + subChunk.Length)));
    ms.Write(size);
    ms.Write("WEBP"u8);
    ms.Write(subChunk);
    return ms.ToArray();
  }
}
