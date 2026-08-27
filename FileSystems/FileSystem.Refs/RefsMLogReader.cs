#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileSystem.Refs;

internal sealed record RefsLogfileInformation(
  ulong SourceObjectId,
  ulong DataStartPhysicalLcn,
  ulong DataEndPhysicalLcn,
  ulong DataClusterCount,
  ulong Control0PhysicalLcn,
  ulong Control1PhysicalLcn) {

  public IReadOnlyList<ulong> ControlPhysicalLcns => [this.Control0PhysicalLcn, this.Control1PhysicalLcn];

  /// <summary>
  /// Reads the primary OID 0x9 Logfile Information table and falls back to its
  /// independently checksummed OID 0xA duplicate. The key=1 value describes
  /// the MLog data range at +0x10/+0x18/+0x20 and control PLCNs at +0x28/+0x30.
  /// </summary>
  public static bool TryRead(RefsMetadataReader metadata, out RefsLogfileInformation information) {
    ArgumentNullException.ThrowIfNull(metadata);
    foreach (var oid in new[] { 0x09UL, 0x0AUL }) {
      if (!TryFindObjectRoot(metadata, oid, out var objectRoot)) continue;
      try {
        foreach (var row in metadata.WalkTree(objectRoot, virtualAddresses: true)) {
          if (!IsKeyOne(row.Key) || row.Value.Length < 0x38) continue;

          var start = BinaryPrimitives.ReadUInt64LittleEndian(row.Value.AsSpan(0x10, 8));
          var end = BinaryPrimitives.ReadUInt64LittleEndian(row.Value.AsSpan(0x18, 8));
          var size = BinaryPrimitives.ReadUInt64LittleEndian(row.Value.AsSpan(0x20, 8));
          var control0 = BinaryPrimitives.ReadUInt64LittleEndian(row.Value.AsSpan(0x28, 8));
          var control1 = BinaryPrimitives.ReadUInt64LittleEndian(row.Value.AsSpan(0x30, 8));
          if (!IsPlausible(metadata, start, end, size, control0, control1)) continue;

          information = new RefsLogfileInformation(oid, start, end, size, control0, control1);
          return true;
        }
      } catch (InvalidDataException) {
        // Primary and duplicate are intentionally independent. A damaged first
        // copy must not prevent the duplicate from being considered.
      }
    }

    information = default!;
    return false;
  }

  private static bool TryFindObjectRoot(
      RefsMetadataReader metadata,
      ulong wantedOid,
      out RefsPageReference root) {
    try {
      foreach (var row in metadata.WalkRoot(0)) {
        if (row.Key.Length < 16 || row.Value.Length < 0x20 + metadata.PageReferenceSize) continue;
        var oid = BinaryPrimitives.ReadUInt64LittleEndian(row.Key.AsSpan(8, 8));
        if (oid != wantedOid) continue;
        var candidate = RefsPageReference.Parse(row.Value.AsSpan(0x20));
        if (candidate.Lcns.Count == 0) continue;
        root = candidate;
        return true;
      }
    } catch (InvalidDataException) {
      // Return false; caller will try the duplicate OID.
    }

    root = RefsPageReference.Empty;
    return false;
  }

  private static bool IsKeyOne(ReadOnlySpan<byte> key) {
    if (key.Length < 8) return false;
    // Schema 0xe090 keys observed for this system table are scalar 1. Accept a
    // leading/trailing u64 representation only when every other byte is zero;
    // this remains fail-closed for an unrelated row that merely contains 1.
    if (BinaryPrimitives.ReadUInt64LittleEndian(key[..8]) == 1
        && AllZero(key[8..])) return true;
    return BinaryPrimitives.ReadUInt64LittleEndian(key[^8..]) == 1
      && AllZero(key[..^8]);
  }

  private static bool IsPlausible(
      RefsMetadataReader metadata,
      ulong start,
      ulong end,
      ulong size,
      ulong control0,
      ulong control1) {
    if (start == 0 || end <= start || size != end - start) return false;
    if (control0 == 0 || control1 == 0 || control0 == control1) return false;
    if (start > metadata.Header.TotalClusters || end > metadata.Header.TotalClusters) return false;
    if (control0 >= metadata.Header.TotalClusters || control1 >= metadata.Header.TotalClusters) return false;
    return true;
  }

  private static bool AllZero(ReadOnlySpan<byte> bytes) {
    foreach (var value in bytes)
      if (value != 0) return false;
    return true;
  }
}

internal sealed record RefsMLogState(
  RefsLogfileInformation Information,
  RefsMLogControlRecord ActiveControl,
  ulong ActiveControlPhysicalLcn,
  IReadOnlyList<RefsMLogDataRecord> ValidDataRecords) {

  public ulong NextLsn {
    get {
      if (this.ValidDataRecords.Count == 0) return checked(this.ActiveControl.OldestLsn + 1);
      return checked(this.ValidDataRecords.Max(r => r.Lsn) + 1);
    }
  }
}

/// <summary>
/// Opens the native ReFS MLog from system OID 0x9/0xA, validates both control
/// slots against the table's physical ranges, chooses the newest control by
/// sequence/generation, and scans every 4 KiB record in the advertised ring.
/// This is read-only until the log checksum/emission primitive is proven.
/// </summary>
internal static class RefsMLogReader {
  public static bool TryOpen(Stream image, RefsMetadataReader metadata, out RefsMLogState state) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(metadata);
    if (!image.CanRead || !image.CanSeek)
      throw new ArgumentException("ReFS MLog reading requires a readable, seekable stream.", nameof(image));
    if (!RefsLogfileInformation.TryRead(metadata, out var information)) {
      state = default!;
      return false;
    }

    var controls = new List<(ulong Lcn, RefsMLogControlRecord Record)>();
    foreach (var lcn in information.ControlPhysicalLcns.Distinct()) {
      if (!TryReadLogBlock(image, metadata.ClusterSize, lcn, out var block)) continue;
      if (!RefsMLogCodec.TryParseControlRecord(block, out var control)) continue;
      if (control.DataStartPhysicalLcn != information.DataStartPhysicalLcn
          || control.DataEndPhysicalLcn != information.DataEndPhysicalLcn) continue;
      controls.Add((lcn, control));
    }
    if (controls.Count == 0) {
      state = default!;
      return false;
    }

    var active = controls.MaxBy(c => (c.Record.Generation, c.Record.Sequence, c.Record.HeaderSequence));
    var records = new List<RefsMLogDataRecord>();
    foreach (var offset in RefsMLogCodec.EnumerateDataBlockOffsets(active.Record, metadata.ClusterSize)) {
      if (offset < 0 || offset > image.Length - RefsMLogCodec.LogBlockSize) continue;
      var block = new byte[RefsMLogCodec.LogBlockSize];
      image.Position = offset;
      image.ReadExactly(block);
      if (!RefsMLogCodec.TryParseDataRecord(block, out var record)) continue;
      if (record.FormatMagic != active.Record.FormatMagic) continue;
      records.Add(record);
    }

    records.Sort((a, b) => a.Lsn.CompareTo(b.Lsn));
    state = new RefsMLogState(information, active.Record, active.Lcn, records);
    return true;
  }

  private static bool TryReadLogBlock(
      Stream image,
      int clusterSize,
      ulong physicalLcn,
      out byte[] block) {
    block = new byte[RefsMLogCodec.LogBlockSize];
    var offset = checked((long)physicalLcn * clusterSize);
    if (offset < 0 || offset > image.Length - block.Length) return false;
    image.Position = offset;
    image.ReadExactly(block);
    return true;
  }
}
