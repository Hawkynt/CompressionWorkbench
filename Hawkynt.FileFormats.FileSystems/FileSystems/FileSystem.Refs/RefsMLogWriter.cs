#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileSystem.Refs;

internal enum RefsMLogXorFoldKind {
  WholeBlockQwords,
  EntryRegionQwords,
  DeclaredEntryQwords,
  PayloadQwords,
  WholeBlockDwordLanes,
  EntryRegionDwordLanes,
}

/// <summary>
/// ReFS MLog stores an 8-byte XOR-fold in the LogCore entry header. The fold
/// domain is not encoded as a version field, so the writer derives it from
/// existing valid records and refuses to emit a log block unless one candidate
/// reproduces every calibration record exactly.
/// </summary>
internal sealed class RefsMLogChecksum {
  private readonly RefsMLogXorFoldKind _kind;

  private RefsMLogChecksum(RefsMLogXorFoldKind kind) => this._kind = kind;

  public RefsMLogXorFoldKind Kind => this._kind;

  public static RefsMLogChecksum Detect(IEnumerable<byte[]> records) {
    ArgumentNullException.ThrowIfNull(records);
    var samples = records
      .Where(b => b.Length == RefsMLogCodec.LogBlockSize && b.AsSpan(0, 4).SequenceEqual("MLog"u8))
      .Take(16)
      .ToList();
    if (samples.Count == 0)
      throw new NotSupportedException("ReFS MLog checksum calibration requires at least one existing LogCore record.");

    var candidates = Enum.GetValues<RefsMLogXorFoldKind>().ToList();
    foreach (var sample in samples) {
      if (!TryGetEntry(sample, out var entryOffset, out var payloadOffset, out var payloadLength, out var declaredLength))
        continue;
      var stored = BinaryPrimitives.ReadUInt64LittleEndian(sample.AsSpan(entryOffset + 8, 8));
      if (stored == 0) continue;
      candidates.RemoveAll(kind => Compute(sample, kind, entryOffset, payloadOffset, payloadLength, declaredLength) != stored);
      if (candidates.Count == 0)
        throw new InvalidDataException("No supported ReFS MLog XOR-fold domain reproduces the existing record checksums.");
    }

    if (candidates.Count != 1)
      throw new NotSupportedException(
        $"ReFS MLog XOR-fold calibration is ambiguous ({string.Join(", ", candidates)}); refusing native log emission.");
    return new RefsMLogChecksum(candidates[0]);
  }

  public ulong Compute(ReadOnlySpan<byte> block) {
    if (!TryGetEntry(block, out var entryOffset, out var payloadOffset, out var payloadLength, out var declaredLength))
      throw new InvalidDataException("ReFS MLog entry framing is malformed.");
    return Compute(block, this._kind, entryOffset, payloadOffset, payloadLength, declaredLength);
  }

  public bool Verify(ReadOnlySpan<byte> block) {
    if (!TryGetEntry(block, out var entryOffset, out _, out _, out _)) return false;
    var stored = BinaryPrimitives.ReadUInt64LittleEndian(block.Slice(entryOffset + 8, 8));
    return stored != 0 && this.Compute(block) == stored;
  }

  public void Stamp(Span<byte> block) {
    if (!TryGetEntry(block, out var entryOffset, out _, out _, out _))
      throw new InvalidDataException("ReFS MLog entry framing is malformed.");
    block.Slice(entryOffset + 8, 8).Clear();
    BinaryPrimitives.WriteUInt64LittleEndian(block.Slice(entryOffset + 8, 8), this.Compute(block));
  }

