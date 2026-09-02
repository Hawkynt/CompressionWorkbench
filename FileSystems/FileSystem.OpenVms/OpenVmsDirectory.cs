#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.OpenVms;

/// <summary>
/// Directory blocks for a Files-11 ODS-2 volume: a run of variable-length name
/// records, each carrying one or more version-and-file-id pairs.
/// </summary>
/// <remarks>
/// <para>A record is a size word holding its own length less two, a version
/// limit, a flags byte, the length of the name, the name padded to an even
/// boundary, and then eight bytes per version — the version number and the six
/// bytes of a file id. A size word of 0xFFFF ends the records in a block. That is
/// what a reader of the format walks, and what this writes.</para>
///
/// <para><b>The chain link.</b> A real directory grows by gaining blocks in its
/// own header's map. This writer instead chains blocks by a link, which it keeps
/// in the last four bytes of each block — past the word that ends the records,
/// where a reader stops and never looks. So the records read as a proper
/// directory and the chain stays available to whatever walks it here.</para>
///
/// <para><b>A reader's quirk, worth knowing and not worth building to.</b> The
/// ODS-2 reader used to check this work computes a record's entry bytes as
/// <c>size + 2 - ((namecount + 8) &amp; ~1)</c>, taking eight from the size of its
/// own record struct where the name in fact begins at six. The two cancel for a
/// name of odd length and are two out for an even one, so that reader agrees with
/// a correct directory only for odd-length names. What is written here follows the
/// format.</para>
/// </remarks>
public static class OpenVmsDirectory {

  /// <summary>Bytes a record spends before its name.</summary>
  public const int RecordHeaderBytes = 6;

  /// <summary>Bytes one version-and-file-id pair takes.</summary>
  public const int VersionEntryBytes = 8;

  /// <summary>The size word that says a block holds no more records.</summary>
  public const ushort EndOfRecords = 0xFFFF;

  /// <summary>Where the link to the next block of the directory sits.</summary>
  public const int ChainLinkOffset = OpenVmsLayout.BlockSize - 4;

  /// <summary>How much of a block records may use, the link being at its end.</summary>
  public const int UsableBytes = ChainLinkOffset;

  /// <summary>The longest name a record here carries.</summary>
  public const int FileNameLength = 24;

  /// <summary>
  /// What the master file directory calls itself, which every Files-11 volume's
  /// root holds so a reader asked for [000000] finds something to open.
  /// </summary>
  public const string SelfName = "000000.DIR";

  /// <summary>Whether an entry is the directory's own, rather than anything put there.</summary>
  public static bool IsSelfEntry(Entry entry)
    => string.Equals(entry.Name, SelfName, StringComparison.OrdinalIgnoreCase);

  /// <summary>One entry of a directory: a name, and the file it names.</summary>
  public sealed record class Entry(int FileId, ushort Sequence, string Name, long Size) {
    /// <summary>
    /// Gets a value indicating whether is free.
    /// </summary>
public bool IsFree => this.FileId == 0;
  }

  /// <summary>Bytes the record for <paramref name="name" /> takes, one version of it.</summary>
  public static int RecordBytes(string name)
    => RecordHeaderBytes + (Encoding.ASCII.GetByteCount(name) + 1 & ~1) + VersionEntryBytes;

  /// <summary>Every entry the block holds, in the order they were written.</summary>
  public static List<Entry> Enumerate(ReadOnlySpan<byte> block) {
    var found = new List<Entry>();
    var offset = 0;
    while (offset + RecordHeaderBytes <= UsableBytes) {
      var size = BinaryPrimitives.ReadUInt16LittleEndian(block[offset..]);
      if (size == EndOfRecords) break;

      var length = size + 2;
      if (length < RecordHeaderBytes || offset + length > UsableBytes) break;

      var nameLength = block[offset + 5];
      var namePadded = nameLength + 1 & ~1;
      if (RecordHeaderBytes + namePadded > length) break;

      var name = Encoding.ASCII.GetString(block.Slice(offset + RecordHeaderBytes, nameLength));
      for (var at = offset + RecordHeaderBytes + namePadded; at + VersionEntryBytes <= offset + length;
           at += VersionEntryBytes) {
        var number = BinaryPrimitives.ReadUInt16LittleEndian(block[(at + 2)..]);
        var sequence = BinaryPrimitives.ReadUInt16LittleEndian(block[(at + 4)..]);
        var high = block[at + 7];
        found.Add(new Entry(high << 16 | number, sequence, name, 0));
      }

      offset += length;
    }

    return found;
  }

