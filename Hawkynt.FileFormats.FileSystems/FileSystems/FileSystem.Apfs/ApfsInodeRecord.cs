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

  /// <summary>INO_EXT_TYPE_NAME — the name the inode is primarily known by.</summary>
  /// <remarks>
  /// Every inode carries it, directories included: apfsprogs reports one that
  /// does not as having "no name for primary link". A container mkfs.apfs builds
  /// gives its root the name "root" and its private directory "private-dir",
  /// each with flags 0x02 and a size that counts the terminating nul.
  /// </remarks>
  private const byte InoExtTypeName = 4;

  /// <summary>The flags a name field carries.</summary>
  private const byte XfDoNotCopy = 0x02;

  /// <summary>Every extended field's value is padded out to eight bytes.</summary>
  private static int PadTo8(int length) => (length + 7) & ~7;

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
      ulong internalFlags = 0, string name = "") {
    var nameBytes = System.Text.Encoding.UTF8.GetBytes(name);
    var nameSize = nameBytes.Length + 1;              // the nul is part of it
    var fieldCount = 1 + (isDir ? 0 : 1);             // the name, and a file's data stream
    var dataBytes = PadTo8(nameSize) + (isDir ? 0 : DstreamSize);
    var v = new byte[FixedLength + XfBlobHeader + fieldCount * XFieldSize + dataBytes];

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
    // uncompressed_size: what the file would be if it were compressed, and so a
    // field only a compressed file fills in. Nothing here is compressed, and
    // reporting the plain size in it is what apfsprogs calls an inode that
    // "should not report uncompressed size".
    BinaryPrimitives.WriteUInt64LittleEndian(v.AsSpan(84), 0);

    // The extended fields: a blob header, then one descriptor per field, then
    // the values in the same order, each padded out to eight bytes.
    var x = v.AsSpan(FixedLength);
    BinaryPrimitives.WriteUInt16LittleEndian(x, (ushort)fieldCount);
    BinaryPrimitives.WriteUInt16LittleEndian(x[2..], (ushort)dataBytes);

    x[4] = InoExtTypeName;
    x[5] = XfDoNotCopy;
    BinaryPrimitives.WriteUInt16LittleEndian(x[6..], (ushort)nameSize);
    if (!isDir) {
      x[8] = InoExtTypeDstream;
      x[9] = XfSystemField;
      BinaryPrimitives.WriteUInt16LittleEndian(x[10..], DstreamSize);
    }

    var values = x[(XfBlobHeader + fieldCount * XFieldSize)..];
    nameBytes.CopyTo(values);                                        // the nul is already zero

    if (isDir) return v;

    var ds = values[PadTo8(nameSize)..];
    BinaryPrimitives.WriteUInt64LittleEndian(ds, (ulong)size);      // size
    // Allocated size is what the extents cover, which is whole blocks.
    BinaryPrimitives.WriteUInt64LittleEndian(ds[8..],
      (ulong)((size + DEFAULT_BLOCK_SIZE - 1) / DEFAULT_BLOCK_SIZE) * DEFAULT_BLOCK_SIZE);
    BinaryPrimitives.WriteUInt64LittleEndian(ds[16..], 0);          // default_crypto_id
    BinaryPrimitives.WriteUInt64LittleEndian(ds[24..], (ulong)size); // total_bytes_written
    BinaryPrimitives.WriteUInt64LittleEndian(ds[32..], 0);          // total_bytes_read
    return v;
  }

  /// <summary>
  /// The length of the file this inode record describes.
  /// </summary>
  /// <remarks>
  /// It lives in the data-stream extended field, not in the fixed part: the
  /// <c>uncompressed_size</c> word there belongs to compressed files and is zero
  /// for everything else. Reading the length from that word worked only because
  /// this writer used to fill it in for every file, which is a thing no APFS
  /// volume does — so the size of a file on a volume from anywhere else read as
  /// nothing at all.
  /// </remarks>
  internal static long ReadDataStreamSize(ReadOnlySpan<byte> value) {
    if (value.Length < FixedLength + XfBlobHeader) return 0;

    var count = BinaryPrimitives.ReadUInt16LittleEndian(value[FixedLength..]);
    var descriptors = FixedLength + XfBlobHeader;
    var data = descriptors + count * XFieldSize;
    if (data > value.Length) return 0;

    for (var i = 0; i < count; ++i) {
      var at = descriptors + i * XFieldSize;
      var type = value[at];
      var size = BinaryPrimitives.ReadUInt16LittleEndian(value[(at + 2)..]);
      if (type == InoExtTypeDstream)
        return data + 8 <= value.Length ? (long)BinaryPrimitives.ReadUInt64LittleEndian(value[data..]) : 0;

      data += PadTo8(size);
      if (data > value.Length) return 0;
    }
    return 0;
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
