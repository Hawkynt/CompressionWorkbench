#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileSystem.Refs;

internal enum RefsRedoOpcode : uint {
  OpenTableFromTablePath = 0x00,
  InsertRow = 0x01,
  DeleteRow = 0x02,
  UpdateRow = 0x03,
  UpdateDataWithRoot = 0x04,
  ReparentTable = 0x05,
  Allocate = 0x06,
  Free = 0x07,
  SetRangeState = 0x08,
  SetSharedRangeState = 0x09,
  DuplicateExtents = 0x0A,
  ModifyStreamExtent = 0x0B,
  StripAllChecksums = 0x0C,
  SetIntegrityInformation = 0x0D,
  SetParentId = 0x0E,
  DeleteTable = 0x0F,
  SetObjectRecordPayload = 0x10,
  AddSchema = 0x11,
  MoveContainer = 0x12,
  AddContainer = 0x13,
  MoveContainerVariant1 = 0x14,
  MoveContainerVariant2 = 0x15,
  SetRangeStateVariant = 0x16,
  ReservedUnhandled = 0x17,
  ContainerCompaction = 0x18,
  DeleteCompressionUnitOffsets = 0x19,
  AddCompressionUnitOffsets = 0x1A,
  GhostExtents = 0x1B,
  CompactionUnreserve = 0x1C,
  UnlinkParentObjectId = 0x1D,
  PrepareEntryForMerge = 0x1E,
  UpdateStreamSummary = 0x1F,
  UpdateStreamUserPayload = 0x20,
  StreamPersistFastRunInsertion = 0x21,
  TableSetSummaryUpdate = 0x22,
  TableSetShadowTreeUpdate = 0x23,
  TableSetCommitMerge = 0x24,
  TableSetCallback08 = 0x25,
  TableSetStrongRefMerge = 0x26,
  SetDefaultCompressionParameters = 0x27,
  BreakWeakReferences = 0x28,
  DuplicateCluster = 0x29,
  ChangeRangeEncryptedState = 0x2A,
  TableSetCallback18 = 0x2B,
}

[Flags]
internal enum RefsRedoFlags : uint {
  None = 0,
  TransactionStart = 1 << 0,
  // Bit 1 is part of the record flags field, but observed v3.x transactions
  // leave it clear; durable commit lives at the log-core/checkpoint boundary.
  CommitMarker = 1 << 1,
  Special = 1 << 16,
}

internal sealed record RefsRedoRecord(
  RefsRedoOpcode Opcode,
  uint TableKeyPathLength,
  uint ValueComponentCount,
  ulong ObjectId,
  RefsRedoFlags Flags,
  byte[] HeaderTemplate,
  byte[] Payload) {

  public const int HeaderSize = 0x38;

  public byte[] Serialize() {
    if (this.HeaderTemplate.Length is not (0 or HeaderSize))
      throw new InvalidOperationException("ReFS redo header template must be empty or exactly 0x38 bytes.");

    var result = new byte[checked(HeaderSize + this.Payload.Length)];
    if (this.HeaderTemplate.Length == HeaderSize) this.HeaderTemplate.CopyTo(result, 0);
    BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(0x00, 4), checked((uint)result.Length));
    BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(0x04, 4), (uint)this.Opcode);
    BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(0x08, 4), this.TableKeyPathLength);
    BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(0x10, 4), this.ValueComponentCount);
    BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(0x20, 8), this.ObjectId);
    BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(0x2C, 4), (uint)this.Flags);
    this.Payload.CopyTo(result, HeaderSize);
    return result;
  }

  public static RefsRedoRecord Create(
      RefsRedoOpcode opcode,
      ulong objectId,
      ReadOnlySpan<byte> payload,
      uint tableKeyPathLength = 0,
      uint valueComponentCount = 0,
      RefsRedoFlags flags = RefsRedoFlags.None)
    => new(
      opcode,
      tableKeyPathLength,
      valueComponentCount,
      objectId,
      flags,
      new byte[HeaderSize],
      payload.ToArray());
}

/// <summary>
/// Codec for the layer-3 _SmsRedoHeader and layer-4 _SmsRedoRecord sequence.
/// It intentionally treats opcode-specific payload components as opaque until
/// each opcode's key/value grammar is independently decoded.
/// </summary>
internal static class RefsRedoCodec {
  private const int BlockHeaderSize = 8;