  private static ulong Compute(
      ReadOnlySpan<byte> source,
      RefsMLogXorFoldKind kind,
      int entryOffset,
      int payloadOffset,
      int payloadLength,
      int declaredLength) {
    var scratch = source.ToArray();
    scratch.AsSpan(entryOffset + 8, 8).Clear();
    var payloadAbsolute = checked(entryOffset + payloadOffset);
    return kind switch {
      RefsMLogXorFoldKind.WholeBlockQwords
        => FoldQwords(scratch),
      RefsMLogXorFoldKind.EntryRegionQwords
        => FoldQwords(scratch.AsSpan(entryOffset)),
      RefsMLogXorFoldKind.DeclaredEntryQwords
        => FoldQwords(scratch.AsSpan(entryOffset, Math.Min(declaredLength, scratch.Length - entryOffset))),
      RefsMLogXorFoldKind.PayloadQwords
        => FoldQwords(scratch.AsSpan(payloadAbsolute, payloadLength)),
      RefsMLogXorFoldKind.WholeBlockDwordLanes
        => FoldDwordLanes(scratch),
      RefsMLogXorFoldKind.EntryRegionDwordLanes
        => FoldDwordLanes(scratch.AsSpan(entryOffset)),
      _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
  }

  private static ulong FoldQwords(ReadOnlySpan<byte> bytes) {
    ulong result = 0;
    var cursor = 0;
    while (cursor + 8 <= bytes.Length) {
      result ^= BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(cursor, 8));
      cursor += 8;
    }
    if (cursor < bytes.Length) {
      Span<byte> tail = stackalloc byte[8];
      bytes[cursor..].CopyTo(tail);
      result ^= BinaryPrimitives.ReadUInt64LittleEndian(tail);
    }
    return result;
  }

  private static ulong FoldDwordLanes(ReadOnlySpan<byte> bytes) {
    uint even = 0;
    uint odd = 0;
    var lane = 0;
    var cursor = 0;
    while (cursor + 4 <= bytes.Length) {
      if ((lane++ & 1) == 0) even ^= BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(cursor, 4));
      else odd ^= BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(cursor, 4));
      cursor += 4;
    }
    if (cursor < bytes.Length) {
      Span<byte> tail = stackalloc byte[4];
      bytes[cursor..].CopyTo(tail);
      if ((lane & 1) == 0) even ^= BinaryPrimitives.ReadUInt32LittleEndian(tail);
      else odd ^= BinaryPrimitives.ReadUInt32LittleEndian(tail);
    }
    return ((ulong)odd << 32) | even;
  }

  internal static bool TryGetEntry(
      ReadOnlySpan<byte> block,
      out int entryOffset,
      out int payloadOffset,
      out int payloadLength,
      out int declaredLength) {
    entryOffset = payloadOffset = payloadLength = declaredLength = 0;
    if (block.Length != RefsMLogCodec.LogBlockSize || !block[..4].SequenceEqual("MLog"u8)) return false;
    entryOffset = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(block[0x54..0x58]));
    if (entryOffset < 0x78 || entryOffset + 0x38 > block.Length) return false;
    var entry = block[entryOffset..];
    payloadLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(entry[0x20..0x24]));
    payloadOffset = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(entry[0x28..0x2C]));
    declaredLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(entry[0x2C..0x30]));
    if (payloadOffset < 0x38 || payloadOffset > entry.Length || payloadLength < 0
        || payloadLength > entry.Length - payloadOffset) return false;
    if (declaredLength <= 0 || declaredLength > entry.Length) declaredLength = entry.Length;
    return true;
  }
}

internal sealed record RefsMLogAppendResult(
  ulong Lsn,
  ulong PreviousLsn,
  long DataBlockOffset,
  ulong ActiveControlPhysicalLcn,
  RefsMLogXorFoldKind ChecksumKind);

/// <summary>
/// Native circular MLog writer. It writes one fully checksummed LogCore data
/// block, flushes it, then advances the alternate control slot. Checkpoint
/// publication remains a separate later commit step.
/// </summary>
internal sealed class RefsMLogWriter {
  private readonly Stream _image;
  private readonly RefsMetadataReader _metadata;
  private RefsMLogState _state;
  private readonly RefsMLogChecksum _checksum;

