#pragma warning disable CS1591
using System.Buffers.Binary;
using static FileSystem.BcacheFs.BcacheFsFormat;

namespace FileSystem.BcacheFs;

/// <summary>
/// Logical recovery overlay built from the journal before any b-tree is
/// modified. Normal reads may compose this with on-disk b-tree contents; a
/// recovery writer can later replay the same ordered updates transactionally.
/// </summary>
internal sealed class BcacheFsJournalOverlay {
  internal required IReadOnlyList<BcacheFsJournalKeyUpdate> KeyUpdates { get; init; }
  internal required IReadOnlyList<BcacheFsJournalRootUpdate> RootUpdates { get; init; }
  internal required IReadOnlyList<BcacheFsJournalSequenceRange> BlacklistedSequences { get; init; }
  internal required IReadOnlyList<string> Diagnostics { get; init; }
  internal required bool Complete { get; init; }

  internal IEnumerable<BcacheFsJournalKeyUpdate> Keys(byte btreeId, byte level)
    => this.KeyUpdates.Where(k => k.BtreeId == btreeId && k.Level == level);

  internal bool IsBlacklisted(ulong sequence)
    => IsBlacklisted(sequence, this.BlacklistedSequences);

  internal BcacheFsJournalRootUpdate? LatestRoot(byte btreeId)
    => this.RootUpdates
      .Where(r => r.BtreeId == btreeId)
      .OrderByDescending(r => r.Sequence)
      .ThenByDescending(r => r.JournalOrder)
      .FirstOrDefault();

  /// <summary>
  /// Kernel-equivalent last-writer-wins view for non-accounting key slots.
  /// Accounting keys are deltas and deliberately remain as individual updates.
  /// </summary>
  internal IReadOnlyDictionary<BcacheFsJournalSlot, BcacheFsJournalKeyUpdate> LatestSlotUpdates() {
    var result = new Dictionary<BcacheFsJournalSlot, BcacheFsJournalKeyUpdate>();
    foreach (var update in this.KeyUpdates.OrderBy(k => k.Sequence).ThenBy(k => k.JournalOrder)) {
      if (update.Key.Type == BcacheFsKeyType.Accounting) continue;
      result[new BcacheFsJournalSlot(update.BtreeId, update.Level, update.Key.Position)] = update;
    }
    return result;
  }

  internal static BcacheFsJournalOverlay Build(
      BcacheFsJournalLog log,
      IEnumerable<BcacheFsJournalSequenceRange>? initialBlacklists = null) {
    ArgumentNullException.ThrowIfNull(log);
    var diagnostics = new List<string>(log.Diagnostics);
    var blacklists = initialBlacklists?.ToList() ?? [];
    var complete = true;

    // Blacklist ranges are half-open [start,end), matching
    // journal_seq_blacklist_entry and bch2_journal_seq_is_blacklisted().
    foreach (var sequence in log.Sequences.Where(s => s.Replayable))
      foreach (var entry in sequence.Preferred!.Parsed!.Entries)
        switch (entry.Header.Type) {
          case BcacheFsJournalEntryType.Blacklist when entry.Payload.Length == 8: {
            var blacklisted = BinaryPrimitives.ReadUInt64LittleEndian(entry.Payload);
            if (blacklisted == ulong.MaxValue) {
              diagnostics.Add("legacy journal blacklist names U64_MAX and cannot be represented as a half-open range.");
              complete = false;
            } else {
              blacklists.Add(new BcacheFsJournalSequenceRange(blacklisted, blacklisted + 1));
            }
            break;
          }
          case BcacheFsJournalEntryType.BlacklistV2 when entry.Payload.Length == 16:
            blacklists.Add(new BcacheFsJournalSequenceRange(
              BinaryPrimitives.ReadUInt64LittleEndian(entry.Payload),
              BinaryPrimitives.ReadUInt64LittleEndian(entry.Payload.AsSpan(8))));
            break;
        }
    blacklists = NormalizeRanges(blacklists, diagnostics);

    var replay = log.ReplayWindow().OrderBy(s => s.Sequence).ToList();
    var required = replay.Where(s => !IsBlacklisted(s.Sequence, blacklists)).ToList();
    foreach (var sequence in required)
      if (!sequence.Replayable) {
        complete = false;
        diagnostics.Add($"required journal sequence {sequence.Sequence} has no checksum-valid replayable replica.");
      }

    if (log.OldestRequiredSequence != 0 && required.Count > 0
        && required[0].Sequence > log.OldestRequiredSequence) {
      var gapStart = log.OldestRequiredSequence;
      var gapEndExclusive = required[0].Sequence;
      if (!RangeCovered(gapStart, gapEndExclusive, blacklists)) {
        complete = false;
        diagnostics.Add($"leading journal sequence gap {gapStart}..{gapEndExclusive - 1} is not blacklisted.");
      }
    }

    if (log.OldestRequiredSequence != 0 && required.Count == 0) {
      complete = false;
      diagnostics.Add($"journal requires replay from sequence {log.OldestRequiredSequence}, but no replayable sequence window was found.");
    }

    for (var i = 1; i < required.Count; ++i) {
      var previous = required[i - 1].Sequence;
      var current = required[i].Sequence;
      if (previous == ulong.MaxValue || current <= previous + 1) continue;
      var gapStart = previous + 1;
      var gapEndExclusive = current;
      if (!RangeCovered(gapStart, gapEndExclusive, blacklists)) {
        complete = false;
        diagnostics.Add($"journal sequence gap {gapStart}..{current - 1} is not blacklisted.");
      }
    }

    var keys = new List<BcacheFsJournalKeyUpdate>();
    var roots = new List<BcacheFsJournalRootUpdate>();
    foreach (var sequence in required.Where(s => s.Replayable)) {
      var set = sequence.Preferred!.Parsed!;
      var order = 0;
      foreach (var entry in set.Entries) {
        switch (entry.Header.Type) {
          case BcacheFsJournalEntryType.BtreeKeys:
          case BcacheFsJournalEntryType.WriteBufferKeys:
            if (!ReadKeys(
                entry,
                sequence.Sequence,
                ref order,
                keys,
                diagnostics,
                set.Header.BigEndian,
                set.Header.Version))
              complete = false;
            break;

          case BcacheFsJournalEntryType.BtreeRoot:
            if (!ReadRoot(
                entry,
                sequence.Sequence,
                ref order,
                roots,
                diagnostics,
                set.Header.BigEndian,
                set.Header.Version))
              complete = false;
            break;

          default:
            ++order;
            break;
        }
      }
    }

    return new BcacheFsJournalOverlay {
      KeyUpdates = keys,
      RootUpdates = roots,
      BlacklistedSequences = blacklists,
      Diagnostics = diagnostics,
      Complete = complete,
    };
  }

