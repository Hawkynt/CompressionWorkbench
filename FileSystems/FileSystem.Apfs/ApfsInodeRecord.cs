#pragma warning disable CS1591
using System.Buffers.Binary;
using static FileSystem.Apfs.ApfsConstants;

namespace FileSystem.Apfs;

/// <summary>
/// The records that make a file a file: its inode, and the count of who shares its
/// data stream.
/// </summary>
/// <remarks>
/// <para>An APFS inode record has a fixed part and, after it, extended fields. A
/// regular file's length is in one of those — the data stream — and not in the fixed
/// part at all. A reader that finds no such field reports the file as empty however
/// many extents it has, so a file written without one exists, is named, is listed,
/// and reads back as nothing.</para>
///
/// <para>The stream also has to be accounted for: a separate record counts how many
/// inodes share it. A driver looks that up before it will open the file and treats
/// its absence as corruption, not as "no one else has it".</para>
///
/// <para>Both the writer and the in-place modifier build these, which is why they
/// live here rather than in either.</para>
/// </remarks>
internal static class ApfsInodeRecord {

  // Extended-field layout: a two-field blob header, then one four-byte descriptor
  // per field, then the values, each padded to eight bytes.
  private const int XfBlobHeader = 4;
  private const int XFieldSize = 4;
  private const ushort DstreamSize = 40;
  private const byte InoExtTypeDstream = 8;
  private const byte XfSystemField = 0x20;

  /// <summary>The fixed part of an inode record, before any extended field.</summary>
  private const int FixedLength = 92;

  private const ushort DirPermissions = 0x1ED;   // 0755
  private const ushort FilePermissions = 0x1A4;  // 0644

  /// <summary>Builds one inode record's value.</summary>
  /// <param name="ino">The inode's own number.</param>
  /// <param name="parentId">The directory it belongs to.</param>
  /// <param name="size">Its length in bytes; zero for a directory.</param>
  /// <param name="isDir">Whether it is a directory.</param>
  /// <param name="nchildren">Children for a directory, or link count for a file.</param>
  /// <param name="internalFlags">Flags the filesystem keeps for itself.</param>
  internal static byte[] BuildValue(ulong ino, ulong parentId, long size, bool isDir, uint nchildren,
      ulong internalFlags = 0) {
    var xfields = isDir ? 0 : XfBlobHeader + XFieldSize + DstreamSize;
    var v = new byte[FixedLength + xfields];

    BinaryPrimitives.WriteUInt64LittleEndian(v, parentId);
    BinaryPrimitives.WriteUInt64LittleEndian(v.AsSpan(8), ino); // private_id = own inode number
    var nowNs = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1_000_000UL;
    BinaryPrimitives.WriteUInt64LittleEndian(v.AsSpan(16), nowNs); // create_time
    BinaryPrimitives.WriteUInt64LittleEndian(v.AsSpan(24), nowNs); // mod_time
    BinaryPrimitives.WriteUInt64LittleEndian(v.AsSpan(32), nowNs); // change_time
    BinaryPrimitives.WriteUInt64LittleEndian(v.AsSpan(40), nowNs); // access_time
    BinaryPrimitives.WriteUInt64LittleEndian(v.AsSpan(48), internalFlags);
    BinaryPrimitives.WriteUInt32LittleEndian(v.AsSpan(56), nchildren); // nchildren (dir) or nlink (file)
    BinaryPrimitives.WriteUInt32LittleEndian(v.AsSpan(60), 0);     // default_protection_class
    BinaryPrimitives.WriteUInt32LittleEndian(v.AsSpan(64), 0);     // write_generation_counter
    BinaryPrimitives.WriteUInt32LittleEndian(v.AsSpan(68), 0);     // bsd_flags
    BinaryPrimitives.WriteUInt32LittleEndian(v.AsSpan(72), 0);     // owner
    BinaryPrimitives.WriteUInt32LittleEndian(v.AsSpan(76), 0);     // group
    // The permission bits belong here too: a mode of nothing but the file type is a
    // directory no one may enter and a file no one may read.
    BinaryPrimitives.WriteUInt16LittleEndian(v.AsSpan(80),
      (ushort)(isDir ? S_IFDIR | DirPermissions : S_IFREG | FilePermissions));
    BinaryPrimitives.WriteUInt16LittleEndian(v.AsSpan(82), 0);     // pad1
    BinaryPrimitives.WriteUInt64LittleEndian(v.AsSpan(84), (ulong)size); // uncompressed_size

    if (isDir)
      return v;

    var x = v.AsSpan(FixedLength);
    BinaryPrimitives.WriteUInt16LittleEndian(x, 1);                 // one extended field
    BinaryPrimitives.WriteUInt16LittleEndian(x[2..], DstreamSize);  // bytes of value data
    x[4] = InoExtTypeDstream;
    x[5] = XfSystemField;
    BinaryPrimitives.WriteUInt16LittleEndian(x[6..], DstreamSize);

    var ds = x[(XfBlobHeader + XFieldSize)..];
    BinaryPrimitives.WriteUInt64LittleEndian(ds, (ulong)size);      // size
    // Allocated size is what the extents cover, which is whole blocks.
    BinaryPrimitives.WriteUInt64LittleEndian(ds[8..],
      (ulong)((size + DEFAULT_BLOCK_SIZE - 1) / DEFAULT_BLOCK_SIZE) * DEFAULT_BLOCK_SIZE);
    BinaryPrimitives.WriteUInt64LittleEndian(ds[16..], 0);          // default_crypto_id
    BinaryPrimitives.WriteUInt64LittleEndian(ds[24..], (ulong)size); // total_bytes_written
    BinaryPrimitives.WriteUInt64LittleEndian(ds[32..], 0);          // total_bytes_read
    return v;
  }

  /// <summary>The key of the record counting who shares an inode's data stream.</summary>
  internal static byte[] BuildDstreamIdKey(ulong ino) {
    var k = new byte[8];
    BinaryPrimitives.WriteUInt64LittleEndian(k, ino | ((ulong)APFS_TYPE_DSTREAM_ID << 60));
    return k;
  }

  /// <summary>That record's value: how many inodes hold the stream.</summary>
  internal static byte[] BuildDstreamIdValue(uint refCount) {
    var v = new byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(v, refCount);
    return v;
  }
}
