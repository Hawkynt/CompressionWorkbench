#pragma warning disable CS1591
using System.Buffers.Binary;
using static FileSystem.LittleFs.LittleFsFormat;

namespace FileSystem.LittleFs;

/// <summary>
/// Accumulates a single littlefs metadata-pair commit (a run of delta-encoded
/// tags terminated by a commit-CRC tag) and serialises it into a full block.
/// </summary>
/// <remarks>
/// On-disk encoding, matching the reference writer:
/// <list type="number">
///   <item><description>The block opens with the revision count (u32 LE).</description></item>
///   <item><description>Each tag word is written big-endian as
///     <c>tobe32((tag &amp; 0x7fffffff) ^ ptag)</c>, where <c>ptag</c> starts at
///     0xFFFFFFFF and becomes the previous in-memory tag after each write; the
///     tag's data bytes follow.</description></item>
///   <item><description>The commit ends with a CCRC tag (type 0x500, id 0x3ff,
///     length = size of the trailing CRC dword). The running CRC (seed
///     0xFFFFFFFF over every byte written so far, including the CCRC tag word)
///     is stored little-endian as the CCRC payload — no final inversion.</description></item>
/// </list>
/// The running CRC is taken over the exact bytes emitted to disk, so a reader
/// that re-CRCs the raw block bytes validates the commit identically.
/// </remarks>
internal sealed class CommitBuilder {
  private readonly uint _blockSize;
  private readonly List<(uint Tag, byte[] Data)> _entries = new();

  public CommitBuilder(uint blockSize) => this._blockSize = blockSize;

  public void AddTag(uint type, uint id, ReadOnlySpan<byte> data) {
    if ((uint)data.Length > LengthMax)
      throw new InvalidOperationException($"tag data length {data.Length} exceeds the 10-bit field limit.");
    this._entries.Add((MakeTag(type, id, (uint)data.Length), data.ToArray()));
  }

  /// <summary>Serialises the commit into a full-size block buffer.</summary>
  public byte[] Finish(uint revision) {
    var block = new byte[this._blockSize];
    var off = 0;

    BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(off, 4), revision);
    off += 4;

    var ptag = 0xFFFFFFFFu;
    var crc = Crc(0xFFFFFFFFu, block.AsSpan(0, 4)); // CRC starts over the revision

    foreach (var (tag, data) in this._entries) {
      var onDisk = (tag & 0x7FFFFFFF) ^ ptag;
      BinaryPrimitives.WriteUInt32BigEndian(block.AsSpan(off, 4), onDisk);
      crc = Crc(crc, block.AsSpan(off, 4));
      off += 4;

      data.CopyTo(block.AsSpan(off));
      crc = Crc(crc, data);
      off += data.Length;

      ptag = tag;
    }

    // Commit-CRC tag: length is the size of the trailing CRC dword (4). The
    // running CRC covers the CCRC tag word too, then the CRC dword is stored.
    if (off + 8 > this._blockSize)
      throw new InvalidOperationException("commit does not fit in a single metadata block.");

    var ccrcTag = MakeTag(TypeCrc, 0x3FF, 4);
    var ccrcOnDisk = (ccrcTag & 0x7FFFFFFF) ^ ptag;
    BinaryPrimitives.WriteUInt32BigEndian(block.AsSpan(off, 4), ccrcOnDisk);
    crc = Crc(crc, block.AsSpan(off, 4));
    off += 4;

    BinaryPrimitives.WriteUInt32LittleEndian(block.AsSpan(off, 4), crc);

    return block;
  }
}
