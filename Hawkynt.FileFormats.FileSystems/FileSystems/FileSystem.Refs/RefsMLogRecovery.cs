#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileSystem.Refs;

internal sealed record RefsMLogRecoveryRecord(
  long Offset,
  RefsMLogDataRecord Record,
  byte[] Bytes);

/// <summary>
/// Builds the redo-only crash-recovery window for the active checkpoint. ReFS
/// records the oldest log record still needed by recovery in CHKP+0x70; stale
/// circular-buffer records before that boundary are history, not replay input.
/// </summary>
internal static class RefsMLogRecovery {
  public static ulong ReadOldestRequiredLsn(Stream image, RefsMetadataReader metadata) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(metadata);
    var bootstrap = RefsBootstrapState.Open(image);
    var checkpoint = bootstrap.ReadCheckpoint(metadata.ActiveCheckpointLcn);
    if (checkpoint.Length < 0x78 || !checkpoint.AsSpan(0, 4).SequenceEqual("CHKP"u8))
      throw new InvalidDataException("Active ReFS checkpoint is too short to expose its recovery LSN.");
    return BinaryPrimitives.ReadUInt64LittleEndian(checkpoint.AsSpan(0x70, 8));
  }

  public static IReadOnlyList<RefsMLogRecoveryRecord> Analyze(
      Stream image,
      RefsMetadataReader metadata,
      out RefsMLogXorFoldKind checksumKind) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(metadata);
    if (!RefsMLogReader.TryOpen(image, metadata, out var state)) {
      checksumKind = default;
      return [];
    }

    var candidates = new List<RefsMLogRecoveryRecord>();
    foreach (var offset in RefsMLogCodec.EnumerateDataBlockOffsets(state.ActiveControl, metadata.ClusterSize)) {
      if (offset < 0 || offset > image.Length - RefsMLogCodec.LogBlockSize) continue;
      var bytes = new byte[RefsMLogCodec.LogBlockSize];
      image.Position = offset;
      image.ReadExactly(bytes);
      if (!RefsMLogCodec.TryParseDataRecord(bytes, out var record)) continue;
      if (record.FormatMagic != state.ActiveControl.FormatMagic) continue;
      candidates.Add(new RefsMLogRecoveryRecord(offset, record, bytes));
    }

    if (candidates.Count == 0) {
      checksumKind = default;
      return [];
    }

    var oldest = ReadOldestRequiredLsn(image, metadata);
    var live = candidates
      .Where(c => oldest == 0 || CompareLsn(c.Record.Lsn, oldest) >= 0)
      .ToArray();
    if (live.Length == 0) {
      checksumKind = default;
      return [];
    }

    var checksum = RefsMLogChecksum.Detect(live.Select(c => c.Bytes));
    checksumKind = checksum.Kind;
    return SelectVerifiedForReplay(live, oldest, c => checksum.Verify(c.Bytes));
  }

  internal static IReadOnlyList<RefsMLogRecoveryRecord> SelectVerifiedForReplay(
      IEnumerable<RefsMLogRecoveryRecord> candidates,
      ulong oldestRequiredLsn,
      Func<RefsMLogRecoveryRecord, bool> verify) {
    ArgumentNullException.ThrowIfNull(candidates);
    ArgumentNullException.ThrowIfNull(verify);
    var live = candidates
      .Where(c => oldestRequiredLsn == 0 || CompareLsn(c.Record.Lsn, oldestRequiredLsn) >= 0)
      .ToArray();
    foreach (var candidate in live)
      if (!verify(candidate))
        throw new InvalidDataException(
          $"ReFS MLog live record LSN 0x{candidate.Record.Lsn:X} failed its XOR-fold checksum.");
    return SelectForReplay(live, oldestRequiredLsn);
  }

  internal static IReadOnlyList<RefsMLogRecoveryRecord> SelectForReplay(
      IEnumerable<RefsMLogRecoveryRecord> records,
      ulong oldestRequiredLsn) {
    ArgumentNullException.ThrowIfNull(records);
    var ordered = records
      .Where(r => oldestRequiredLsn == 0 || CompareLsn(r.Record.Lsn, oldestRequiredLsn) >= 0)
      .OrderBy(r => LsnGeneration(r.Record.Lsn))
      .ThenBy(r => LsnIndex(r.Record.Lsn))
      .ToList();

    for (var i = 1; i < ordered.Count; ++i) {
      if (ordered[i - 1].Record.Lsn == ordered[i].Record.Lsn)
        throw new InvalidDataException($"ReFS MLog contains duplicate live LSN 0x{ordered[i].Record.Lsn:X}.");

      var current = ordered[i].Record;
      var previous = ordered[i - 1].Record;
      if (current.PreviousLsn != 0 && CompareLsn(current.PreviousLsn, current.Lsn) >= 0)
        throw new InvalidDataException($"ReFS MLog LSN 0x{current.Lsn:X} points to a nonpreceding LSN.");
      var requiresImmediate = ShouldRequireImmediatePredecessor(current.Lsn, previous.Lsn);
      var refersToMissingLiveRecord = current.PreviousLsn != 0
        && (oldestRequiredLsn == 0 || CompareLsn(current.PreviousLsn, oldestRequiredLsn) >= 0);
      if (current.PreviousLsn != previous.Lsn && (requiresImmediate || refersToMissingLiveRecord))
        throw new InvalidDataException(
          $"ReFS MLog live chain is broken at LSN 0x{current.Lsn:X}: previous is 0x{current.PreviousLsn:X}, expected 0x{previous.Lsn:X}.");
    }

    return ordered;
  }

  private static bool ShouldRequireImmediatePredecessor(ulong current, ulong previous) {
    var currentGeneration = LsnGeneration(current);
    var previousGeneration = LsnGeneration(previous);
    var currentIndex = LsnIndex(current);
    var previousIndex = LsnIndex(previous);

    // Within one generation the low 32 bits are the 4 KiB circular-log block
    // index. Consecutive live slots must therefore chain exactly. Generation
    // rollover is the circular-buffer boundary; the driver may reset the link
    // there, so validation resumes on the following record.
    return currentGeneration == previousGeneration
      && currentIndex == unchecked(previousIndex + 1);
  }

  private static uint LsnGeneration(ulong lsn) => unchecked((uint)(lsn >> 32));
  private static uint LsnIndex(ulong lsn) => unchecked((uint)lsn);

  internal static int CompareLsn(ulong left, ulong right) {
    var generation = LsnGeneration(left).CompareTo(LsnGeneration(right));
    return generation != 0 ? generation : LsnIndex(left).CompareTo(LsnIndex(right));
  }
}

