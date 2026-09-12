#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Registry;

namespace FileFormat.Aomei;

/// <summary>
/// True in-place modifier for AOMEI <c>.adi</c>/<c>.afi</c> containers
/// emitted by <see cref="AomeiWriter"/>. Performs Add / Replace / Remove
/// against the trailing <see cref="AomeiConstants.IndexTypeDataBlock"/>
/// (0x202) <c>BR_IMAGE_INDEX</c> record by appending fresh
/// <c>BR_IMAGE_INDEX_ENTRY_VDB</c> entries (each 0x20 bytes) at the end
/// of the index's entry array.
///
/// <para>
/// <b>On-disk semantic.</b> The shipped layout the writer produces is
/// <code>
/// [ BIFH (0x65C) ]
/// [ INFO records ]
/// [ User-data envelopes (0xF001 type, one per input) ]
/// [ INDEX_TYPE_DATABLOCK BR_IMAGE_INDEX (last record before BIFT) ]
/// [ BIFT (0x674) ]
/// </code>
/// Every byte before the BR_IMAGE_INDEX — every existing user-data
/// envelope — stays byte-identical at its original offset on every
/// mutation. Inside the index, existing VDB entries at offsets
/// <c>[<see cref="AomeiConstants.ShippedIndexEntriesOffset"/>,
/// + oldCount × 0x20)</c> also stay byte-identical; new entries land
/// immediately after. The only patched fields are the index's
/// EntryCount, the index's BR_STANDARD_HEADER Size, the index's
/// BR_STANDARD_HEADER Crc32, and (because the index grew) the BIFT
/// which is re-emitted at the new tail.
/// </para>
///
/// <para>
/// <b>Add</b> (<see cref="Add(Stream, IReadOnlyList{ArchiveInputInfo})"/>):
/// each input becomes a fresh user-data envelope written at the OLD
/// index start offset, then the BR_IMAGE_INDEX is re-laid right after
/// with the existing VDB entries first (byte-identical) and the new
/// VDB entries appended (each with a brand-new <c>RegNo</c>,
/// <c>ImgOffset</c> = new envelope's absolute offset, sizes + CRC).
/// </para>
///
/// <para>
/// <b>Replace</b> (<see cref="Replace"/>): a fresh user-data envelope
/// for the replacement bytes is written at the OLD index offset, then
/// the BR_IMAGE_INDEX is re-laid with the existing entries verbatim
/// plus a fresh entry carrying the SAME <c>RegNo</c> as the target.
/// The reader's latest-entry-wins gate surfaces the new envelope as
/// the live state; the old envelope's bytes stay byte-identical at
/// their original offset.
/// </para>
///
/// <para>
/// <b>Remove</b> (<see cref="Remove"/>): no new envelope is written —
/// the BR_IMAGE_INDEX is re-laid with the existing entries verbatim
/// plus a tombstone entry sharing the target's <c>RegNo</c>. Tombstones
/// encode <c>NewSize = <see cref="AomeiConstants.TombstoneNewSizeSentinel"/>
/// = 0xFFFFFFFF</c> + <c>ImgOffset = 0</c> + <c>OldSize = 0</c> +
/// <c>Crc32 = 0</c> on the wire. The original envelope's bytes survive
/// at their offset (the operation is byte-preserving on payload, not
/// forensic wipe); the reader's latest-wins gate hides the live entry.
/// </para>
///
/// <para>
/// <b>By design.</b> The modifier only operates on containers that
/// already contain a trailing BR_IMAGE_INDEX (i.e. were emitted by this
/// project's writer with at least one user-data input, or had one
/// added by a prior Add call). Foreign AOMEI images and empty
/// containers cause an <see cref="InvalidOperationException"/> — the
/// vendor's BR_IMAGE_INDEX placement is undocumented past the
/// header-layout level so an in-place modify of a real vendor sample
/// would either corrupt the image or produce something the vendor
/// reader rejects silently. See <see cref="AomeiFormatDescriptor"/>
/// for the honest-scope note.
/// </para>
/// </summary>
public static class AomeiInPlaceModifier {

  /// <summary>Appends one fresh VDB entry per input. Each input becomes
  /// a new user-data envelope written at the OLD index start offset;
  /// the BR_IMAGE_INDEX is re-laid right after with the existing
  /// entries first and the new entries appended (RegNo = max-seen + 1
  /// upward).</summary>
  public static void Add(Stream image, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(inputs);
    RequireRwSeekable(image);
    var fileInputs = new List<ArchiveInputInfo>();
    foreach (var i in inputs)
      if (!i.IsDirectory)
        fileInputs.Add(i);
    if (fileInputs.Count == 0) return;

    var state = ScanState(image);
    var nextRegNo = state.NextRegNo;
    var newEntries = new List<BrImageIndexEntryVdb>(state.AllEntries);

    // Envelopes land at the OLD index start offset, contiguously.
    image.Position = state.OldIndexOffset;
    var envelopeOffset = state.OldIndexOffset;
    foreach (var input in fileInputs) {
      var payload = input.ReadContent();
      var envelope = AomeiWriter.BuildUserDataRecord(input.ArchiveName, payload);
      image.Write(envelope, 0, envelope.Length);
      newEntries.Add(new BrImageIndexEntryVdb {
        RegNo = nextRegNo++,
        BlockNo = 0,
        ImgOffset = (ulong)envelopeOffset,
        OldSize = (uint)envelope.Length,
        NewSize = (uint)envelope.Length,
        Crc32 = BrCrc32.Compute(envelope),
      });
      envelopeOffset += envelope.Length;
    }

    WriteIndexAndTail(image, newEntries, envelopeOffset);
  }

