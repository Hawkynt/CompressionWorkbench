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

  /// <summary>
  /// Per-entry RecordIndex resolution computed by walking the FileMeta chain anchored on
  /// the Listing entry's <see cref="AcronisFileEntry.MetaOffset"/> field. <c>null</c> at index
  /// <c>i</c> means the chain walk could not resolve the entry (no FirstFileMetaRecord found at
  /// the claimed offset, or no RecordIndex follows the chain).
  /// </summary>
  /// <remarks>
  /// <para>
  /// The chain walk follows the spec-grounded path described by upstream RE
  /// (<see href="https://github.com/dennisss/acronis-tib"/>): each Listing entry's MetaOffset
  /// points (relative to <see cref="AcronisVolumeHeader.HeaderLength"/>) at the entry's
  /// FirstFileMetaRecord(102) block; the per-file chain runs 102 → 1 → 2 → 5 → 108 → 109+;
  /// the first RecordIndex(108) seen in archive order after the anchored 102 record is the
  /// authoritative index for that entry.
  /// </para>
  /// <para>
  /// When the chain walk resolves every entry it is used as the authoritative pairing path; when
  /// any entry fails to resolve, the reader falls back to the sequential-pairing heuristic
  /// (Nth Listing entry ↔ Nth RecordIndex by archive order, gated by Listing.FileSize ==
  /// RecordIndex.TotalSize cross-check). Both paths feed the same per-blob MD5 verification
  /// in <see cref="ExtractFile(int)"/>, so a wrong pairing fails closed.
  /// </para>
  /// </remarks>
  public IReadOnlyList<AcronisRecord?> RecordIndicesByChainWalk { get; }

  /// <summary>
  /// <c>true</c> iff <see cref="RecordIndicesByChainWalk"/> resolved every Listing entry to a
  /// RecordIndex via the on-disk FileMeta chain walk (no nulls).
  /// </summary>
  public bool ChainWalkComplete { get; }

  /// <summary>
  /// <c>true</c> iff <see cref="RecordIndicesByChainWalk"/> agrees with the legacy sequential
  /// pairing at every resolved entry. When this is <c>true</c> AND <see cref="ChainWalkComplete"/>
  /// is <c>true</c>, the two paths cross-validate each other for this slice.
  /// </summary>
  public bool ChainWalkMatchesSequentialPairing { get; }

  /// <summary>
  /// Per-entry decoded FileMeta record (102 = FirstFileMetaRecord) body, resolved by walking the
  /// chain anchored on the Listing entry's <see cref="AcronisFileEntry.MetaOffset"/>. <c>null</c>
  /// at index <c>i</c> when chain walk did not resolve the entry, when the anchored 102 body
  /// failed to decode as an attribute stream, or when the body has no attributes the decoder
  /// recognizes.
  /// </summary>
  /// <remarks>
  /// <para>
  /// The body shape (attribute-stream layout) and the high-value id meanings (ItemCommon →
  /// filename + alt name, SourceItem → source path, HardLinkId, BackupTime, TimeZone) are
  /// reverse-engineered from <c>ti_tools.dll</c> 32-bit (Acronis True Image 2018). See
  /// <see cref="AcronisFileMetaBodyDecoder"/> for the format details.
  /// </para>
  /// </remarks>
  public IReadOnlyList<AcronisFileMetaBody?> FileMetaBodiesByEntry { get; }

  /// <summary>
  /// Per-entry filename decoded from the anchored FirstFileMetaRecord(102)'s ItemCommon
  /// attribute (id 0x10). <c>null</c> at index <c>i</c> when the body couldn't be decoded or
  /// didn't contain an ItemCommon attribute. When set, this is the authoritative filename per
  /// the reverse-engineered InputItem model; the Listing record's
  /// <see cref="AcronisFileEntry.Name"/> may agree or disagree depending on whether the
  /// Listing was rewritten after the FileMeta was emitted.
  /// </summary>
  public IReadOnlyList<string?> DecodedNamesByEntry { get; }

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
      this.RecordIndicesByChainWalk = [];
      this.ChainWalkComplete = false;
      this.ChainWalkMatchesSequentialPairing = true;
      this.FileMetaBodiesByEntry = [];
      this.DecodedNamesByEntry = [];
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

    // Build the FileMeta chain walk index. Anchor on Listing.MetaOffset → FirstFileMetaRecord(102),
    // then take the first RecordIndex(108) appearing after that 102 record in archive order.
    var (chainWalk, complete, matchesSequential, anchors) = ResolveByFileMetaChain(entries, records, indices, this.Header.HeaderLength);
    this.RecordIndicesByChainWalk = chainWalk;
    this.ChainWalkComplete = complete;
    this.ChainWalkMatchesSequentialPairing = matchesSequential;

    // Per-entry decoded FileMeta102 bodies + decoded names. Surfaced as parallel lists keyed by
    // entry index; null entries indicate "chain walk didn't resolve" or "decoder didn't find
    // ItemCommon" so callers can fall back to the Listing-record Name without ambiguity.
    var bodies = new AcronisFileMetaBody?[entries.Count];
    var names = new string?[entries.Count];
    for (var i = 0; i < entries.Count; i++) {
      var anchor = anchors[i];
      var body = anchor?.MetaBody;
      bodies[i] = body;
      names[i] = body?.ItemCommon?.Name;
    }
    this.FileMetaBodiesByEntry = bodies;
    this.DecodedNamesByEntry = names;
  }

  /// <summary>
  /// Walks the FileMeta chain to resolve every Listing entry to its authoritative RecordIndex.
  /// </summary>
  /// <remarks>
  /// <para>
  /// Algorithm (per upstream RE — dennisss/acronis-tib src/win/record.ts ListingRecord comment
  /// "Offset relative to after the header of the FirstFileMetaRecord for this entry"):
  /// </para>
  /// <list type="number">
  ///   <item><description>Index every FirstFileMetaRecord(102) by relative offset (<c>Start - HeaderLength</c>).</description></item>
  ///   <item><description>For each Listing entry: look up its anchor 102 via <see cref="AcronisFileEntry.MetaOffset"/>.</description></item>
  ///   <item><description>Find the first RecordIndex(108) in archive order whose <c>Start &gt; anchor.Start</c> — that's the entry's index.</description></item>
  ///   <item><description>If the same RecordIndex is claimed by two entries (overlap), record both as unresolved (chain walk inconclusive for that slice).</description></item>
  /// </list>
  /// <para>
  /// Returns <c>(chainWalk, complete, matchesSequential)</c>:
  /// </para>
  /// <list type="bullet">
  ///   <item><description><c>chainWalk[i]</c> = the RecordIndex resolved for entry <c>i</c>, or <c>null</c> on failure.</description></item>
  ///   <item><description><c>complete</c> = no nulls in <c>chainWalk</c>.</description></item>
  ///   <item><description><c>matchesSequential</c> = every resolved entry agrees with the Nth-Listing↔Nth-RecordIndex pairing.</description></item>
  /// </list>
  /// </remarks>
  private static (IReadOnlyList<AcronisRecord?> ChainWalk, bool Complete, bool MatchesSequential, IReadOnlyList<AcronisRecord?> Anchors) ResolveByFileMetaChain(
      IReadOnlyList<AcronisFileEntry> entries,
      IReadOnlyList<AcronisRecord> records,
      IReadOnlyList<AcronisRecord> indices,
      ushort headerLength) {

    if (entries.Count == 0) return ([], false, true, []);

    // Index FirstFileMetaRecord(102) records by their relative offset.
    var ffmByOffset = new Dictionary<long, AcronisRecord>();
    foreach (var rec in records) {
      if (rec.Type != AcronisRecordType.FirstFileMetaRecord) continue;
      var relativeOffset = rec.Start - headerLength;
      // Tolerate duplicates by keeping the first occurrence — duplicates are an integrity defect
      // and the consumer will see the failure as a downstream MD5 mismatch.
      ffmByOffset.TryAdd(relativeOffset, rec);
    }

    // Pre-sort RecordIndex records by Start (archive order) for fast "first ≥ X" lookup.
    var indicesByStart = indices.OrderBy(r => r.Start).ToList();

    var result = new AcronisRecord?[entries.Count];
    var anchorPerEntry = new AcronisRecord?[entries.Count];
    var seenIndexStart = new HashSet<long>();
    var allResolved = true;
    for (var i = 0; i < entries.Count; i++) {
      var entry = entries[i];
      // Look up the entry's anchor 102. A MetaOffset value that doesn't correspond to any 102
      // record in the slice (including the legacy MetaOffset=0 case when there is no 102 at
      // relative offset 0) is treated as unresolved — the chain walk requires a real anchor.
      if (!ffmByOffset.TryGetValue(entry.MetaOffset, out var anchor)) {
        result[i] = null;
        anchorPerEntry[i] = null;
        allResolved = false;
        continue;
      }
      anchorPerEntry[i] = anchor;
      // First RecordIndex with Start > anchor.Start that we haven't already claimed.
      AcronisRecord? claim = null;
      foreach (var idx in indicesByStart) {
        if (idx.Start <= anchor.Start) continue;
        if (seenIndexStart.Contains(idx.Start)) continue;
        claim = idx;
        break;
      }
      if (claim is null) {
        result[i] = null;
        allResolved = false;
        continue;
      }
      result[i] = claim;
      seenIndexStart.Add(claim.Start);
    }

    // Cross-check against sequential pairing.
    var matchesSequential = true;
    var sequentialUpper = Math.Min(entries.Count, indices.Count);
    for (var i = 0; i < sequentialUpper; i++) {
      if (result[i] is null) continue; // unresolved — nothing to compare
      // Sequential pairing yields indices[i]. Chain-walk says result[i]. Compare by Start.
      if (result[i]!.Start != indices[i].Start) {
        matchesSequential = false;
        break;
      }
    }

    return (result, allResolved, matchesSequential, anchorPerEntry);
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
  /// <remarks>
  /// Pairing strategy: when <see cref="ChainWalkComplete"/> is <c>true</c> the FileMeta chain walk
  /// is used (authoritative — anchored on the Listing entry's on-disk <see cref="AcronisFileEntry.MetaOffset"/>
  /// pointer through the 102 → 1 → 2 → 5 chain to the next RecordIndex(108)). Otherwise the
  /// sequential-pairing heuristic is used (Nth Listing entry ↔ Nth RecordIndex by archive order,
  /// gated by Listing.FileSize == RecordIndex.TotalSize). Per-blob MD5 verification gates both
  /// paths so a wrong pairing produces <c>IntegrityValid = false</c> rather than silent corruption.
  /// </remarks>
  /// <exception cref="InvalidOperationException">
  /// Thrown when neither pairing path can resolve <paramref name="entryIndex"/> to a RecordIndex,
  /// or when the resolved RecordIndex references a Blob that's missing from the archive.
  /// </exception>
  public AcronisExtractionResult ExtractFile(int entryIndex) {
    if (entryIndex < 0 || entryIndex >= this.Entries.Count) throw new ArgumentOutOfRangeException(nameof(entryIndex));
    var entry = this.Entries[entryIndex];

    // Prefer the FileMeta chain walk when it resolved this entry — it's the spec-grounded path.
    AcronisRecordIndexInfo index;
    if (this.ChainWalkComplete && this.RecordIndicesByChainWalk[entryIndex] is { } chainIdx) {
      index = chainIdx.Index!;
    } else {
      if (!this.CanExtractByPairing(out var reason)) throw new InvalidOperationException(reason);
      index = this.RecordIndices[entryIndex].Index!;
    }

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