  private static bool ReadKeys(
      BcacheFsJournalEntry entry,
      ulong sequence,
      ref int order,
      List<BcacheFsJournalKeyUpdate> destination,
      List<string> diagnostics,
      bool bigEndian,
      uint version) {
    if (!BcacheFsJournalEntryCodec.TryReadKeys(entry, out var keys, out var error, bigEndian)) {
      diagnostics.Add($"journal sequence {sequence} btree {entry.Header.BtreeId}: {error}");
      return false;
    }

    foreach (var key in keys) {
      var compatible = BcacheFsKeyCompatibility.Apply(
        key, entry.Header.BtreeId, entry.Header.Level, version);
      destination.Add(new BcacheFsJournalKeyUpdate(
        sequence, order++, entry.Header.BtreeId, entry.Header.Level, compatible));
    }
    return true;
  }

  private static bool ReadRoot(
      BcacheFsJournalEntry entry,
      ulong sequence,
      ref int order,
      List<BcacheFsJournalRootUpdate> destination,
      List<string> diagnostics,
      bool bigEndian,
      uint version) {
    if (!BcacheFsJournalEntryCodec.TryReadKeys(entry, out var keys, out var error, bigEndian)) {
      diagnostics.Add($"journal sequence {sequence} root for btree {entry.Header.BtreeId}: {error}");
      return false;
    }
    if (keys.Count != 1) {
      diagnostics.Add($"journal sequence {sequence} root for btree {entry.Header.BtreeId} contains {keys.Count} keys; expected exactly one.");
      return false;
    }

    var compatible = BcacheFsKeyCompatibility.Apply(
      keys[0], entry.Header.BtreeId, entry.Header.Level, version);
    destination.Add(new BcacheFsJournalRootUpdate(
      sequence, order++, entry.Header.BtreeId, entry.Header.Level, compatible));
    return true;
  }

  private static List<BcacheFsJournalSequenceRange> NormalizeRanges(
      IEnumerable<BcacheFsJournalSequenceRange> ranges,
      List<string> diagnostics) {
    var sorted = ranges
      .Where(r => {
        if (r.Start < r.EndExclusive) return true;
        diagnostics.Add($"invalid journal blacklist [{r.Start},{r.EndExclusive}) (start >= end).");
        return false;
      })
      .OrderBy(r => r.Start)
      .ThenBy(r => r.EndExclusive)
      .ToList();
    if (sorted.Count < 2) return sorted;

    var result = new List<BcacheFsJournalSequenceRange> { sorted[0] };
    for (var i = 1; i < sorted.Count; ++i) {
      var last = result[^1];
      var next = sorted[i];
      if (next.Start <= last.EndExclusive)
        result[^1] = new BcacheFsJournalSequenceRange(last.Start, Math.Max(last.EndExclusive, next.EndExclusive));
      else
        result.Add(next);
    }
    return result;
  }

  private static bool IsBlacklisted(ulong sequence, IReadOnlyList<BcacheFsJournalSequenceRange> ranges)
    => ranges.Any(r => sequence >= r.Start && sequence < r.EndExclusive);

  private static bool RangeCovered(
      ulong start,
      ulong endExclusive,
      IReadOnlyList<BcacheFsJournalSequenceRange> ranges) {
    if (start >= endExclusive) return true;
    var cursor = start;
    foreach (var range in ranges) {
      if (range.EndExclusive <= cursor) continue;
      if (range.Start > cursor) return false;
      if (range.EndExclusive >= endExclusive) return true;
      cursor = range.EndExclusive;
    }
    return false;
  }
}

/// <summary>Half-open journal sequence range [Start, EndExclusive).</summary>
internal readonly record struct BcacheFsJournalSequenceRange(ulong Start, ulong EndExclusive);

internal readonly record struct BcacheFsJournalSlot(byte BtreeId, byte Level, Bpos Position);

internal sealed record BcacheFsJournalKeyUpdate(
  ulong Sequence,
  int JournalOrder,
  byte BtreeId,
  byte Level,
  BcacheFsRawKey Key);

internal sealed record BcacheFsJournalRootUpdate(
  ulong Sequence,
  int JournalOrder,
  byte BtreeId,
  byte Level,
  BcacheFsRawKey RootKey);