  public static IReadOnlyList<RefsRedoRecord> ParseBlock(ReadOnlySpan<byte> block) {
    if (block.Length < BlockHeaderSize)
      throw new InvalidDataException("ReFS redo block is shorter than its header.");

    var totalSize = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(block[0x00..0x04]));
    var firstRecordOffset = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(block[0x04..0x08]));
    if (totalSize < BlockHeaderSize || totalSize > block.Length)
      throw new InvalidDataException("ReFS redo block total size lies outside its containing log record.");
    if (firstRecordOffset < BlockHeaderSize || firstRecordOffset > totalSize)
      throw new InvalidDataException("ReFS redo block first-record offset is malformed.");

    var result = new List<RefsRedoRecord>();
    var cursor = firstRecordOffset;
    while (totalSize - cursor >= RefsRedoRecord.HeaderSize) {
      var size = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(block.Slice(cursor, 4)));
      if (size == 0) break;
      if (size < RefsRedoRecord.HeaderSize || size > totalSize - cursor)
        throw new InvalidDataException("ReFS redo record size exceeds the remaining redo block.");

      var bytes = block.Slice(cursor, size);
      var opcode = BinaryPrimitives.ReadUInt32LittleEndian(bytes[0x04..0x08]);
      result.Add(new RefsRedoRecord(
        (RefsRedoOpcode)opcode,
        BinaryPrimitives.ReadUInt32LittleEndian(bytes[0x08..0x0C]),
        BinaryPrimitives.ReadUInt32LittleEndian(bytes[0x10..0x14]),
        BinaryPrimitives.ReadUInt64LittleEndian(bytes[0x20..0x28]),
        (RefsRedoFlags)BinaryPrimitives.ReadUInt32LittleEndian(bytes[0x2C..0x30]),
        bytes[..RefsRedoRecord.HeaderSize].ToArray(),
        bytes[RefsRedoRecord.HeaderSize..].ToArray()));
      cursor += size;
    }

    return result;
  }

  public static byte[] SerializeBlock(IReadOnlyList<RefsRedoRecord> records) {
    ArgumentNullException.ThrowIfNull(records);
    var serialized = new byte[records.Count][];
    var total = BlockHeaderSize;
    for (var i = 0; i < records.Count; ++i) {
      serialized[i] = records[i].Serialize();
      total = checked(total + serialized[i].Length);
    }

    if (total > RefsMLogCodec.LogBlockSize)
      throw new InvalidOperationException("A ReFS redo transaction must fit in one 4 KiB MLog block.");

    var result = new byte[total];
    BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(0x00, 4), checked((uint)total));
    BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(0x04, 4), BlockHeaderSize);
    var cursor = BlockHeaderSize;
    foreach (var record in serialized) {
      record.CopyTo(result, cursor);
      cursor += record.Length;
    }
    return result;
  }
}

internal sealed record RefsMLogDataRecord(
  uint FormatMagic,
  ulong Lsn,
  ulong PreviousLsn,
  ulong EntryChecksum,
  int EntryHeaderOffset,
  int PayloadOffset,
  IReadOnlyList<RefsRedoRecord> RedoRecords);

internal sealed record RefsMLogControlRecord(
  uint FormatMagic,
  byte[] HeaderUuid,
  ulong HeaderSequence,
  uint ValidationChecksum,
  int EntryHeaderOffset,
  int PayloadOffset,
  ulong Sequence,
  ulong DataStartPhysicalLcn,
  ulong DataEndPhysicalLcn,
  ulong OldestLsn,
  ulong Generation,
  ulong WriteCounter,
  ulong TotalEntries,
  byte[] ControlUuid) {

  public ulong DataClusterCount
    => this.DataEndPhysicalLcn >= this.DataStartPhysicalLcn
      ? checked(this.DataEndPhysicalLcn - this.DataStartPhysicalLcn)
      : 0;

  public bool UuidsMatch => this.HeaderUuid.AsSpan().SequenceEqual(this.ControlUuid);
}

/// <summary>
/// Reads the LogCore envelope used by ReFS MLog control and data records. Data
/// redo payloads are parsed; control payloads expose the physical circular-ring
/// bounds and sequence/generation state required by a future native writer.
/// The log's XOR-fold integrity algorithm is deliberately still separate from
/// framing so a writer cannot accidentally publish an unchecked synthetic page.
/// </summary>
internal static class RefsMLogCodec {
  public const int LogBlockSize = 4096;
  private const int Layer1MinimumSize = 0x78;
  private const int ClassicEntryHeaderSize = 0x38;

  public static bool TryParseDataRecord(ReadOnlySpan<byte> block, out RefsMLogDataRecord record) {
    record = default!;
    if (!TryGetEntry(block, expectedRecordType: 2, out var common)) return false;
    if (common.PayloadLength < 8) return false;

    IReadOnlyList<RefsRedoRecord> redo;
    try {
      redo = RefsRedoCodec.ParseBlock(block.Slice(common.AbsolutePayloadOffset, common.PayloadLength));
    } catch (InvalidDataException) {
      return false;
    }

    record = new RefsMLogDataRecord(
      common.FormatMagic,
      BinaryPrimitives.ReadUInt64LittleEndian(block[0x28..0x30]),
      BinaryPrimitives.ReadUInt64LittleEndian(block[0x30..0x38]),
      common.EntryChecksum,
      common.EntryHeaderOffset,
      common.PayloadOffset,
      redo);
    return true;
  }

