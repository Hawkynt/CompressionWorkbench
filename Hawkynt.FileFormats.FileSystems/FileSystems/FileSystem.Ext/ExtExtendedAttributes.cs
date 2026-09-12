#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Core.DiskImage;

namespace FileSystem.Ext;

/// <summary>
/// Reads ext2/3/4 extended attributes and performs conservative in-inode mutations.
/// </summary>
/// <remarks>
/// <para>
/// ext stores small xattrs after the extended inode fields and may store additional
/// attributes in one external xattr block referenced by <c>i_file_acl</c>. Both forms
/// are readable here. Mutations deliberately target only the in-inode form: growing
/// or rewriting an external xattr block also requires block allocation/reference-count
/// and metadata-checksum publication, so unsupported cases fail closed.
/// </para>
/// <para>
/// The implementation follows the Linux ext4 on-disk documentation. No kernel
/// implementation code is copied.
/// </para>
/// </remarks>
public static class ExtExtendedAttributes {
  private const int SuperblockOffset = 1024;
  private const ushort ExtMagic = 0xEF53;
  private const uint XattrMagic = 0xEA020000;
  private const uint Incompat64Bit = 0x0080;
  private const uint IncompatCsumSeed = 0x2000;
  private const uint RoCompatMetadataCsum = 0x0400;
  private const int ClassicInodeSize = 128;
  private const int EntryHeaderSize = 16;

  /// <summary>Reads all directly stored xattrs for <paramref name="path"/>.</summary>
  public static IReadOnlyDictionary<string, byte[]> Read(Stream image, string path) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(path);
    EnsureReadableSeekable(image);

    var original = image.Position;
    try {
      var geometry = Geometry.Read(image);
      var inodeNumber = ResolveInode(image, path, geometry.RootInode);
      var inodeOffset = geometry.InodeOffset(image, inodeNumber);
      var inode = ReadAt(image, inodeOffset, geometry.InodeSize);
      var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);

      ReadInline(inode, result);

      var externalBlock = ExternalXattrBlock(inode, geometry.Has64Bit);
      if (externalBlock != 0) {
        var blockOffset = checked((long)externalBlock * geometry.BlockSize);
        if (blockOffset < 0 || blockOffset + geometry.BlockSize > image.Length)
          throw new InvalidDataException("ext: external xattr block lies outside the image.");
        var block = ReadAt(image, blockOffset, geometry.BlockSize);
        if (BinaryPrimitives.ReadUInt32LittleEndian(block) != XattrMagic)
          throw new InvalidDataException("ext: i_file_acl does not reference an ext xattr block.");
        ReadEntries(block, EntryHeaderOffset: 32, ValueBaseOffset: 0, result);
      }

