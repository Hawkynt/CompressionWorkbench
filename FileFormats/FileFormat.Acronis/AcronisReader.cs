#pragma warning disable CS1591
using System.Security.Cryptography;

namespace FileFormat.Acronis;

/// <summary>
/// High-level read-only facade for Acronis classic .tib backups.
/// </summary>
/// <remarks>
/// <para>
/// Walks the record stream described by upstream RE (https://github.com/dennisss/acronis-tib).
/// Surfaces Listing-record entries as file metadata, and bridges them to file content via the
/// (fully parsed) RecordIndex(108) → Blob(109) handle chain.
/// </para>
/// <para>
/// Per-file FileMeta records (102/1/2/5) sit between the Listing entry's <c>MetaOffset</c> and
/// the file's RecordIndex. Their bodies remain undocumented in every public source surveyed
/// (dennisss/acronis-tib, all 7 forks, TIB-ShellEx is a repackage of Acronis's own closed-source
/// DLLs, MultiExtractor + R-Studio are closed-source commercial, no academic forensic paper
/// covers this format, no Russian-language RE write-up exists past the dennisss reference).
/// Their framing (1-byte type + raw deflate body + 4-byte trailing checksum) IS known, so we can
/// walk past them, surface them as opaque records, and reach the next RecordIndex(108).
/// </para>
/// <para>
/// Listing entry → RecordIndex pairing: upstream comments the MetaOffset as
/// <em>"Offset relative to after the header of the FirstFileMetaRecord ... Reading sequential"</em>.
/// We therefore pair the Nth Listing entry with the Nth RecordIndex in archive order — the
/// natural format invariant for backup archives that emit one (FirstFileMetaRecord → … →
/// RecordIndex → Blob+) chain per file in Listing order. If a file's reconstructed MD5 disagrees
/// with the RecordIndex handle hash we surface that as <see cref="AcronisExtractionResult.IntegrityValid"/> = false
/// so callers can detect a mismatch; we never silently emit wrong data.
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

  /// <summary>All records walked from the metadata stream (in archive order).</summary>
  public IReadOnlyList<AcronisRecord> Records { get; }

  /// <summary>
  /// RecordIndex records (type 108) in archive order. The Nth element corresponds to the Nth
  /// entry in <see cref="Entries"/> per the sequential-pairing assumption (see class remarks).
  /// </summary>
  public IReadOnlyList<AcronisRecord> RecordIndices { get; }

  /// <summary>
  /// Per-file metadata records (102/1/2/5) surfaced as opaque diagnostic blobs.
  /// Body layouts are undocumented across every public source surveyed.
  /// </summary>
  public IReadOnlyList<AcronisRecord> FileMetaRecords { get; }

  private readonly Stream _stream;
  // recordOffset (relative to end of header) → Blob record (for fast lookup during extraction).
  private readonly Dictionary<long, AcronisRecord> _blobsByRecordOffset;

  public AcronisReader(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    this._stream = stream;
    this.Header = AcronisVolumeHeader.Read(stream);

    if (this.Header.Version != AcronisVolumeVersion.Windows) {
      // Mac-format .tib uses a wholly different record/box stream. List/Extract not supported here.
      this.Trailer = null;
      this.Entries = [];
      this.ConfigAttributes = [];
      this.Records = [];
      this.RecordIndices = [];
      this.FileMetaRecords = [];
      this._blobsByRecordOffset = [];
      return;
    }

    this.Trailer = AcronisSliceTrailer.TryRead(stream, this.Header);

    var entries = new List<AcronisFileEntry>();
    var configAttrs = new List<AcronisConfigAttribute>();
    var records = new List<AcronisRecord>();
    var indices = new List<AcronisRecord>();
    var metas = new List<AcronisRecord>();
    var blobMap = new Dictionary<long, AcronisRecord>();

    if (this.Trailer is { Form: AcronisSliceForm.FileSystem, MetadataOffset: > 0 } t
        && t.MetadataOffset < stream.Length) {
      stream.Position = t.MetadataOffset;
      // Records live between metadataOffset and the start of the trailer payload (the 12-byte
      // file-system trailer that precedes the 48-byte footer).
      const int FileSystemTrailerLength = 12;
      const int FooterLength = 48;
      var recordsEnd = stream.Length - FooterLength - FileSystemTrailerLength;
      records = AcronisRecordReader.ReadAll(stream, recordsEnd);

      foreach (var rec in records) {
        if (rec.Files is { } files) entries.AddRange(files);
        if (rec.ConfigAttrs is { } attrs) configAttrs.AddRange(attrs);
        switch (rec.Type) {
          case AcronisRecordType.RecordIndex:
            indices.Add(rec);
            break;
          case AcronisRecordType.FirstFileMetaRecord:
          case AcronisRecordType.FileMetaA:
          case AcronisRecordType.FileMetaB:
          case AcronisRecordType.FileMetaC:
            metas.Add(rec);
            break;
          case AcronisRecordType.Blob:
            // recordOffset key = absolute Start - HeaderLength, mirroring the upstream definition.
            blobMap[rec.Start - this.Header.HeaderLength] = rec;
            break;
        }
      }
    }

    this.Entries = entries;
    this.ConfigAttributes = configAttrs;
    this.Records = records;
    this.RecordIndices = indices;
    this.FileMetaRecords = metas;
    this._blobsByRecordOffset = blobMap;
  }

  /// <summary>
  /// Returns <c>true</c> when the slice contains at least as many RecordIndex(108) records as
  /// Listing entries and every entry size matches its corresponding RecordIndex
  /// <see cref="AcronisRecordIndexInfo.TotalSize"/>. Failure of either condition indicates the
  /// sequential-pairing assumption (see class remarks) does NOT hold for this slice and
  /// extraction should be treated as unverifiable.
  /// </summary>
  public bool CanExtractByPairing(out string? reason) {
    if (this.Entries.Count == 0) {
      reason = "Slice has no listed file entries.";
      return false;
    }
    if (this.RecordIndices.Count < this.Entries.Count) {
      reason = $"Slice has {this.Entries.Count} listed entries but only {this.RecordIndices.Count} RecordIndex(108) records — cannot pair sequentially.";
      return false;
    }
    for (var i = 0; i < this.Entries.Count; i++) {
      var entrySize = this.Entries[i].FileSize;
      var indexSize = this.RecordIndices[i].Index?.TotalSize ?? -1;
      if (entrySize != indexSize) {
        reason = $"Entry[{i}] size {entrySize} != RecordIndex[{i}].TotalSize {indexSize} — sequential pairing rejected.";
        return false;
      }
    }
    reason = null;
    return true;
  }

  /// <summary>
  /// Extracts the file content for the entry at <paramref name="entryIndex"/> by walking the
  /// paired RecordIndex's handles and decompressing each referenced Blob.
  /// </summary>
  /// <exception cref="InvalidOperationException">
  /// Thrown when the sequential-pairing assumption cannot be verified (entry size mismatch,
  /// insufficient RecordIndex count, or referenced Blob missing from the archive).
  /// </exception>
  public AcronisExtractionResult ExtractFile(int entryIndex) {
    if (entryIndex < 0 || entryIndex >= this.Entries.Count) throw new ArgumentOutOfRangeException(nameof(entryIndex));
    if (!this.CanExtractByPairing(out var reason)) throw new InvalidOperationException(reason);
    var entry = this.Entries[entryIndex];
    var index = this.RecordIndices[entryIndex].Index!;

    // Concatenate fragments in StartOffset order to allow out-of-order handles in the index.
    var sortedHandles = index.Handles.OrderBy(h => h.StartOffset).ToList();
    var output = new byte[entry.FileSize];
    var integrityValid = true;

    foreach (var handle in sortedHandles) {
      if (!this._blobsByRecordOffset.TryGetValue(handle.RecordOffset, out var blob))
        throw new InvalidOperationException(
          $"Acronis: RecordIndex handle references Blob at recordOffset 0x{handle.RecordOffset:X8} but no Blob with that offset was found in the archive.");
      var blobData = blob.Payload ?? throw new InvalidOperationException("Acronis: Blob record has no decoded payload.");

      // MD5 check — surface mismatches without aborting (caller decides).
      var actualMd5 = MD5.HashData(blobData);
      if (!actualMd5.AsSpan().SequenceEqual(handle.Md5)) integrityValid = false;

      var destStart = handle.StartOffset;
      var copyLen = blobData.Length;
      if (destStart + copyLen > entry.FileSize) copyLen = (int)Math.Max(0L, entry.FileSize - destStart);
      if (destStart < 0 || destStart >= entry.FileSize) continue;
      Buffer.BlockCopy(blobData, 0, output, (int)destStart, copyLen);
    }

    return new AcronisExtractionResult(output, integrityValid);
  }
}

/// <summary>Outcome of an <see cref="AcronisReader.ExtractFile"/> call.</summary>
/// <param name="Data">The reconstructed file content (length = entry's <c>FileSize</c>).</param>
/// <param name="IntegrityValid">
/// <c>true</c> iff every blob's MD5 matched the corresponding RecordIndex handle hash. When
/// <c>false</c>, the data was still concatenated but the integrity check failed, so the caller
/// should treat the result as suspect (potentially indicating a wrong sequential pairing).
/// </param>
public sealed record AcronisExtractionResult(byte[] Data, bool IntegrityValid);