  public static bool TryParseControlRecord(ReadOnlySpan<byte> block, out RefsMLogControlRecord record) {
    record = default!;
    if (!TryGetEntry(block, expectedRecordType: 1, out var common)) return false;
    if (common.PayloadLength < 0x60) return false;

    var payload = block.Slice(common.AbsolutePayloadOffset, common.PayloadLength);
    var dataStart = BinaryPrimitives.ReadUInt64LittleEndian(payload[0x08..0x10]);
    var dataEnd = BinaryPrimitives.ReadUInt64LittleEndian(payload[0x10..0x18]);
    if (dataEnd <= dataStart) return false;

    var headerUuid = block[0x10..0x20].ToArray();
    var controlUuid = payload[0x50..0x60].ToArray();
    if (!headerUuid.AsSpan().SequenceEqual(controlUuid)) return false;

    record = new RefsMLogControlRecord(
      common.FormatMagic,
      headerUuid,
      BinaryPrimitives.ReadUInt64LittleEndian(block[0x20..0x28]),
      block.Length >= 0x84 ? BinaryPrimitives.ReadUInt32LittleEndian(block[0x80..0x84]) : 0,
      common.EntryHeaderOffset,
      common.PayloadOffset,
      BinaryPrimitives.ReadUInt64LittleEndian(payload[0x00..0x08]),
      dataStart,
      dataEnd,
      BinaryPrimitives.ReadUInt64LittleEndian(payload[0x18..0x20]),
      BinaryPrimitives.ReadUInt64LittleEndian(payload[0x20..0x28]),
      BinaryPrimitives.ReadUInt64LittleEndian(payload[0x38..0x40]),
      BinaryPrimitives.ReadUInt64LittleEndian(payload[0x48..0x50]),
      controlUuid);
    return true;
  }

  /// <summary>
  /// Enumerates every 4 KiB log block in a control-record-advertised data area.
  /// MLog keeps 4 KiB addressing even when the ReFS volume cluster size is 64K.
  /// </summary>
  public static IEnumerable<long> EnumerateDataBlockOffsets(
      RefsMLogControlRecord control,
      int volumeClusterSize) {
    ArgumentNullException.ThrowIfNull(control);
    if (volumeClusterSize <= 0 || (volumeClusterSize & (volumeClusterSize - 1)) != 0)
      throw new ArgumentOutOfRangeException(nameof(volumeClusterSize));
    if (volumeClusterSize % LogBlockSize != 0)
      throw new NotSupportedException("ReFS MLog requires the volume cluster size to be a multiple of 4 KiB.");

    var start = checked((long)control.DataStartPhysicalLcn * volumeClusterSize);
    var end = checked((long)control.DataEndPhysicalLcn * volumeClusterSize);
    if (end <= start) throw new InvalidDataException("ReFS MLog data-area bounds are empty or reversed.");
    for (var offset = start; offset <= end - LogBlockSize; offset = checked(offset + LogBlockSize))
      yield return offset;
  }

  private readonly record struct EntryInfo(
    uint FormatMagic,
    ulong EntryChecksum,
    int EntryHeaderOffset,
    int PayloadOffset,
    int AbsolutePayloadOffset,
    int PayloadLength);

  private static bool TryGetEntry(
      ReadOnlySpan<byte> block,
      uint expectedRecordType,
      out EntryInfo info) {
    info = default;
    if (block.Length < 0xB0 || !block[..4].SequenceEqual("MLog"u8)) return false;
    if (BinaryPrimitives.ReadUInt32LittleEndian(block[0x08..0x0C]) != 1) return false;
    if (BinaryPrimitives.ReadUInt32LittleEndian(block[0x0C..0x10]) != LogBlockSize) return false;

    var entryOffset = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(block[0x54..0x58]));
    if (entryOffset < Layer1MinimumSize || entryOffset + ClassicEntryHeaderSize > block.Length) return false;
    var entry = block[entryOffset..];
    var payloadLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(entry[0x20..0x24]));
    var payloadOffset = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(entry[0x28..0x2C]));
    var recordType = BinaryPrimitives.ReadUInt32LittleEndian(entry[0x30..0x34]);
    if (recordType != expectedRecordType || payloadOffset < ClassicEntryHeaderSize || payloadOffset > entry.Length)
      return false;
    var absolutePayloadOffset = checked(entryOffset + payloadOffset);
    if (payloadLength < 0 || absolutePayloadOffset < 0 || absolutePayloadOffset > block.Length
        || payloadLength > block.Length - absolutePayloadOffset)
      return false;

    info = new EntryInfo(
      BinaryPrimitives.ReadUInt32LittleEndian(block[0x04..0x08]),
      BinaryPrimitives.ReadUInt64LittleEndian(entry[0x08..0x10]),
      entryOffset,
      payloadOffset,
      absolutePayloadOffset,
      payloadLength);
    return true;
  }
}