      return result;
    } finally {
      image.Position = original;
    }
  }

  /// <summary>
  /// Creates or replaces one in-inode xattr. External-block and EA-inode values
  /// are never rewritten by this conservative mutator.
  /// </summary>
  public static void Set(Stream image, string path, string name, ReadOnlySpan<byte> value) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(path);
    ArgumentException.ThrowIfNullOrWhiteSpace(name);
    EnsureWritableSeekable(image);

    var original = image.Position;
    try {
      var geometry = Geometry.Read(image);
      var inodeNumber = ResolveInode(image, path, geometry.RootInode);
      var inodeOffset = geometry.InodeOffset(image, inodeNumber);
      var inode = ReadAt(image, inodeOffset, geometry.InodeSize);

      var externalBlock = ExternalXattrBlock(inode, geometry.Has64Bit);
      if (externalBlock != 0) {
        var external = ReadExternal(image, geometry, externalBlock);
        if (external.ContainsKey(name))
          throw new NotSupportedException(
            $"ext: xattr '{name}' is stored in an external xattr block; external-block mutation is intentionally unsupported.");
      }

      var attributes = ReadInlineDictionary(inode);
      attributes[name] = value.ToArray();
      WriteInline(inode, attributes);
      StampInodeIfNeeded(geometry, inodeNumber, inode);
      WriteAt(image, inodeOffset, inode);
      image.Flush();
    } finally {
      image.Position = original;
    }
  }

  /// <summary>Removes one in-inode xattr, returning false when it was absent.</summary>
  public static bool Remove(Stream image, string path, string name) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(path);
    ArgumentException.ThrowIfNullOrWhiteSpace(name);
    EnsureWritableSeekable(image);

    var original = image.Position;
    try {
      var geometry = Geometry.Read(image);
      var inodeNumber = ResolveInode(image, path, geometry.RootInode);
      var inodeOffset = geometry.InodeOffset(image, inodeNumber);
      var inode = ReadAt(image, inodeOffset, geometry.InodeSize);

      var attributes = ReadInlineDictionary(inode);
      if (attributes.Remove(name)) {
        WriteInline(inode, attributes);
        StampInodeIfNeeded(geometry, inodeNumber, inode);
        WriteAt(image, inodeOffset, inode);
        image.Flush();
        return true;
      }

      var externalBlock = ExternalXattrBlock(inode, geometry.Has64Bit);
      if (externalBlock != 0 && ReadExternal(image, geometry, externalBlock).ContainsKey(name))
        throw new NotSupportedException(
          $"ext: xattr '{name}' is stored in an external xattr block; external-block mutation is intentionally unsupported.");

      return false;
    } finally {
      image.Position = original;
    }
  }

  private static Dictionary<string, byte[]> ReadInlineDictionary(byte[] inode) {
    var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
    ReadInline(inode, result);
    return result;
  }

  private static void ReadInline(byte[] inode, Dictionary<string, byte[]> result) {
    if (inode.Length <= ClassicInodeSize) return;
    var extraIsize = BinaryPrimitives.ReadUInt16LittleEndian(inode.AsSpan(ClassicInodeSize));
    var headerOffset = ClassicInodeSize + extraIsize;
    if (headerOffset < ClassicInodeSize || headerOffset + 4 > inode.Length) return;
    if (BinaryPrimitives.ReadUInt32LittleEndian(inode.AsSpan(headerOffset)) != XattrMagic) return;

    // For inode-body xattrs e_value_offs is relative to the first xattr entry,
    // immediately after the four-byte ibody header.
    var firstEntry = headerOffset + 4;
    ReadEntries(inode, firstEntry, firstEntry, result);
  }

  private static Dictionary<string, byte[]> ReadExternal(Stream image, Geometry geometry, ulong blockNumber) {
    var offset = checked((long)blockNumber * geometry.BlockSize);
    if (offset < 0 || offset + geometry.BlockSize > image.Length)
      throw new InvalidDataException("ext: external xattr block lies outside the image.");
    var block = ReadAt(image, offset, geometry.BlockSize);
    if (BinaryPrimitives.ReadUInt32LittleEndian(block) != XattrMagic)
      throw new InvalidDataException("ext: invalid external xattr block magic.");
    var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
    ReadEntries(block, 32, 0, result);
    return result;
  }

  private static void ReadEntries(
      byte[] storage,
      int EntryHeaderOffset,
      int ValueBaseOffset,
      Dictionary<string, byte[]> result) {
    var pos = EntryHeaderOffset;
    while (pos + EntryHeaderSize <= storage.Length) {
      // Four zero bytes terminate the entry array.
      if (BinaryPrimitives.ReadUInt32LittleEndian(storage.AsSpan(pos)) == 0) break;

      var nameLength = storage[pos];
      var nameIndex = storage[pos + 1];
      var valueOffset = BinaryPrimitives.ReadUInt16LittleEndian(storage.AsSpan(pos + 2));
      var valueInode = BinaryPrimitives.ReadUInt32LittleEndian(storage.AsSpan(pos + 4));
      var valueSize = BinaryPrimitives.ReadUInt32LittleEndian(storage.AsSpan(pos + 8));
      var entryLength = Align4(EntryHeaderSize + nameLength);
      if (nameLength == 0 || pos + entryLength > storage.Length)
        throw new InvalidDataException("ext: malformed xattr entry.");
      if (valueInode != 0)
        throw new NotSupportedException("ext: EA-inode xattr values are not supported by the image accessor.");
      if (valueSize > int.MaxValue)
        throw new InvalidDataException("ext: xattr value is too large.");

      var valueAt = checked(ValueBaseOffset + valueOffset);
      if (valueAt < 0 || valueAt + (long)valueSize > storage.Length)
        throw new InvalidDataException("ext: xattr value lies outside its storage region.");

      var suffix = Encoding.UTF8.GetString(storage, pos + EntryHeaderSize, nameLength);
      var fullName = ExpandName(nameIndex, suffix);
      var value = storage.AsSpan(valueAt, (int)valueSize).ToArray();
      result[fullName] = value;
      pos += entryLength;
    }
  }

  private static void WriteInline(byte[] inode, IReadOnlyDictionary<string, byte[]> attributes) {
    if (inode.Length <= ClassicInodeSize)
      throw new NotSupportedException("ext: 128-byte inodes have no in-inode xattr area.");

    var extraIsize = BinaryPrimitives.ReadUInt16LittleEndian(inode.AsSpan(ClassicInodeSize));
    if (extraIsize == 0)
      throw new NotSupportedException("ext: inode does not declare an extended inode area for inline xattrs.");

    var headerOffset = ClassicInodeSize + extraIsize;
    if (headerOffset < ClassicInodeSize || headerOffset + 4 > inode.Length)
      throw new InvalidDataException("ext: invalid i_extra_isize leaves no xattr body header.");

    var region = inode.AsSpan(headerOffset);
    region.Clear();
    if (attributes.Count == 0) return;

    BinaryPrimitives.WriteUInt32LittleEndian(region, XattrMagic);
    var firstEntry = headerOffset + 4;
    var entryPos = firstEntry;
    var valuePos = inode.Length;

    foreach (var pair in attributes.OrderBy(pair => pair.Key, StringComparer.Ordinal)) {
      var (nameIndex, suffix) = CompressName(pair.Key);
      var nameBytes = Encoding.UTF8.GetBytes(suffix);
      if (nameBytes.Length is 0 or > byte.MaxValue)
        throw new ArgumentException($"ext: xattr name '{pair.Key}' is not representable.");
      var value = pair.Value ?? [];

      valuePos = AlignDown4(valuePos - value.Length);
      var entryLength = Align4(EntryHeaderSize + nameBytes.Length);
      if (entryPos + entryLength + 4 > valuePos)
        throw new NotSupportedException(
          $"ext: xattrs do not fit in inode {inode.Length}-byte inline storage; external-block allocation is not implemented.");

      var relativeValueOffset = valuePos - firstEntry;
      if ((uint)relativeValueOffset > ushort.MaxValue)
        throw new NotSupportedException("ext: inline xattr value offset exceeds the on-disk field width.");

      inode[entryPos] = (byte)nameBytes.Length;
      inode[entryPos + 1] = nameIndex;
      BinaryPrimitives.WriteUInt16LittleEndian(inode.AsSpan(entryPos + 2), (ushort)relativeValueOffset);
      BinaryPrimitives.WriteUInt32LittleEndian(inode.AsSpan(entryPos + 4), 0); // e_value_inum
      BinaryPrimitives.WriteUInt32LittleEndian(inode.AsSpan(entryPos + 8), checked((uint)value.Length));
      BinaryPrimitives.WriteUInt32LittleEndian(inode.AsSpan(entryPos + 12), 0); // e_hash optional for ibody attrs
      nameBytes.CopyTo(inode.AsSpan(entryPos + EntryHeaderSize));
      value.CopyTo(inode.AsSpan(valuePos));
      entryPos += entryLength;
    }

    // Four zero bytes after the final entry terminate the entry array; the region
    // was cleared before encoding, so no additional write is needed.
  }

  private static ulong ExternalXattrBlock(ReadOnlySpan<byte> inode, bool has64Bit) {
    if (inode.Length < 108) return 0;
    var low = BinaryPrimitives.ReadUInt32LittleEndian(inode[0x68..]);
    ulong high = 0;
    // Linux ext4 inode osd2 starts at 0x74; l_i_file_acl_high is the u16 at 0x76.
    if (has64Bit && inode.Length >= 0x78)
      high = BinaryPrimitives.ReadUInt16LittleEndian(inode[0x76..]);
    return low | high << 32;
  }

  private static string ExpandName(byte index, string suffix) => index switch {
    1 => "user." + suffix,
    2 => "system.posix_acl_access" + suffix,
    3 => "system.posix_acl_default" + suffix,
    4 => "trusted." + suffix,
    6 => "security." + suffix,
    7 => "system." + suffix,
    _ => $"ext.index{index}." + suffix,
  };

  private static (byte Index, string Suffix) CompressName(string name) {
    if (name.StartsWith("user.", StringComparison.Ordinal)) return (1, name[5..]);
    if (name.Equals("system.posix_acl_access", StringComparison.Ordinal)) return (2, "");
    if (name.Equals("system.posix_acl_default", StringComparison.Ordinal)) return (3, "");
    if (name.StartsWith("trusted.", StringComparison.Ordinal)) return (4, name[8..]);
    if (name.StartsWith("security.", StringComparison.Ordinal)) return (6, name[9..]);
    if (name.StartsWith("system.", StringComparison.Ordinal)) return (7, name[7..]);
    throw new ArgumentException(
      $"ext: xattr '{name}' has no supported namespace prefix (user., trusted., security., system.).",
      nameof(name));
  }

  private static uint ResolveInode(Stream image, string path, uint rootInode) {
    var normalized = Normalize(path);
    if (normalized.Length == 0) return rootInode;
    image.Position = 0;
    using var reader = new ExtReader(image, leaveOpen: true);
    var entry = reader.Entries.FirstOrDefault(entry =>
      string.Equals(Normalize(entry.Name), normalized, StringComparison.Ordinal));
    return entry?.Inode ?? throw new FileNotFoundException($"ext entry not found: {path}");
  }

  private static string Normalize(string path) => path.Replace('\\', '/').Trim('/');

  private static void StampInodeIfNeeded(Geometry geometry, uint inodeNumber, byte[] inode) {
    if (!geometry.HasMetadataChecksum) return;
    var generation = inode.Length >= 0x68
      ? BinaryPrimitives.ReadUInt32LittleEndian(inode.AsSpan(0x64))
      : 0;
    var inodeSeed = ExtChecksums.InodeSeed(geometry.ChecksumSeed, inodeNumber, generation);
    ExtChecksums.StampInode(inode, geometry.InodeSize, inodeSeed);
  }

  private static byte[] ReadAt(Stream image, long offset, int length) {
    var data = new byte[length];
    image.Position = offset;
    image.ReadExactly(data);
    return data;
  }

  private static void WriteAt(Stream image, long offset, ReadOnlySpan<byte> data) {
    image.Position = offset;
    image.Write(data);
  }

  private static int Align4(int value) => checked((value + 3) & ~3);
  private static int AlignDown4(int value) => value & ~3;

  private static void EnsureReadableSeekable(Stream image) {
    if (!image.CanRead || !image.CanSeek)
      throw new ArgumentException("ext xattr access requires a readable, seekable image.", nameof(image));
  }

  private static void EnsureWritableSeekable(Stream image) {
    EnsureReadableSeekable(image);
    if (!image.CanWrite)
      throw new ArgumentException("ext xattr mutation requires a writable image.", nameof(image));
  }

  private readonly record struct Geometry(
    int BlockSize,
    uint InodesPerGroup,
    int InodeSize,
    uint FirstDataBlock,
    int DescriptorSize,
    bool Has64Bit,
    bool HasMetadataChecksum,
    uint ChecksumSeed,
    uint RootInode) {

    public static Geometry Read(Stream image) {
      if (image.Length < SuperblockOffset + 1024)
        throw new InvalidDataException("ext: image is too small for its superblock.");
      var sb = ReadAt(image, SuperblockOffset, 1024);
      if (BinaryPrimitives.ReadUInt16LittleEndian(sb.AsSpan(56)) != ExtMagic)
        throw new InvalidDataException("ext: invalid superblock magic.");

      var blockSize = checked(1024 << (int)BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(24)));
      var inodesPerGroup = BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(40));
      var inodeSize = BinaryPrimitives.ReadUInt16LittleEndian(sb.AsSpan(88));
      if (inodeSize == 0) inodeSize = ClassicInodeSize;
      var firstDataBlock = BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(20));
      var featureIncompat = BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(96));
      var featureRoCompat = BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(100));
      var descriptorSize = ExtBlockGroupGeometry.DescriptorSize(sb);
      var checksumSeed = (featureIncompat & IncompatCsumSeed) != 0
        ? BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(624))
        : ExtChecksums.SeedFromUuid(sb.AsSpan(104, 16));

      if (blockSize is < 1024 or > 65536 || (blockSize & (blockSize - 1)) != 0)
        throw new InvalidDataException($"ext: invalid block size {blockSize}.");
      if (inodesPerGroup == 0)
        throw new InvalidDataException("ext: superblock declares zero inodes per group.");
      if (inodeSize < ClassicInodeSize)
        throw new InvalidDataException($"ext: invalid inode size {inodeSize}.");

      return new Geometry(
        blockSize,
        inodesPerGroup,
        inodeSize,
        firstDataBlock,
        descriptorSize,
        (featureIncompat & Incompat64Bit) != 0,
        (featureRoCompat & RoCompatMetadataCsum) != 0,
        checksumSeed,
        RootInode: 2);
    }

    public long InodeOffset(Stream image, uint inodeNumber) {
      if (inodeNumber == 0)
        throw new ArgumentOutOfRangeException(nameof(inodeNumber));
      var group = (inodeNumber - 1) / this.InodesPerGroup;
      var index = (inodeNumber - 1) % this.InodesPerGroup;
      var bgdtBlock = this.FirstDataBlock + 1;
      var descriptorOffset = checked((long)bgdtBlock * this.BlockSize + (long)group * this.DescriptorSize);
      if (descriptorOffset + this.DescriptorSize > image.Length)
        throw new InvalidDataException("ext: inode group descriptor lies outside the image.");
      var descriptor = ReadAt(image, descriptorOffset, this.DescriptorSize);
      ulong tableBlock = BinaryPrimitives.ReadUInt32LittleEndian(descriptor.AsSpan(8));
      if (this.Has64Bit && descriptor.Length >= 44)
        tableBlock |= (ulong)BinaryPrimitives.ReadUInt32LittleEndian(descriptor.AsSpan(40)) << 32;
      var offset = checked((long)tableBlock * this.BlockSize + (long)index * this.InodeSize);
      if (offset < 0 || offset + this.InodeSize > image.Length)
        throw new InvalidDataException("ext: inode lies outside the image.");
      return offset;
    }
  }
}