/// <summary>
/// Redo-only ReFS restarter front-end. Analysis verifies LogCore XOR folds and
/// applies only the CHKP-advertised recovery window. Opcode-specific mutation is
/// delegated to an explicit target so unknown redo grammars remain fail-closed.
/// </summary>
internal static class RefsMLogRestarter {
  public static int Replay(
      Stream image,
      RefsMetadataReader metadata,
      IRefsRedoTarget target,
      ulong minimumLsn = 0) {
    ArgumentNullException.ThrowIfNull(target);
    var recovery = RefsMLogRecovery.Analyze(image, metadata, out _);
    return ReplaySelected(recovery, target, minimumLsn);
  }

  internal static int ReplaySelected(
      IReadOnlyList<RefsMLogRecoveryRecord> recovery,
      IRefsRedoTarget target,
      ulong minimumLsn = 0) {
    ArgumentNullException.ThrowIfNull(recovery);
    ArgumentNullException.ThrowIfNull(target);
    var pending = recovery
      .Where(item => minimumLsn == 0 || RefsMLogRecovery.CompareLsn(item.Record.Lsn, minimumLsn) >= 0)
      .SelectMany(item => item.Record.RedoRecords.Select(redo => (item.Record.Lsn, Redo: redo)))
      .ToArray();

    // An unsupported opcode or payload in a later log block must be found
    // before a preceding block changes the target. The target validates the
    // version-specific payload grammar; the framing layer validates opcodes.
    foreach (var (lsn, redo) in pending) {
      if (!Enum.IsDefined(redo.Opcode) || redo.Opcode == RefsRedoOpcode.ReservedUnhandled)
        throw new NotSupportedException($"ReFS redo opcode 0x{(uint)redo.Opcode:X2} cannot be replayed.");
      target.Preflight(lsn, redo);
    }

    var applied = 0;
    foreach (var (lsn, redo) in pending) {
      target.Apply(lsn, redo);
      ++applied;
    }
    return applied;
  }
}