  /// <summary>
  /// Writes <paramref name="entries" /> into <paramref name="block" />, one record
  /// each, and ends them. The chain link is left as it was.
  /// </summary>
  /// <returns>False when they do not fit, the block then being left as it was.</returns>
  public static bool TryWrite(Span<byte> block, IReadOnlyList<Entry> entries) {
    var needed = 2;   // the word that ends the records
    foreach (var entry in entries) needed += RecordBytes(entry.Name);
    if (needed > UsableBytes) return false;

    block[..UsableBytes].Clear();
    var offset = 0;
    foreach (var entry in entries) {
      var nameBytes = Encoding.ASCII.GetBytes(entry.Name);
      var namePadded = nameBytes.Length + 1 & ~1;
      var length = RecordHeaderBytes + namePadded + VersionEntryBytes;

      BinaryPrimitives.WriteUInt16LittleEndian(block[offset..], (ushort)(length - 2));
      BinaryPrimitives.WriteUInt16LittleEndian(block[(offset + 2)..], 0);   // no version limit
      block[offset + 4] = 0;                                               // no flags
      block[offset + 5] = (byte)nameBytes.Length;
      nameBytes.CopyTo(block[(offset + RecordHeaderBytes)..]);

      var at = offset + RecordHeaderBytes + namePadded;
      BinaryPrimitives.WriteUInt16LittleEndian(block[at..], 1);            // version one
      BinaryPrimitives.WriteUInt16LittleEndian(block[(at + 2)..], (ushort)(entry.FileId & 0xFFFF));
      BinaryPrimitives.WriteUInt16LittleEndian(block[(at + 4)..], entry.Sequence);
      block[at + 6] = 0;                                                   // this volume
      block[at + 7] = (byte)(entry.FileId >> 16 & 0xFF);
      offset += length;
    }

    BinaryPrimitives.WriteUInt16LittleEndian(block[offset..], EndOfRecords);
    return true;
  }

  /// <summary>Adds one entry to what the block already holds.</summary>
  /// <returns>False when it does not fit.</returns>
  public static bool TryAppend(Span<byte> block, Entry entry) {
    var entries = Enumerate(block);
    entries.Add(entry);
    return TryWrite(block, entries);
  }

  /// <summary>Takes out every entry naming <paramref name="name" />.</summary>
  /// <returns>False when there was none.</returns>
  public static bool TryRemove(Span<byte> block, string name) {
    var entries = Enumerate(block);
    var kept = entries.FindAll(e => !string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
    if (kept.Count == entries.Count) return false;

    TryWrite(block, kept);
    return true;
  }

  /// <summary>Empties a block of records, leaving the chain link alone.</summary>
  public static void Clear(Span<byte> block) => TryWrite(block, []);

  /// <summary>Reads the chain link (next directory LBN, 0 if last) from <paramref name="dirBlock"/>.</summary>
  public static int ReadChainLink(ReadOnlySpan<byte> dirBlock)
    => (int)BinaryPrimitives.ReadUInt32LittleEndian(dirBlock.Slice(ChainLinkOffset, 4));

  /// <summary>Writes the chain link (next directory LBN, 0 if last) into <paramref name="dirBlock"/>.</summary>
  public static void WriteChainLink(Span<byte> dirBlock, int nextLbn)
    => BinaryPrimitives.WriteUInt32LittleEndian(dirBlock.Slice(ChainLinkOffset, 4), (uint)nextLbn);
}