  public RefsMLogWriter(Stream image, RefsMetadataReader metadata) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(metadata);
    if (!image.CanRead || !image.CanWrite || !image.CanSeek)
      throw new ArgumentException("Native ReFS MLog writing requires a readable, writable, seekable image.", nameof(image));
    if (!RefsMLogReader.TryOpen(image, metadata, out this._state))
      throw new NotSupportedException("The ReFS MLog could not be opened from OID 0x9/0xA.");
    this._image = image;
    this._metadata = metadata;
    this._checksum = RefsMLogChecksum.Detect(this.ReadCalibrationBlocks());
  }

  public RefsMLogState State => this._state;
  public RefsMLogXorFoldKind ChecksumKind => this._checksum.Kind;

  public RefsMLogAppendResult Append(IReadOnlyList<RefsRedoRecord> records) {
    ArgumentNullException.ThrowIfNull(records);
    if (records.Count == 0) throw new ArgumentException("A ReFS MLog transaction must contain at least one redo record.", nameof(records));

    var redo = RefsRedoCodec.SerializeBlock(records);
    var latest = this.FindLatestDataBlock();
    var template = latest.Block ?? this.FindAnyDataTemplate()
      ?? throw new NotSupportedException("ReFS MLog has no existing data-record envelope to clone safely.");

    if (!RefsMLogChecksum.TryGetEntry(template, out var entryOffset, out var payloadOffset, out _, out _))
      throw new InvalidDataException("ReFS MLog template has malformed entry framing.");
    var absolutePayload = checked(entryOffset + payloadOffset);
    if (redo.Length > template.Length - absolutePayload)
      throw new InvalidOperationException("ReFS redo transaction does not fit the native LogCore data record.");

    var blockCount = checked((uint)(
      ((this._state.Information.DataEndPhysicalLcn - this._state.Information.DataStartPhysicalLcn)
        * (ulong)this._metadata.ClusterSize) / RefsMLogCodec.LogBlockSize));
    if (blockCount == 0) throw new InvalidDataException("ReFS MLog circular data area is empty.");

    var previousLsn = latest.Lsn;
    var previousIndex = latest.Block == null ? uint.MaxValue : unchecked((uint)previousLsn);
    var generation = latest.Block == null
      ? checked((uint)Math.Max(1UL, this._state.ActiveControl.Generation))
      : checked((uint)(previousLsn >> 32));
    var nextIndex = previousIndex == uint.MaxValue ? 0U : previousIndex + 1;
    if (nextIndex >= blockCount) {
      nextIndex = 0;
      generation = checked(generation + 1);
    }
    var lsn = ((ulong)generation << 32) | nextIndex;

    var block = template.ToArray();
    block.AsSpan(absolutePayload).Clear();
    redo.CopyTo(block, absolutePayload);
    BinaryPrimitives.WriteUInt64LittleEndian(block.AsSpan(0x28, 8), lsn);
    BinaryPrimitives.WriteUInt64LittleEndian(block.AsSpan(0x30, 8), previousLsn);
    var counter = BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(0x20, 4));
    BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(0x20, 4), checked(counter + 1));

    var entry = block.AsSpan(entryOffset);
    BinaryPrimitives.WriteUInt64LittleEndian(entry[0x00..0x08], lsn);
    entry[0x08..0x10].Clear();
    BinaryPrimitives.WriteUInt64LittleEndian(entry[0x18..0x20], previousLsn);
    BinaryPrimitives.WriteUInt32LittleEndian(entry[0x20..0x24], checked((uint)redo.Length));
    BinaryPrimitives.WriteUInt32LittleEndian(entry[0x2C..0x30], checked((uint)(block.Length - entryOffset)));
    BinaryPrimitives.WriteUInt32LittleEndian(entry[0x30..0x34], 2U);
    this._checksum.Stamp(block);

    var dataStartOffset = checked((long)this._state.Information.DataStartPhysicalLcn * this._metadata.ClusterSize);
    var targetOffset = checked(dataStartOffset + (long)nextIndex * RefsMLogCodec.LogBlockSize);
    if (targetOffset < 0 || targetOffset > this._image.Length - block.Length)
      throw new InvalidDataException("ReFS MLog circular target lies outside the image.");
    this._image.Position = targetOffset;
    this._image.Write(block);
    this._image.Flush();

    var activeControl = this.AdvanceControl(lsn, generation, records.Count);
    if (!RefsMLogReader.TryOpen(this._image, RefsMetadataReader.Open(this._image), out this._state))
      throw new IOException("ReFS MLog was written but could not be reopened through its native control state.");

    return new RefsMLogAppendResult(lsn, previousLsn, targetOffset, activeControl, this._checksum.Kind);
  }

  private ulong AdvanceControl(ulong lsn, uint generation, int redoCount) {
    var targets = this._state.Information.ControlPhysicalLcns
      .Where(lcn => lcn != this._state.ActiveControlPhysicalLcn)
      .Distinct()
      .ToArray();
    if (targets.Length == 0)
      throw new NotSupportedException("ReFS MLog has no alternate control slot.");

    var activeBytes = this.ReadLogBlockAtLcn(this._state.ActiveControlPhysicalLcn);
    if (!RefsMLogChecksum.TryGetEntry(activeBytes, out var entryOffset, out var payloadOffset, out _, out _))
      throw new InvalidDataException("Active ReFS MLog control record is malformed.");
    var candidate = activeBytes.ToArray();
    var payload = candidate.AsSpan(entryOffset + payloadOffset);

    var headerSequence = BinaryPrimitives.ReadUInt64LittleEndian(candidate.AsSpan(0x20, 8));
    BinaryPrimitives.WriteUInt64LittleEndian(candidate.AsSpan(0x20, 8), checked(headerSequence + 1));
    var sequence = BinaryPrimitives.ReadUInt64LittleEndian(payload[0x00..0x08]);
    BinaryPrimitives.WriteUInt64LittleEndian(payload[0x00..0x08], checked(sequence + 1));
    BinaryPrimitives.WriteUInt64LittleEndian(payload[0x20..0x28], generation);
    var writes = BinaryPrimitives.ReadUInt64LittleEndian(payload[0x38..0x40]);
    BinaryPrimitives.WriteUInt64LittleEndian(payload[0x38..0x40], checked(writes + 1));
    var entries = BinaryPrimitives.ReadUInt64LittleEndian(payload[0x48..0x50]);
    BinaryPrimitives.WriteUInt64LittleEndian(payload[0x48..0x50], checked(entries + (ulong)redoCount));

    // Keep OldestLsn unless the ring has caught it. If the just-written slot is
    // the low-32 index of OldestLsn, advance oldest to the following record.
    var oldest = BinaryPrimitives.ReadUInt64LittleEndian(payload[0x18..0x20]);
    if (unchecked((uint)oldest) == unchecked((uint)lsn))
      BinaryPrimitives.WriteUInt64LittleEndian(payload[0x18..0x20], lsn);

    this._checksum.Stamp(candidate);
    var target = targets[0];
    var targetOffset = checked((long)target * this._metadata.ClusterSize);
    this._image.Position = targetOffset;
    this._image.Write(candidate);
    this._image.Flush();
    return target;
  }

  private IEnumerable<byte[]> ReadCalibrationBlocks() {
    foreach (var offset in RefsMLogCodec.EnumerateDataBlockOffsets(this._state.ActiveControl, this._metadata.ClusterSize)) {
      if (offset < 0 || offset > this._image.Length - RefsMLogCodec.LogBlockSize) continue;
      var block = new byte[RefsMLogCodec.LogBlockSize];
      this._image.Position = offset;
      this._image.ReadExactly(block);
      if (RefsMLogCodec.TryParseDataRecord(block, out var record)
          && record.FormatMagic == this._state.ActiveControl.FormatMagic)
        yield return block;
    }
    foreach (var lcn in this._state.Information.ControlPhysicalLcns.Distinct()) {
      var block = this.ReadLogBlockAtLcn(lcn);
      if (RefsMLogCodec.TryParseControlRecord(block, out _)) yield return block;
    }
  }

  private (ulong Lsn, byte[]? Block) FindLatestDataBlock() {
    ulong bestLsn = 0;
    byte[]? best = null;
    foreach (var offset in RefsMLogCodec.EnumerateDataBlockOffsets(this._state.ActiveControl, this._metadata.ClusterSize)) {
      if (offset < 0 || offset > this._image.Length - RefsMLogCodec.LogBlockSize) continue;
      var block = new byte[RefsMLogCodec.LogBlockSize];
      this._image.Position = offset;
      this._image.ReadExactly(block);
      if (!RefsMLogCodec.TryParseDataRecord(block, out var record)
          || record.FormatMagic != this._state.ActiveControl.FormatMagic
          || !this._checksum.Verify(block)) continue;
      if (best == null || CompareLsn(record.Lsn, bestLsn) > 0) {
        bestLsn = record.Lsn;
        best = block;
      }
    }
    return (bestLsn, best);
  }

  private byte[]? FindAnyDataTemplate() {
    foreach (var block in this.ReadCalibrationBlocks())
      if (RefsMLogCodec.TryParseDataRecord(block, out _)) return block;
    return null;
  }

  private byte[] ReadLogBlockAtLcn(ulong lcn) {
    var offset = checked((long)lcn * this._metadata.ClusterSize);
    if (offset < 0 || offset > this._image.Length - RefsMLogCodec.LogBlockSize)
      throw new InvalidDataException("ReFS MLog block lies outside the image.");
    var result = new byte[RefsMLogCodec.LogBlockSize];
    this._image.Position = offset;
    this._image.ReadExactly(result);
    return result;
  }

  private static int CompareLsn(ulong left, ulong right) {
    var lg = unchecked((uint)(left >> 32));
    var rg = unchecked((uint)(right >> 32));
    var g = lg.CompareTo(rg);
    return g != 0 ? g : unchecked((uint)left).CompareTo(unchecked((uint)right));
  }
}

