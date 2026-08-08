#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.OpenVms;

/// <summary>
/// Root-directory block layout for the workbench-layout Files-11 ODS-2 image.
/// <para>
/// A real ODS-2 directory is a chain of 512-byte blocks holding
/// variable-length name records, each followed by one or more
/// version-number / File-ID tuples. Implementing the full variable-length
/// record walker is multi-week work (out of scope per the descriptor's
/// honest-scope notice). Our writer / reader / in-place modifier instead
/// share a fixed 64-byte directory-entry slot layout inside the same
/// directory blocks at the spec-mandated LBN (root directory at
/// <see cref="OpenVmsLayout.RootDirectoryLbn"/>):
/// </para>
/// <list type="bullet">
///   <item>+0  (u16 LE) — File-ID number low 16 bits. 0 marks the slot free.</item>
///   <item>+2  (u16 LE) — File-ID sequence.</item>
///   <item>+4  (u16 LE) — File-ID number high 16 bits.</item>
///   <item>+6  (u16 LE) — reserved (zero).</item>
///   <item>+8  (24 bytes ASCII) — file name (NUL-padded).</item>
///   <item>+32 (u32 LE) — file size in bytes (low 32).</item>
///   <item>+36 (u32 LE) — file size in bytes (high 32, for &gt;4 GB headroom — always 0 today).</item>
///   <item>+40 .. +63 — reserved (zero).</item>
/// </list>
/// <para>
/// 8 slots per 512-byte LBN (8 × 64 = 512). Directory grows by linking
/// additional blocks via the chain field at the start of each directory
/// block (next-LBN u32 LE at offset 0 of each block — overlapping the
/// first slot's FID-low field; we therefore reserve slot 0 of each
/// directory block exclusively for chain linkage and start file entries
/// at slot 1, giving 7 entries per block).
/// </para>
/// </summary>
/// <remarks>
/// <para><b>What a real ODS-2 directory holds, and what this does not.</b> A
/// Files-11 directory is a run of variable-length records: a size word holding
/// the record's length less two, a version limit, flags, a name length, the name
/// padded to an even boundary, and then one eight-byte entry per version — a
/// version number and a file id. A record whose size word reads 0xFFFF ends the
/// block. What follows here is fixed-width slots of this writer's own instead,
/// which is why an ODS-2 reader mounts one of these volumes and reads its label
/// but will not list it.</para>
///
/// <para>Worth knowing before that is written: the reader used to check this work
/// computes a record's entry bytes as <c>size + 2 - ((namecount + 8) &amp; ~1)</c>,
/// taking eight from the size of its own record struct where the name actually
/// begins at six. The two cancel for a name of odd length and are two out for an
/// even one, so that reader agrees with a correct directory only for odd-length
/// names. Build to the format, not to the reader.</para>
/// </remarks>
public static class OpenVmsDirectory {
  public const int EntrySize = 64;
  public const int EntriesPerBlock = OpenVmsLayout.BlockSize / EntrySize;        // 8
  public const int FileEntryStartSlot = 1;                                       // slot 0 is the chain link
  public const int FileEntriesPerBlock = EntriesPerBlock - FileEntryStartSlot;   // 7
  public const int FileNameLength = 24;

  // Offsets inside a 64-byte entry.
  public const int EntryFidLow = 0;
  public const int EntryFidSeq = 2;
  public const int EntryFidHigh = 4;
  public const int EntryNameOffset = 8;
  public const int EntrySizeLowOffset = 32;
  public const int EntrySizeHighOffset = 36;

  // The next-block chain link sits in the first 4 bytes of the directory block
  // (which is slot 0's FID-low slot — slot 0 is reserved exclusively for this).
  public const int ChainLinkOffset = 0;

  /// <summary>One parsed directory entry.</summary>
  public sealed record class Entry(int FileId, ushort Sequence, string Name, long Size) {
    public bool IsFree => this.FileId == 0;
  }

