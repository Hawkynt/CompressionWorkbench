#pragma warning disable CS1591
namespace FileFormat.Acronis;

/// <summary>
/// High-level read-only listing facade for Acronis classic .tib backups.
/// </summary>
/// <remarks>
/// <para>
/// Walks the record stream described by upstream RE (https://github.com/dennisss/acronis-tib).
/// Surfaces Listing-record entries as file metadata. Does NOT extract file content — the
/// FirstFileMetaRecord/FileMetaA/B/C bridge between a Listing entry's <c>MetaOffset</c> and the
/// per-file RecordIndex is not understood upstream, so the file-to-blob mapping is unavailable.
/// </para>
/// <para>
/// Supported: file-system slices (trailer magic <c>2C 8A E1 94</c>) in Windows volumes.
/// </para>
/// <para>
/// Not supported:
/// </para>
/// <list type="bullet">
///   <item><description>Sector-by-sector slices — variable-length trailer not implemented.</description></item>
///   <item><description>Encrypted backups — record payloads would be cipher-wrapped before deflate.</description></item>
///   <item><description>Multi-volume slices — only single-file .tib slices are walked.</description></item>
///   <item><description>.tibx (Acronis True Image 2020+) — different container.</description></item>
/// </list>
/// </remarks>
public sealed class AcronisReader {
  public AcronisVolumeHeader Header { get; }
  public AcronisSliceTrailer? Trailer { get; }
  public IReadOnlyList<AcronisFileEntry> Entries { get; }
  public IReadOnlyList<AcronisConfigAttribute> ConfigAttributes { get; }

  public AcronisReader(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    this.Header = AcronisVolumeHeader.Read(stream);

    if (this.Header.Version != AcronisVolumeVersion.Windows) {
      // Mac-format .tib uses a wholly different record/box stream. List/Extract not supported here.
      this.Trailer = null;
      this.Entries = [];
      this.ConfigAttributes = [];
      return;
    }

    this.Trailer = AcronisSliceTrailer.TryRead(stream, this.Header);

    var entries = new List<AcronisFileEntry>();
    var configAttrs = new List<AcronisConfigAttribute>();

    if (this.Trailer is { Form: AcronisSliceForm.FileSystem, MetadataOffset: > 0 } t
        && t.MetadataOffset < stream.Length) {
      stream.Position = t.MetadataOffset;
      // Records live between metadataOffset and the start of the trailer payload (the 12-byte
      // file-system trailer that precedes the 48-byte footer).
      const int FileSystemTrailerLength = 12;
      const int FooterLength = 48;
      var recordsEnd = stream.Length - FooterLength - FileSystemTrailerLength;
      var records = AcronisRecordReader.ReadAll(stream, recordsEnd);

      foreach (var rec in records) {
        if (rec.Files is { } files) entries.AddRange(files);
        if (rec.ConfigAttrs is { } attrs) configAttrs.AddRange(attrs);
      }
    }

    this.Entries = entries;
    this.ConfigAttributes = configAttrs;
  }
}
