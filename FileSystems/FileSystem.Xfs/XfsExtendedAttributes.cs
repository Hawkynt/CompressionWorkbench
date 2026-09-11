#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Core.Checksums;

namespace FileSystem.Xfs;

/// <summary>
/// Reads and mutates XFS short-form extended attributes stored in an inode's
/// attribute fork.
/// </summary>
/// <remarks>
/// <para>
/// XFS can promote an attribute fork from short-form to leaf/node btrees and can
/// place large values in remote blocks. This accessor intentionally stops before
/// that boundary: local inode attributes are fully round-trippable, while leaf,
/// node, and remote-value mutation fails closed instead of rewriting structures
/// the current workbench does not yet allocate transactionally.
/// </para>
/// <para>
/// Layout and namespace constants are taken from the published XFS on-disk
/// specification; no Linux implementation code is copied.
/// </para>
/// </remarks>
public static class XfsExtendedAttributes {
  private const uint XfsMagic = 0x58465342; // XFSB
  private const ushort InodeMagic = 0x494E; // IN
  private const byte FormatLocal = 1;
  private const byte FormatExtents = 2;
  private const byte FormatBtree = 3;
  private const byte AttrRoot = 0x02;
  private const byte AttrSecure = 0x04;
  private const byte AttrParent = 0x08;
  private const byte AttrIncomplete = 0x80;
  private const int DiCrcOffset = 100;
  private const int ShortFormHeaderSize = 4; // be16 totsize, u8 count, u8 padding
  private const int ShortFormEntryHeaderSize = 3; // namelen, valuelen, flags

  /// <summary>Reads all short-form xattrs for <paramref name="path"/>.</summary>
  public static IReadOnlyDictionary<string, byte[]> Read(Stream image, string path) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(path);
    EnsureReadableSeekable(image);

