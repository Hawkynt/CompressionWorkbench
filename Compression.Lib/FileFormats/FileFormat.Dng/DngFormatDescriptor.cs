#pragma warning disable CS1591
using Compression.Registry;

namespace FileFormat.Dng;

/// <summary>
/// Exposes an Adobe DNG (Digital Negative) or camera-RAW TIFF variant as an archive.
/// Layout: IFD0 typically holds the thumbnail, its <c>SubIFDs</c> chain the full-res
/// previews and the raw sensor IFDs, and an EXIF sub-IFD carries EXIF + MakerNote.
/// We do NOT decode the raw sensor bytes — they come out as raw strip data.
/// Confidence stays low (0.4) so plain TIFF still wins on <c>.tif</c> files; detection
/// leans on extension + the DNGVersion tag.
/// </summary>
public sealed class DngFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveInMemoryExtract {
  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "Dng";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "Adobe DNG / Camera RAW";
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
  public string DefaultExtension => ".dng";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".dng", ".nef", ".cr2", ".raf", ".arw", ".rw2", ".orf", ".pef", ".srw"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  // Same byte-order marks as TIFF — use lower confidence than TiffFormatDescriptor (0.85)
  // so plain .tif still dispatches to TIFF. DNG identity is confirmed by extension / DNGVersion tag.
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([0x49, 0x49, 0x2A, 0x00], Confidence: 0.40), // little-endian TIFF
    new([0x4D, 0x4D, 0x00, 0x2A], Confidence: 0.40), // big-endian TIFF
  ];
  /// <summary>
  /// Gets the methods.
  /// </summary>
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Embedded JPEG + raw strips")];
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
  public string Description => "Adobe DNG (TIFF container) for camera RAW; surfaces thumbnail + previews + raw IFDs.";

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

  private sealed record EntryLayout(
      string Name,
      string Kind,
      IReadOnlyList<DngReader.SourceRange> Ranges,
      byte[]? Generated = null) {

    public int Size => this.Generated?.Length ?? DngReader.GetTotalLength(this.Ranges);
  }

  private static IReadOnlyList<(string Name, string Kind, byte[] Data)> BuildEntries(Stream stream) {
    using var memory = new MemoryStream();
    stream.CopyTo(memory);
    var blob = memory.ToArray();

    return BuildLayout(blob)
      .Select(entry => (
        entry.Name,
        entry.Kind,
        entry.Generated ?? MaterializeRanges(blob, entry.Ranges)))
      .ToList();
  }

  List<ArchiveEntryInfo> IArchiveFormatOperations.ListSpan(ReadOnlySpan<byte> archive, string? password) =>
    BuildLayout(archive).Select((entry, index) => new ArchiveEntryInfo(
      Index: index,
      Name: entry.Name,
      OriginalSize: entry.Size,
      CompressedSize: entry.Size,
      Method: "stored",
      IsDirectory: false,
      IsEncrypted: false,
      LastModified: null,
      Kind: entry.Kind)).ToList();

  void IArchiveFormatOperations.ExtractSpan(
      ReadOnlySpan<byte> archive, string outputDir, string? password, string[]? files) {
    foreach (var entry in BuildLayout(archive)) {
      if (files is { Length: > 0 } && !FormatHelpers.MatchesFilter(entry.Name, files))
        continue;

      if (entry.Generated is { } generated) {
        FormatHelpers.WriteFile(outputDir, entry.Name, generated);
        continue;
      }

      using var output = FormatHelpers.CreateEntryFile(outputDir, entry.Name);
      foreach (var range in entry.Ranges)
        output.Write(archive.Slice(range.Offset, range.Length));
    }
  }

  private static List<EntryLayout> BuildLayout(ReadOnlySpan<byte> blob) {
    var reader = DngReader.ReadLayout(blob);
    var entries = new List<EntryLayout> {
      new("FULL.dng", "Container", [new DngReader.SourceRange(0, blob.Length)]),
    };

    if (reader.TopLevelIfds.Count > 0) {
      var ifd0 = reader.TopLevelIfds[0];
      var jpegRanges = DngReader.GetEmbeddedJpegRanges(blob, ifd0);
      if (DngReader.GetTotalLength(jpegRanges) > 0) {
        entries.Add(new EntryLayout("thumbnail.jpg", "Frame", jpegRanges));
      } else {
        var compression = ifd0.Entries.FirstOrDefault(
          static entry => entry.Tag == DngReader.TagCompression);
        if (compression != null && compression.ValueOrOffset is 6 or 7) {
          var stripRanges = DngReader.GetStripRanges(blob, reader, ifd0);
          if (DngReader.GetTotalLength(stripRanges) > 0)
            entries.Add(new EntryLayout("thumbnail.jpg", "Frame", stripRanges));
        }
      }
    }

    var previewIndex = 0;
    var rawIndex = 0;
    foreach (var subIfd in reader.SubIfds) {
      if (DngReader.IsJpegPreviewIfd(subIfd)) {
        var jpegRanges = DngReader.GetEmbeddedJpegRanges(blob, subIfd);
        if (DngReader.GetTotalLength(jpegRanges) > 0) {
          entries.Add(new EntryLayout($"preview_{previewIndex:D2}.jpg", "Frame", jpegRanges));
          ++previewIndex;
          continue;
        }
      }

      var compression = subIfd.Entries.FirstOrDefault(
        static entry => entry.Tag == DngReader.TagCompression);
      var isJpegStrip = compression != null && compression.ValueOrOffset is 6 or 7;
      var stripRanges = DngReader.GetStripRanges(blob, reader, subIfd);
      if (DngReader.GetTotalLength(stripRanges) == 0)
        continue;

      if (isJpegStrip) {
        entries.Add(new EntryLayout($"preview_{previewIndex:D2}.jpg", "Frame", stripRanges));
        ++previewIndex;
      } else {
        entries.Add(new EntryLayout($"raw_sensor_{rawIndex:D2}.bin", "Frame", stripRanges));
        ++rawIndex;
      }
    }

    if (reader.ExifIfd != null) {
      entries.Add(new EntryLayout(
        "metadata/exif.bin",
        "Tag",
        [],
        SerializeExif(reader.IsBigEndian, reader.ExifIfd)));

      var makerNote = reader.ExifIfd.Entries.FirstOrDefault(
        static entry => entry.Tag == DngReader.TagMakerNote);
      if (makerNote != null && makerNote.Count > 0) {
        if (makerNote.Count <= 4) {
          entries.Add(new EntryLayout(
            "metadata/makernote.bin",
            "Tag",
            [],
            InlineMakerNoteBytes(makerNote, reader.IsBigEndian)));
        } else if (DngReader.GetExternalValueRange(blob, makerNote) is { } makerNoteRange) {
          entries.Add(new EntryLayout(
            "metadata/makernote.bin",
            "Tag",
            [makerNoteRange]));
        }
      }
    }

    return entries;
  }

  private static byte[] MaterializeRanges(
      ReadOnlySpan<byte> source, IReadOnlyList<DngReader.SourceRange> ranges) {
    var result = new byte[DngReader.GetTotalLength(ranges)];
    var destinationOffset = 0;
    foreach (var range in ranges) {
      source.Slice(range.Offset, range.Length).CopyTo(result.AsSpan(destinationOffset));
      destinationOffset += range.Length;
    }
    return result;
  }

  private static byte[] SerializeExif(bool isBigEndian, DngReader.Ifd? exifIfd) {
    if (exifIfd == null) return Array.Empty<byte>();
    // Emit a small synthetic blob: byte-order + entry count + each entry's raw 12 bytes.
    // This isn't a standalone TIFF — just a metadata dump a triage tool can read.
    using var ms = new MemoryStream();
    ms.WriteByte(isBigEndian ? (byte)'M' : (byte)'I');
    ms.WriteByte(isBigEndian ? (byte)'M' : (byte)'I');
    Span<byte> w = stackalloc byte[4];
    System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(w, (ushort)exifIfd.Entries.Count);
    ms.Write(w.Slice(0, 2));
    foreach (var e in exifIfd.Entries) {
      System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(w, e.Tag); ms.Write(w.Slice(0, 2));
      System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(w, e.Type); ms.Write(w.Slice(0, 2));
      System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(w, e.Count); ms.Write(w);
      System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(w, e.ValueOrOffset); ms.Write(w);
    }
    return ms.ToArray();
  }

  private static byte[] InlineMakerNoteBytes(DngReader.Entry e, bool bigEndian) {
    var buf = new byte[Math.Min(4, e.Count)];
    if (bigEndian) {
      buf[0] = (byte)(e.ValueOrOffset >> 24);
      if (buf.Length > 1) buf[1] = (byte)(e.ValueOrOffset >> 16);
      if (buf.Length > 2) buf[2] = (byte)(e.ValueOrOffset >> 8);
      if (buf.Length > 3) buf[3] = (byte)e.ValueOrOffset;
    } else {
      buf[0] = (byte)e.ValueOrOffset;
      if (buf.Length > 1) buf[1] = (byte)(e.ValueOrOffset >> 8);
      if (buf.Length > 2) buf[2] = (byte)(e.ValueOrOffset >> 16);
      if (buf.Length > 3) buf[3] = (byte)(e.ValueOrOffset >> 24);
    }
    return buf;
  }
}
