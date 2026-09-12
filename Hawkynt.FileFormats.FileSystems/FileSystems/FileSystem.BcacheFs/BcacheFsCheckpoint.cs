#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileSystem.BcacheFs;

/// <summary>
/// Last clean checkpoint recorded in BCH_SB_FIELD_clean. Its payload uses the
/// same jset_entry framing as the journal, including btree_root entries.
/// </summary>
internal sealed class BcacheFsCheckpoint {
  internal required uint Flags { get; init; }
  internal required ushort ReadClock { get; init; }
  internal required ushort WriteClock { get; init; }
  internal required ulong JournalSequence { get; init; }
  internal required IReadOnlyList<BcacheFsJournalEntry> Entries { get; init; }
  internal required IReadOnlyList<BcacheFsTreeRoot> Roots { get; init; }
  internal required IReadOnlyList<string> Diagnostics { get; init; }
  internal required bool Valid { get; init; }

  internal static BcacheFsCheckpoint? Read(BcacheFsSuperblockRecord superblock) {
    ArgumentNullException.ThrowIfNull(superblock);
    var field = superblock.FieldsOf(BcacheFsSuperblockFieldType.Clean).LastOrDefault();
    if (field == null) return null;

    var diagnostics = new List<string>();
    var bytes = field.RawBytes;
    if (bytes.Length < 24)
      return new BcacheFsCheckpoint {
        Flags = 0,
        ReadClock = 0,
        WriteClock = 0,
        JournalSequence = 0,
        Entries = [],
        Roots = [],
        Diagnostics = ["clean field is shorter than its 24-byte fixed header."],
        Valid = false,
      };

    var flags = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8));
    var readClock = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(12));
    var writeClock = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(14));
    var journalSequence = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(16));

    if (!BcacheFsJournalEntryCodec.TryParseList(bytes.AsSpan(24), out var entries, out var entryError)) {
      diagnostics.Add(entryError);
      return new BcacheFsCheckpoint {
        Flags = flags,
        ReadClock = readClock,
        WriteClock = writeClock,
        JournalSequence = journalSequence,
        Entries = entries,
        Roots = [],
        Diagnostics = diagnostics,
        Valid = false,
      };
    }

    var roots = new List<BcacheFsTreeRoot>();
    foreach (var entry in entries) {
      if (entry.Header.Type != BcacheFsJournalEntryType.BtreeRoot) continue;
      if (!BcacheFsJournalEntryCodec.TryReadKeys(
          entry, out var keys, out var keyError, superblock.BigEndian)) {
        diagnostics.Add($"clean root for btree {entry.Header.BtreeId}: {keyError}");
        continue;
      }
      if (keys.Count != 1) {
        diagnostics.Add($"clean root for btree {entry.Header.BtreeId} contains {keys.Count} keys; expected exactly one.");
        continue;
      }
      roots.Add(new BcacheFsTreeRoot(
        entry.Header.BtreeId,
        entry.Header.Level,
        keys[0],
        journalSequence,
        BcacheFsTreeRootSource.CleanSuperblock));
    }

    var rootEntries = entries.Count(e => e.Header.Type == BcacheFsJournalEntryType.BtreeRoot);
    var valid = diagnostics.Count == 0 && roots.Count == rootEntries;
    return new BcacheFsCheckpoint {
      Flags = flags,
      ReadClock = readClock,
      WriteClock = writeClock,
      JournalSequence = journalSequence,
      Entries = entries,
      Roots = roots,
      Diagnostics = diagnostics,
      Valid = valid,
    };
  }
}

internal enum BcacheFsTreeRootSource : byte {
  CleanSuperblock,
  Journal,
}

internal sealed record BcacheFsTreeRoot(
  byte BtreeId,
  byte Level,
  BcacheFsRawKey Key,
  ulong Sequence,
  BcacheFsTreeRootSource Source);
