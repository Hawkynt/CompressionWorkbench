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
  public string Id => "Dng";
  public string DisplayName => "Adobe DNG / Camera RAW";
  public FormatCategory Category => FormatCategory.Image;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".dng";
  public IReadOnlyList<string> Extensions => [".dng", ".nef", ".cr2", ".raf", ".arw", ".rw2", ".orf", ".pef", ".srw"];
  public IReadOnlyList<string> CompoundExtensions => [];
  // Same byte-order marks as TIFF — use lower confidence than TiffFormatDescriptor (0.85)
  // so plain .tif still dispatches to TIFF. DNG identity is confirmed by extension / DNGVersion tag.
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([0x49, 0x49, 0x2A, 0x00], Confidence: 0.40), // little-endian TIFF
    new([0x4D, 0x4D, 0x00, 0x2A], Confidence: 0.40), // big-endian TIFF
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Embedded JPEG + raw strips")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Adobe DNG (TIFF container) for camera RAW; surfaces thumbnail + previews + raw IFDs.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) =>
    BuildEntries(stream).Select((e, i) => new ArchiveEntryInfo(
      Index: i, Name: e.Name,
      OriginalSize: e.Data.Length, CompressedSize: e.Data.Length,
      Method: "stored", IsDirectory: false, IsEncrypted: false, LastModified: null,
      Kind: e.Kind)).ToList();

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    foreach (var e in BuildEntries(stream)) {
      if (files != null && files.Length > 0 && !FormatHelpers.MatchesFilter(e.Name, files))
        continue;
      FormatHelpers.WriteFile(outputDir, e.Name, e.Data);
    }
  }

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
    var reader = new DngReader(blob);

    var entries = new List<(string, string, byte[])> {
      ("FULL.dng", "Track", blob),
    };

    // IFD0 thumbnail: if it has JPEG interchange tags, dump as thumbnail.jpg; otherwise
    // if it has strip offsets and compression == 1 (uncompressed), skip (raw pixel dump
    // without dims is of little value). Compression == 6/7 (OldJPEG/JPEG) → strip bytes
    // form a JPEG.
    if (reader.TopLevelIfds.Count > 0) {
      var ifd0 = reader.TopLevelIfds[0];
      var jpeg = reader.ReadEmbeddedJpeg(ifd0);
      if (jpeg.Length > 0) {
        entries.Add(("thumbnail.jpg", "Frame", jpeg));
      } else {
        var comp = ifd0.Entries.FirstOrDefault(e => e.Tag == DngReader.TagCompression);
        if (comp != null && (comp.ValueOrOffset == 6 || comp.ValueOrOffset == 7)) {
          var strip = reader.ReadStripBytes(ifd0);
          if (strip.Length > 0) entries.Add(("thumbnail.jpg", "Frame", strip));
        }
      }
    }

    // SubIFDs: embedded JPEG previews, raw sensor data, or other metadata IFDs.
    var previewIdx = 0;
    var rawIdx = 0;
    foreach (var sub in reader.SubIfds) {
      if (DngReader.IsJpegPreviewIfd(sub)) {
        var jpeg = reader.ReadEmbeddedJpeg(sub);
        if (jpeg.Length > 0) {
          entries.Add(($"preview_{previewIdx:D2}.jpg", "Frame", jpeg));
          previewIdx++;
          continue;
        }
      }
      // JPEG-compressed strips (OldJPEG / JPEG).
      var comp = sub.Entries.FirstOrDefault(e => e.Tag == DngReader.TagCompression);
      var isJpegStrip = comp != null && (comp.ValueOrOffset == 6 || comp.ValueOrOffset == 7);
      var strip = reader.ReadStripBytes(sub);
      if (strip.Length == 0) continue;
      if (isJpegStrip) {
        entries.Add(($"preview_{previewIdx:D2}.jpg", "Frame", strip));
        previewIdx++;
      } else {
        entries.Add(($"raw_sensor_{rawIdx:D2}.bin", "Frame", strip));
        rawIdx++;
      }
    }

    // EXIF sub-IFD.
    if (reader.ExifIfd != null) {
      entries.Add(("metadata/exif.bin", "Tag", SerializeExif(reader)));
      var makerNote = reader.ExifIfd.Entries.FirstOrDefault(e => e.Tag == DngReader.TagMakerNote);
      if (makerNote != null && makerNote.Count > 0) {
        var bytes = makerNote.Count <= 4
          ? InlineMakerNoteBytes(makerNote, reader.IsBigEndian)
          : reader.ReadBytesAt(makerNote.ValueOrOffset, makerNote.Count);
        if (bytes.Length > 0)
          entries.Add(("metadata/makernote.bin", "Tag", bytes));
      }
    }

    return entries;
  }

  private static byte[] SerializeExif(DngReader reader) {
    if (reader.ExifIfd == null) return Array.Empty<byte>();
    // Emit a small synthetic blob: byte-order + entry count + each entry's raw 12 bytes.
    // This isn't a standalone TIFF — just a metadata dump a triage tool can read.
    using var ms = new MemoryStream();
    ms.WriteByte(reader.IsBigEndian ? (byte)'M' : (byte)'I');
    ms.WriteByte(reader.IsBigEndian ? (byte)'M' : (byte)'I');
    Span<byte> w = stackalloc byte[4];
    System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(w, (ushort)reader.ExifIfd.Entries.Count);
    ms.Write(w.Slice(0, 2));
    foreach (var e in reader.ExifIfd.Entries) {
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