    var original = image.Position;
    try {
      var geometry = Geometry.Read(image);
      var inodeNumber = ResolveInode(image, path, geometry.RootInode);
      var inode = ReadAt(image, geometry.InodeOffset(inodeNumber), geometry.InodeSize);
      return ParseShortForm(inode, geometry).ToDictionary(
        attribute => ExpandName(attribute.Flags, attribute.Name),
        attribute => attribute.Value,
        StringComparer.Ordinal);
    } finally {
      image.Position = original;
    }
  }

  /// <summary>
  /// Creates or replaces a short-form xattr. If the resulting fork does not fit
  /// in the inode, the call fails without modifying the image.
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
      var inodeOffset = geometry.InodeOffset(inodeNumber);
      var inode = ReadAt(image, inodeOffset, geometry.InodeSize);
      ValidateInode(inode);

      var attributes = ParseShortForm(inode, geometry).ToList();
      var (namespaceFlags, suffix) = CompressName(name);
      var suffixBytes = Encoding.UTF8.GetBytes(suffix);
      if (suffixBytes.Length is 0 or > byte.MaxValue)
        throw new ArgumentException($"XFS xattr name '{name}' is not representable in short-form storage.", nameof(name));
      if (value.Length > byte.MaxValue)
        throw new NotSupportedException("XFS short-form xattr values are limited to 255 bytes; remote-value allocation is not implemented.");

      var index = attributes.FindIndex(attribute =>
        SameNamespace(attribute.Flags, namespaceFlags) &&
        string.Equals(attribute.Name, suffix, StringComparison.Ordinal));
      if (index >= 0) {
        var existing = attributes[index];
        attributes[index] = existing with { Value = value.ToArray() };
      } else {
        attributes.Add(new ShortFormAttribute(suffix, value.ToArray(), namespaceFlags));
      }

      WriteShortForm(inode, geometry, attributes);
      StampInodeCrcIfNeeded(inode, geometry);
      WriteAt(image, inodeOffset, inode);
      image.Flush();
    } finally {
      image.Position = original;
    }
  }

  /// <summary>Removes one short-form xattr, returning false when it was absent.</summary>
  public static bool Remove(Stream image, string path, string name) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(path);
    ArgumentException.ThrowIfNullOrWhiteSpace(name);
    EnsureWritableSeekable(image);

    var original = image.Position;
    try {
      var geometry = Geometry.Read(image);
      var inodeNumber = ResolveInode(image, path, geometry.RootInode);
      var inodeOffset = geometry.InodeOffset(inodeNumber);
      var inode = ReadAt(image, inodeOffset, geometry.InodeSize);
      ValidateInode(inode);

      var attributes = ParseShortForm(inode, geometry).ToList();
      var (namespaceFlags, suffix) = CompressName(name);
      var removed = attributes.RemoveAll(attribute =>
        SameNamespace(attribute.Flags, namespaceFlags) &&
        string.Equals(attribute.Name, suffix, StringComparison.Ordinal)) > 0;
      if (!removed) return false;

      WriteShortForm(inode, geometry, attributes);
      StampInodeCrcIfNeeded(inode, geometry);
      WriteAt(image, inodeOffset, inode);
      image.Flush();
      return true;
    } finally {
      image.Position = original;
    }
  }

  private static IReadOnlyList<ShortFormAttribute> ParseShortForm(byte[] inode, Geometry geometry) {
    ValidateInode(inode);
    var forkoff = inode[82];
    if (forkoff == 0) return [];

    var attrFormat = inode[83];
    if (attrFormat != FormatLocal)
      throw new NotSupportedException(
        $"XFS: attribute fork format {attrFormat} is not short-form; leaf/btree xattr access is not implemented.");

    var start = checked(geometry.CoreSize + forkoff * 8);
    if (start + ShortFormHeaderSize > inode.Length)
      throw new InvalidDataException("XFS: short-form attribute fork begins outside the inode.");

    var totalSize = BinaryPrimitives.ReadUInt16BigEndian(inode.AsSpan(start));
    var count = inode[start + 2];
    if (totalSize < ShortFormHeaderSize || start + totalSize > inode.Length)
      throw new InvalidDataException("XFS: invalid short-form xattr size.");

    var result = new List<ShortFormAttribute>(count);
    var pos = start + ShortFormHeaderSize;
    var end = start + totalSize;
    for (var i = 0; i < count; ++i) {
      if (pos + ShortFormEntryHeaderSize > end)
        throw new InvalidDataException("XFS: truncated short-form xattr entry.");
      var nameLength = inode[pos];
      var valueLength = inode[pos + 1];
      var flags = inode[pos + 2];
      var recordLength = ShortFormEntryHeaderSize + nameLength + valueLength;
      if (nameLength == 0 || pos + recordLength > end)
        throw new InvalidDataException("XFS: malformed short-form xattr entry.");

      var name = Encoding.UTF8.GetString(inode, pos + ShortFormEntryHeaderSize, nameLength);
      var value = inode.AsSpan(pos + ShortFormEntryHeaderSize + nameLength, valueLength).ToArray();
      result.Add(new ShortFormAttribute(name, value, flags));
      pos += recordLength;
    }

    return result;
  }

  private static void WriteShortForm(
      byte[] inode,
      Geometry geometry,
      IReadOnlyList<ShortFormAttribute> attributes) {
    ValidateInode(inode);

    foreach (var attribute in attributes) {
      if ((attribute.Flags & (AttrParent | AttrIncomplete)) != 0)
        throw new NotSupportedException(
          "XFS: parent-pointer or incomplete xattrs cannot be rewritten by the short-form accessor.");
    }

    if (attributes.Count == 0) {
      var oldForkoff = inode[82];
      if (oldForkoff != 0) {
        var oldStart = checked(geometry.CoreSize + oldForkoff * 8);
        if (oldStart < inode.Length) inode.AsSpan(oldStart).Clear();
      }
      BinaryPrimitives.WriteUInt16BigEndian(inode.AsSpan(80), 0); // di_anextents
      inode[82] = 0;                                              // di_forkoff
      inode[83] = 0;                                              // di_aformat
      return;
    }

    if (attributes.Count > byte.MaxValue)
      throw new NotSupportedException("XFS short-form xattrs cannot encode more than 255 attributes.");

    var encodedSize = ShortFormHeaderSize;
    foreach (var attribute in attributes) {
      var nameLength = Encoding.UTF8.GetByteCount(attribute.Name);
      if (nameLength is 0 or > byte.MaxValue || attribute.Value.Length > byte.MaxValue)
        throw new NotSupportedException("XFS xattr cannot be represented in short-form storage.");
      encodedSize = checked(encodedSize + ShortFormEntryHeaderSize + nameLength + attribute.Value.Length);
    }

    var existingForkoff = inode[82];
    int forkoff;
    if (existingForkoff != 0) {
      if (inode[83] != FormatLocal)
        throw new NotSupportedException("XFS: existing non-local attribute fork cannot be rewritten as short-form.");
      forkoff = existingForkoff;
    } else {
      var dataBytes = DataForkBytesUsed(inode, geometry);
      forkoff = checked((dataBytes + 7) / 8);
      if (forkoff == 0) forkoff = 1;
    }

    if (forkoff > byte.MaxValue)
      throw new NotSupportedException("XFS: data fork leaves no representable attribute-fork offset.");
    var start = checked(geometry.CoreSize + forkoff * 8);
    if (start + encodedSize > inode.Length)
      throw new NotSupportedException(
        "XFS: xattrs do not fit in the inode attribute fork; leaf/remote attribute allocation is not implemented.");

    // Build off-image first so a capacity/encoding failure cannot leave a half-written fork.
    var encoded = new byte[encodedSize];
    BinaryPrimitives.WriteUInt16BigEndian(encoded, checked((ushort)encodedSize));
    encoded[2] = checked((byte)attributes.Count);
    encoded[3] = 0;
    var pos = ShortFormHeaderSize;
    foreach (var attribute in attributes) {
      var nameBytes = Encoding.UTF8.GetBytes(attribute.Name);
      encoded[pos] = checked((byte)nameBytes.Length);
      encoded[pos + 1] = checked((byte)attribute.Value.Length);
      encoded[pos + 2] = attribute.Flags;
      nameBytes.AsSpan().CopyTo(encoded.AsSpan(pos + ShortFormEntryHeaderSize));
      attribute.Value.AsSpan().CopyTo(encoded.AsSpan(pos + ShortFormEntryHeaderSize + nameBytes.Length));
      pos += ShortFormEntryHeaderSize + nameBytes.Length + attribute.Value.Length;
    }

    // Preserve the data fork and replace only the attribute-fork region.
    inode.AsSpan(start).Clear();
    encoded.AsSpan().CopyTo(inode.AsSpan(start));
    BinaryPrimitives.WriteUInt16BigEndian(inode.AsSpan(80), 0); // short-form has no attr extents
    inode[82] = checked((byte)forkoff);
    inode[83] = FormatLocal;
  }

  private static int DataForkBytesUsed(byte[] inode, Geometry geometry) {
    var format = inode[5];
    return format switch {
      FormatLocal => checked((int)Math.Min(
        BinaryPrimitives.ReadUInt64BigEndian(inode.AsSpan(56)),
        (ulong)(inode.Length - geometry.CoreSize))),
      FormatExtents => checked((int)BinaryPrimitives.ReadUInt32BigEndian(inode.AsSpan(76)) * 16),
      FormatBtree => throw new NotSupportedException(
        "XFS: cannot introduce an attr fork beside a btree data fork without decoding its in-inode root size."),
      _ => 0,
    };
  }

  private static string ExpandName(byte flags, string suffix) {
    if ((flags & AttrRoot) != 0) return "trusted." + suffix;
    if ((flags & AttrSecure) != 0) return "security." + suffix;
    if ((flags & AttrParent) != 0) return "xfs.parent." + suffix;
    return "user." + suffix;
  }

  private static (byte Flags, string Suffix) CompressName(string name) {
    if (name.StartsWith("user.", StringComparison.Ordinal)) return (0, name[5..]);
    if (name.StartsWith("trusted.", StringComparison.Ordinal)) return (AttrRoot, name[8..]);
    if (name.StartsWith("security.", StringComparison.Ordinal)) return (AttrSecure, name[9..]);
    throw new ArgumentException(
      $"XFS xattr '{name}' has no supported namespace prefix (user., trusted., security.).",
      nameof(name));
  }

  private static bool SameNamespace(byte existingFlags, byte requestedFlags)
    => (existingFlags & (AttrRoot | AttrSecure | AttrParent)) == requestedFlags;

  private static ulong ResolveInode(Stream image, string path, ulong rootInode) {
    var normalized = Normalize(path);
    if (normalized.Length == 0) return rootInode;
    image.Position = 0;
    using var reader = new XfsReader(image, leaveOpen: true);
    var entry = reader.Entries.FirstOrDefault(entry =>
      string.Equals(Normalize(entry.Name), normalized, StringComparison.Ordinal));
    return entry is null
      ? throw new FileNotFoundException($"XFS entry not found: {path}")
      : checked((ulong)entry.InodeNumber);
  }

  private static string Normalize(string path) => path.Replace('\\', '/').Trim('/');

  private static void ValidateInode(ReadOnlySpan<byte> inode) {
    if (inode.Length < 100 || BinaryPrimitives.ReadUInt16BigEndian(inode) != InodeMagic)
      throw new InvalidDataException("XFS: invalid inode magic.");
  }

  private static void StampInodeCrcIfNeeded(byte[] inode, Geometry geometry) {
    if (!geometry.IsV5) return;
    inode.AsSpan(DiCrcOffset, sizeof(uint)).Clear();
    var crc = Crc32.Compute(inode, Crc32.Castagnoli);
    BinaryPrimitives.WriteUInt32LittleEndian(inode.AsSpan(DiCrcOffset), crc);
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

  private static void EnsureReadableSeekable(Stream image) {
    if (!image.CanRead || !image.CanSeek)
      throw new ArgumentException("XFS xattr access requires a readable, seekable image.", nameof(image));
  }

  private static void EnsureWritableSeekable(Stream image) {
    EnsureReadableSeekable(image);
    if (!image.CanWrite)
      throw new ArgumentException("XFS xattr mutation requires a writable image.", nameof(image));
  }

  private sealed record ShortFormAttribute(string Name, byte[] Value, byte Flags);

  private readonly record struct Geometry(
    uint BlockSize,
    ushort InodeSize,
    ulong RootInode,
    uint AgBlocks,
    byte AgBlockLog,
    int CoreSize,
    bool IsV5) {

    public static Geometry Read(Stream image) {
      if (image.Length < 512)
        throw new InvalidDataException("XFS: image too small for its superblock.");
      var sb = ReadAt(image, 0, 512);
      if (BinaryPrimitives.ReadUInt32BigEndian(sb) != XfsMagic)
        throw new InvalidDataException("XFS: invalid superblock magic.");

      var blockSize = BinaryPrimitives.ReadUInt32BigEndian(sb.AsSpan(4));
      var rootInode = BinaryPrimitives.ReadUInt64BigEndian(sb.AsSpan(56));
      var agBlocks = BinaryPrimitives.ReadUInt32BigEndian(sb.AsSpan(84));
      var version = BinaryPrimitives.ReadUInt16BigEndian(sb.AsSpan(100));
      var inodeSize = BinaryPrimitives.ReadUInt16BigEndian(sb.AsSpan(104));
      var agBlockLog = sb[124];
      if (blockSize == 0) blockSize = 4096;
      if (inodeSize == 0) inodeSize = 256;
      if (agBlocks == 0) agBlocks = checked((uint)(image.Length / blockSize));
      if (agBlockLog == 0) {
        var value = agBlocks;
        while (value > 1) { ++agBlockLog; value >>= 1; }
      }

      var isV5 = (version & 0xF) >= 5;
      return new Geometry(blockSize, inodeSize, rootInode, agBlocks, agBlockLog,
        CoreSize: isV5 ? 176 : 100,
        IsV5: isV5);
    }

    public long InodeOffset(ulong inodeNumber) {
      var inodesPerBlock = checked((int)(this.BlockSize / this.InodeSize));
      if (inodesPerBlock <= 0 || (inodesPerBlock & (inodesPerBlock - 1)) != 0)
        throw new InvalidDataException("XFS: unsupported inode geometry.");
      var inoPbLog = 0;
      for (var value = inodesPerBlock; value > 1; value >>= 1) ++inoPbLog;
      var aginoLog = this.AgBlockLog + inoPbLog;
      var agNumber = inodeNumber >> aginoLog;
      var agInode = inodeNumber & ((1UL << aginoLog) - 1);
      var block = agInode / (ulong)inodesPerBlock;
      var slot = agInode % (ulong)inodesPerBlock;
      return checked((long)((agNumber * this.AgBlocks + block) * this.BlockSize + slot * this.InodeSize));
    }
  }
}
