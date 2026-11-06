#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileSystem.Ufs;

/// <summary>
/// The byte order of every multi-byte field of a UFS volume — superblock, cylinder-group
/// headers and summaries, inodes, directory records, pointer blocks and <c>fs_cs</c>.
/// FFS stores them in the order of the host that made the volume; NetBSD's endian-
/// independent FFS (<c>FFS_EI</c>) and <c>newfs -B</c> read and write either. Bitmaps and
/// names are byte strings and have no order.
/// </summary>
internal readonly record struct UfsByteOrder(bool BigEndian) {
  public static readonly UfsByteOrder Little = new(false);
  public static readonly UfsByteOrder Big = new(true);

  public short I16(ReadOnlySpan<byte> s) => BigEndian ? BinaryPrimitives.ReadInt16BigEndian(s) : BinaryPrimitives.ReadInt16LittleEndian(s);
  public ushort U16(ReadOnlySpan<byte> s) => BigEndian ? BinaryPrimitives.ReadUInt16BigEndian(s) : BinaryPrimitives.ReadUInt16LittleEndian(s);
  public int I32(ReadOnlySpan<byte> s) => BigEndian ? BinaryPrimitives.ReadInt32BigEndian(s) : BinaryPrimitives.ReadInt32LittleEndian(s);
  public uint U32(ReadOnlySpan<byte> s) => BigEndian ? BinaryPrimitives.ReadUInt32BigEndian(s) : BinaryPrimitives.ReadUInt32LittleEndian(s);
  public long I64(ReadOnlySpan<byte> s) => BigEndian ? BinaryPrimitives.ReadInt64BigEndian(s) : BinaryPrimitives.ReadInt64LittleEndian(s);
  public ulong U64(ReadOnlySpan<byte> s) => BigEndian ? BinaryPrimitives.ReadUInt64BigEndian(s) : BinaryPrimitives.ReadUInt64LittleEndian(s);

  public void I16(Span<byte> s, short v) { if (BigEndian) BinaryPrimitives.WriteInt16BigEndian(s, v); else BinaryPrimitives.WriteInt16LittleEndian(s, v); }
  public void U16(Span<byte> s, ushort v) { if (BigEndian) BinaryPrimitives.WriteUInt16BigEndian(s, v); else BinaryPrimitives.WriteUInt16LittleEndian(s, v); }
  public void I32(Span<byte> s, int v) { if (BigEndian) BinaryPrimitives.WriteInt32BigEndian(s, v); else BinaryPrimitives.WriteInt32LittleEndian(s, v); }
  public void U32(Span<byte> s, uint v) { if (BigEndian) BinaryPrimitives.WriteUInt32BigEndian(s, v); else BinaryPrimitives.WriteUInt32LittleEndian(s, v); }
  public void I64(Span<byte> s, long v) { if (BigEndian) BinaryPrimitives.WriteInt64BigEndian(s, v); else BinaryPrimitives.WriteInt64LittleEndian(s, v); }
  public void U64(Span<byte> s, ulong v) { if (BigEndian) BinaryPrimitives.WriteUInt64BigEndian(s, v); else BinaryPrimitives.WriteUInt64LittleEndian(s, v); }
}