  /// <summary>Writes <paramref name="entry"/> into the directory block at <paramref name="slot"/>.</summary>
  public static void WriteEntry(Span<byte> dirBlock, int slot, Entry entry) {
    if (slot < FileEntryStartSlot || slot >= EntriesPerBlock)
      throw new ArgumentOutOfRangeException(nameof(slot));
    var off = slot * EntrySize;
    BinaryPrimitives.WriteUInt16LittleEndian(dirBlock.Slice(off + EntryFidLow, 2), (ushort)(entry.FileId & 0xFFFF));
    BinaryPrimitives.WriteUInt16LittleEndian(dirBlock.Slice(off + EntryFidSeq, 2), entry.Sequence);
    BinaryPrimitives.WriteUInt16LittleEndian(dirBlock.Slice(off + EntryFidHigh, 2), (ushort)((entry.FileId >> 16) & 0xFFFF));
    BinaryPrimitives.WriteUInt16LittleEndian(dirBlock.Slice(off + 6, 2), 0);

    // Clear the name area first so prior content doesn't leak.
    dirBlock.Slice(off + EntryNameOffset, FileNameLength).Clear();
    var nameBytes = Encoding.ASCII.GetBytes(entry.Name);
    var nameLen = Math.Min(nameBytes.Length, FileNameLength);
    nameBytes.AsSpan(0, nameLen).CopyTo(dirBlock.Slice(off + EntryNameOffset));

    BinaryPrimitives.WriteUInt32LittleEndian(dirBlock.Slice(off + EntrySizeLowOffset, 4), (uint)(entry.Size & 0xFFFFFFFF));
    BinaryPrimitives.WriteUInt32LittleEndian(dirBlock.Slice(off + EntrySizeHighOffset, 4), (uint)((entry.Size >> 32) & 0xFFFFFFFF));

    // Trailing reserved bytes stay zero.
    dirBlock.Slice(off + 40, EntrySize - 40).Clear();
  }

  /// <summary>Zeros the directory slot — the entry becomes free.</summary>
  public static void ClearEntry(Span<byte> dirBlock, int slot) {
    if (slot < FileEntryStartSlot || slot >= EntriesPerBlock)
      throw new ArgumentOutOfRangeException(nameof(slot));
    dirBlock.Slice(slot * EntrySize, EntrySize).Clear();
  }

  /// <summary>Reads the entry at <paramref name="slot"/> in <paramref name="dirBlock"/>.</summary>
  public static Entry ReadEntry(ReadOnlySpan<byte> dirBlock, int slot) {
    if (slot < FileEntryStartSlot || slot >= EntriesPerBlock)
      throw new ArgumentOutOfRangeException(nameof(slot));
    var off = slot * EntrySize;
    var fidLow = BinaryPrimitives.ReadUInt16LittleEndian(dirBlock.Slice(off + EntryFidLow, 2));
    var seq = BinaryPrimitives.ReadUInt16LittleEndian(dirBlock.Slice(off + EntryFidSeq, 2));
    var fidHigh = BinaryPrimitives.ReadUInt16LittleEndian(dirBlock.Slice(off + EntryFidHigh, 2));
    var nameRaw = dirBlock.Slice(off + EntryNameOffset, FileNameLength).ToArray();
    var end = Array.IndexOf(nameRaw, (byte)0);
    if (end < 0) end = nameRaw.Length;
    var name = Encoding.ASCII.GetString(nameRaw, 0, end);
    var sizeLow = BinaryPrimitives.ReadUInt32LittleEndian(dirBlock.Slice(off + EntrySizeLowOffset, 4));
    var sizeHigh = BinaryPrimitives.ReadUInt32LittleEndian(dirBlock.Slice(off + EntrySizeHighOffset, 4));
    var size = ((long)sizeHigh << 32) | sizeLow;
    return new Entry((fidHigh << 16) | fidLow, seq, name, size);
  }

  /// <summary>Reads the chain link (next directory LBN, 0 if last) from <paramref name="dirBlock"/>.</summary>
  public static int ReadChainLink(ReadOnlySpan<byte> dirBlock)
    => (int)BinaryPrimitives.ReadUInt32LittleEndian(dirBlock.Slice(ChainLinkOffset, 4));

  /// <summary>Writes the chain link (next directory LBN, 0 if last) into <paramref name="dirBlock"/>.</summary>
  public static void WriteChainLink(Span<byte> dirBlock, int nextLbn)
    => BinaryPrimitives.WriteUInt32LittleEndian(dirBlock.Slice(ChainLinkOffset, 4), (uint)nextLbn);
}
