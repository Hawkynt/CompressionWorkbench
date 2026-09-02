#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;

namespace FileFormat.JpegArchive;

/// <summary>
/// Surfaces a JPEG file as an archive of its addressable sub-images:
/// <c>FULL.jpg</c>, <c>thumbnail_exif.jpg</c> (IFD1 inside APP1 EXIF), XMP/IPTC
/// text metadata, and the raw EXIF bytes. A cameraphone photo typically carries
/// a 160×120 thumbnail here that no ordinary JPEG viewer surfaces separately.
/// <para>
/// The existing JPEG single-image path (via <c>FileFormat.PngCrushAdapters</c>)
/// owns magic resolution for <c>.jpg</c>/<c>.jpeg</c>. This descriptor is
/// accessible explicitly via <c>cwb list --format JpegArchive photo.jpg</c>.
/// </para>
/// </summary>
public sealed class JpegArchiveDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveInMemoryExtract, IArchiveLayoutMap, IArchiveDefragmentable {
  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "JpegArchive";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "JPEG (archive view)";
  /// <summary>
  /// Gets the category.
  /// </summary>
  public FormatCategory Category => FormatCategory.Image;
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsOptimize;
  /// <summary>
  /// Gets the default extension.
  /// </summary>
  public string DefaultExtension => ".jpg";
  // Empty — extension collides with the primary JPEG reader; only addressable by explicit --format.
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
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
  public string Description => "JPEG sub-image extraction: full image + EXIF thumbnail + XMP/IPTC metadata.";

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
  public List<ArchiveEntryInfo> List(Stream stream, string? password) =>
    BuildEntries(stream).Select((e, i) => new ArchiveEntryInfo(
      Index: i, Name: e.Name,
      OriginalSize: e.Data.Length, CompressedSize: e.Data.Length,
      Method: "stored", IsDirectory: false, IsEncrypted: false, LastModified: null,
      Kind: e.Kind)).ToList();

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    foreach (var e in BuildEntries(stream)) {
      if (files != null && files.Length > 0 && !FormatHelpers.MatchesFilter(e.Name, files))
        continue;
      FormatHelpers.WriteFile(outputDir, e.Name, e.Data);
    }
  }

  /// <summary>
  /// Performs the extract entry operation.
  /// </summary>
  public void ExtractEntry(Stream input, string entryName, Stream output, string? password) {
    foreach (var e in BuildEntries(input)) {
      if (e.Name.Equals(entryName, StringComparison.OrdinalIgnoreCase)) {
        output.Write(e.Data);
        return;
      }
    }
    throw new FileNotFoundException($"Entry not found: {entryName}");
  }

  private static IReadOnlyList<(string Name, string Kind, byte[] Data)> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var blob = ms.ToArray();

    var entries = new List<(string Name, string Kind, byte[] Data)> {
      ("FULL.jpg", "Container", blob),
    };

    // Walk APP markers.
    byte[]? exifTiffArea = null;
    var photoshopThumbnails = new List<byte[]>();
    string? xmpPacket = null;
    byte[]? iptc = null;

    var pos = 2;  // skip SOI
    while (pos + 4 < blob.Length) {
      if (blob[pos] != 0xFF) break;
      var marker = blob[pos + 1];
      if (marker == 0xD9 /* EOI */ || marker == 0xDA /* SOS — no more app markers */) break;
      if (marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7)) { pos += 2; continue; }

      var segLen = BinaryPrimitives.ReadUInt16BigEndian(blob.AsSpan(pos + 2));
      if (pos + 2 + segLen > blob.Length) break;
      var segStart = pos + 4;
      var segEnd = pos + 2 + segLen;
      var body = blob.AsSpan(segStart, segEnd - segStart);

      switch (marker) {
        case 0xE1 /* APP1 */:
          if (body.Length >= 6 && body[0] == 'E' && body[1] == 'x' && body[2] == 'i' && body[3] == 'f' &&
              body[4] == 0 && body[5] == 0) {
            exifTiffArea = body[6..].ToArray();
          } else if (body.Length >= 29 &&
                     Encoding.ASCII.GetString(body[..28]) == "http://ns.adobe.com/xap/1.0/") {
            xmpPacket = Encoding.UTF8.GetString(body[29..]);
          }
          break;
        case 0xED /* APP13 — Photoshop IRB */:
          if (body.Length >= 14 && Encoding.ASCII.GetString(body[..13]) == "Photoshop 3.0") {
            ParsePhotoshopIrb(body[14..], photoshopThumbnails, out iptc);
          }
          break;
      }

      pos = segEnd;
    }

    if (exifTiffArea != null) {
      entries.Add(("metadata/exif.bin", "Tag", exifTiffArea));
      var thumb = ExifIfdParser.FindIfd1Thumbnail(exifTiffArea);
      if (thumb != null && thumb.Offset + thumb.Length <= exifTiffArea.Length) {
        var thumbBytes = exifTiffArea.AsSpan(thumb.Offset, thumb.Length).ToArray();
        entries.Add(("thumbnail_exif.jpg", "Tag", thumbBytes));
      }
    }
    if (xmpPacket != null)
      entries.Add(("metadata/xmp.xml", "Tag", Encoding.UTF8.GetBytes(xmpPacket)));
    if (iptc != null)
      entries.Add(("metadata/iptc.iim", "Tag", iptc));
    for (var i = 0; i < photoshopThumbnails.Count; ++i)
      entries.Add(($"thumbnail_photoshop_{i:D2}.jpg", "Tag", photoshopThumbnails[i]));

    return entries;
  }

  // ── IArchiveLayoutMap ───────────────────────────────────────────

  /// <inheritdoc />
  public IEnumerable<DefragBlockInfo> EnumerateLayout(Stream archive) => JpegLayoutMap.Enumerate(archive);

  // ── IArchiveDefragmentable ────────────────────────────────────

  /// <inheritdoc />
  public void Defragment(Stream archive) => JpegOptimizer.Optimize(archive);

  /// <inheritdoc />
  public void Defragment(Stream archive, DefragOptions options) {
    // JPEG optimization is a single fixed strategy: move EXIF to front.
    // All defrag modes converge to the same operation, unless a metadata
    // placement profile overrides the default.
    JpegOptimizer.Optimize(archive, options.MetadataPlacement);
  }

  // Photoshop IRB: sequence of 8BIM resource blocks.
  // Each: "8BIM" (4) + resourceId (2 BE) + pascalName (2-byte-padded) + size (4 BE) + data (padded).
  // Resource ID 0x0404 = IPTC block; 0x0409 / 0x040C = thumbnail with 28-byte header + JPEG.
  private static void ParsePhotoshopIrb(ReadOnlySpan<byte> irb, List<byte[]> thumbnails, out byte[]? iptc) {
    iptc = null;
    var pos = 0;
    while (pos + 12 < irb.Length) {
      if (irb[pos] != '8' || irb[pos + 1] != 'B' || irb[pos + 2] != 'I' || irb[pos + 3] != 'M') break;
      var resId = BinaryPrimitives.ReadUInt16BigEndian(irb[(pos + 4)..]);
      var nameLen = irb[pos + 6];
      var namePad = (nameLen + 1) % 2 == 0 ? 0 : 1;
      var dataStart = pos + 6 + 1 + nameLen + namePad;
      if (dataStart + 4 > irb.Length) break;
      var dataSize = (int)BinaryPrimitives.ReadUInt32BigEndian(irb[dataStart..]);
      var data = irb.Slice(dataStart + 4, Math.Min(dataSize, irb.Length - dataStart - 4));

      if (resId == 0x0404) iptc = data.ToArray();
      else if (resId is 0x0409 or 0x040C) {
        // Thumbnail resource: 28-byte header then raw JPEG bytes.
        if (data.Length > 28) thumbnails.Add(data[28..].ToArray());
      }

      var totalSize = 6 + 1 + nameLen + namePad + 4 + dataSize + (dataSize % 2);
      pos += totalSize;
    }
  }
}