  /// <summary>Replaces the live entry whose <see cref="BrImageIndexEntryVdb.RegNo"/>
  /// equals <paramref name="regNo"/>. Writes a fresh user-data envelope
  /// at the OLD index start offset and appends a fresh VDB entry
  /// sharing <paramref name="regNo"/>; the reader's latest-wins gate
  /// surfaces the new envelope as the live state. The old envelope's
  /// bytes stay byte-identical at their original offset.</summary>
  /// <exception cref="FileNotFoundException">When no live entry carries
  /// the supplied <paramref name="regNo"/>.</exception>
  public static void Replace(Stream image, uint regNo, string newName, byte[] newPayload) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(newName);
    ArgumentNullException.ThrowIfNull(newPayload);
    RequireRwSeekable(image);

    var state = ScanState(image);
    if (!state.LiveRegNos.Contains(regNo))
      throw new FileNotFoundException(
        $"Aomei in-place Replace: no live VDB entry with RegNo={regNo} in image.");

    var envelope = AomeiWriter.BuildUserDataRecord(newName, newPayload);
    image.Position = state.OldIndexOffset;
    image.Write(envelope, 0, envelope.Length);

    var newEntries = new List<BrImageIndexEntryVdb>(state.AllEntries) {
      new() {
        RegNo = regNo,
        BlockNo = 0,
        ImgOffset = (ulong)state.OldIndexOffset,
        OldSize = (uint)envelope.Length,
        NewSize = (uint)envelope.Length,
        Crc32 = BrCrc32.Compute(envelope),
      },
    };
    WriteIndexAndTail(image, newEntries, state.OldIndexOffset + envelope.Length);
  }

  /// <summary>Appends a tombstone VDB entry sharing the target's
  /// <paramref name="regNo"/>. The original envelope's bytes survive at
  /// their offset; the reader's latest-wins gate suppresses the live
  /// entry.</summary>
  /// <exception cref="FileNotFoundException">When no live entry carries
  /// the supplied <paramref name="regNo"/>.</exception>
  public static void Remove(Stream image, uint regNo) {
    ArgumentNullException.ThrowIfNull(image);
    RequireRwSeekable(image);

    var state = ScanState(image);
    if (!state.LiveRegNos.Contains(regNo))
      throw new FileNotFoundException(
        $"Aomei in-place Remove: no live VDB entry with RegNo={regNo} in image.");

    var newEntries = new List<BrImageIndexEntryVdb>(state.AllEntries) {
      new() {
        RegNo = regNo,
        BlockNo = 0,
        ImgOffset = 0,
        OldSize = 0,
        NewSize = AomeiConstants.TombstoneNewSizeSentinel,
        Crc32 = 0,
      },
    };
    // Tombstone-only Remove writes no fresh envelope; the index is re-
    // laid in place at its old offset (no envelope shift).
    image.Position = state.OldIndexOffset;
    WriteIndexAndTail(image, newEntries, state.OldIndexOffset);
  }

  // ─── Internals ─────────────────────────────────────────────────────────

  private sealed record ScanResult(
    long OldIndexOffset,
    int OldIndexSize,
    List<BrImageIndexEntryVdb> AllEntries,
    HashSet<uint> LiveRegNos,
    uint NextRegNo);

  private static ScanResult ScanState(Stream image) {
    image.Position = 0;
    var reader = new AomeiReader(image);
    if (reader.DataBlockIndexFileOffset is not { } offset ||
        reader.DataBlockIndexSize is not { } size) {
      throw new InvalidOperationException(
        "Aomei in-place modify requires a container with a trailing " +
        "INDEX_TYPE_DATABLOCK BR_IMAGE_INDEX record (the one emitted by " +
        "AomeiWriter when at least one user-data input is present). " +
        "Empty containers and foreign AOMEI images cannot be modified " +
        "in place — use IArchiveCreatable.Create to produce a fresh " +
        "container instead.");
    }
    var live = new HashSet<uint>();
    foreach (var e in reader.LiveVdbEntries) live.Add(e.RegNo);
    var nextRegNo = 1u;
    foreach (var e in reader.AllVdbEntries)
      if (e.RegNo >= nextRegNo) nextRegNo = e.RegNo + 1;
    return new ScanResult(
      offset,
      size,
      new List<BrImageIndexEntryVdb>(reader.AllVdbEntries),
      live,
      nextRegNo);
  }

  private static void WriteIndexAndTail(Stream image, List<BrImageIndexEntryVdb> entries, long indexOffset) {
    var indexBytes = BrImageIndex.BuildDataBlockRecord(entries);
    image.Position = indexOffset;
    image.Write(indexBytes, 0, indexBytes.Length);
    var tail = BrFileTail.BuildEmpty();
    image.Write(tail, 0, tail.Length);
    image.SetLength(indexOffset + indexBytes.Length + tail.Length);
  }

  private static void RequireRwSeekable(Stream s) {
    if (!s.CanRead || !s.CanWrite || !s.CanSeek)
      throw new ArgumentException(
        "Aomei in-place modify requires a read+write+seekable stream.",
        nameof(s));
  }
}