internal interface IRefsRedoTarget {
  void Apply(ulong lsn, RefsRedoRecord record);
}

/// <summary>
/// Two-pass-friendly redo enumerator. Analysis is performed by validation and
/// LSN ordering; the target then receives redo records in durable order. Unknown
/// opcode semantics belong to the target and must fail closed there.
/// </summary>
internal static class RefsMLogReplayer {
  public static int Replay(
      Stream image,
      RefsMetadataReader metadata,
      IRefsRedoTarget target,
      ulong minimumLsn = 0) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(metadata);
    ArgumentNullException.ThrowIfNull(target);
    if (!RefsMLogReader.TryOpen(image, metadata, out var state)) return 0;

    var blocks = new List<(RefsMLogDataRecord Record, byte[] Bytes)>();
    foreach (var offset in RefsMLogCodec.EnumerateDataBlockOffsets(state.ActiveControl, metadata.ClusterSize)) {
      if (offset < 0 || offset > image.Length - RefsMLogCodec.LogBlockSize) continue;
      var bytes = new byte[RefsMLogCodec.LogBlockSize];
      image.Position = offset;
      image.ReadExactly(bytes);
      if (RefsMLogCodec.TryParseDataRecord(bytes, out var record)
          && record.FormatMagic == state.ActiveControl.FormatMagic
          && record.Lsn >= minimumLsn)
        blocks.Add((record, bytes));
    }
    if (blocks.Count == 0) return 0;

    var checksum = RefsMLogChecksum.Detect(blocks.Select(b => b.Bytes));
    var applied = 0;
    foreach (var item in blocks.OrderBy(b => b.Record.Lsn)) {
      if (!checksum.Verify(item.Bytes))
        throw new InvalidDataException($"ReFS MLog record LSN 0x{item.Record.Lsn:X} failed its XOR-fold checksum.");
      foreach (var redo in item.Record.RedoRecords) {
        target.Apply(item.Record.Lsn, redo);
        ++applied;
      }
    }
    return applied;
  }
}