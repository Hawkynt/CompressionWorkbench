#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileSystem.Btrfs;

/// <summary>
/// Mount-grade logical file map for the single-device Btrfs profile understood
/// by this package. Unlike <see cref="BtrfsReader.ExtractTo"/>, this collector
/// merges EXTENT_DATA records across every FS-tree leaf before ordering them by
/// file offset, so a file cannot be reordered merely because its keys crossed a
/// leaf boundary. Missing logical ranges, explicit sparse extents and prealloc
/// extents read as zeroes. Compressed/encrypted/other-encoded records fail
/// closed until their decoder is implemented.
/// </summary>
internal static class BtrfsNativeFileMap {
  private const int SuperblockOffset = 0x10000;
  private const int SuperblockSize = 4096;
  private const int SystemChunkArrayOffset = 0x32B;
  private const byte ChunkItem = 228;
  private const byte RootItem = 132;
  private const byte ExtentData = 108;
  private const long FsTreeObjectId = 5;

  internal readonly record struct Segment(long FileOffset, long Length, long? PhysicalOffset) {
    public long End => checked(FileOffset + Length);
    public bool IsZero => PhysicalOffset == null;
  }

  internal sealed record Map(
    uint NodeSize,
    uint SectorSize,
    IReadOnlyDictionary<long, Segment[]> Files,
    bool UsesRealChunkMap);

  private readonly record struct Chunk(long Logical, long Physical, long Length);

  public static Map Read(Stream image, IReadOnlyList<BtrfsEntry> entries) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(entries);
    if (!image.CanRead || !image.CanSeek)
      throw new ArgumentException("Btrfs native mapping requires a readable, seekable image.", nameof(image));
    if (image.Length < SuperblockOffset + SuperblockSize)
      throw new InvalidDataException("Btrfs image is too small for its superblock.");

    var sb = ReadBytes(image, SuperblockOffset, SuperblockSize);
    if (!sb.AsSpan(0x40, 8).SequenceEqual("_BHRfS_M"u8))
      throw new InvalidDataException("Btrfs superblock magic is invalid.");

    var rootTreeLogical = BinaryPrimitives.ReadInt64LittleEndian(sb.AsSpan(0x50));
    var chunkTreeLogical = BinaryPrimitives.ReadInt64LittleEndian(sb.AsSpan(0x58));
    var sectorSize = BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(0x90));
    var nodeSize = BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(0x94));
    var sysChunkSize = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(sb.AsSpan(0xA0)));

    if (nodeSize is < 4096 or > 65536 || (nodeSize & (nodeSize - 1)) != 0)
      throw new NotSupportedException($"Btrfs node size {nodeSize:N0} is unsupported.");
    if (sectorSize is < 512 or > 65536 || (sectorSize & (sectorSize - 1)) != 0)
      throw new NotSupportedException($"Btrfs sector size {sectorSize:N0} is unsupported.");
    if (sysChunkSize < 0 || SystemChunkArrayOffset + sysChunkSize > sb.Length)
      throw new InvalidDataException("Btrfs sys_chunk_array exceeds the superblock.");

    var chunks = new List<Chunk>();
    ParseSystemChunkArray(sb.AsSpan(SystemChunkArrayOffset, sysChunkSize), chunks);
    if (chunks.Count == 0)
      throw new NotSupportedException(
        "Btrfs native mounting requires a real single-device sys_chunk_array; synthetic identity-mapped test images remain reader-only fixtures.");

    var chunkTreePhysical = LogicalToPhysical(chunks, chunkTreeLogical, image.Length);
    WalkChunkTree(image, chunkTreePhysical, nodeSize, chunks, new HashSet<long>());

    var rootTreePhysical = LogicalToPhysical(chunks, rootTreeLogical, image.Length);
    var fsRootLogical = FindFsRoot(image, rootTreePhysical, nodeSize, chunks, new HashSet<long>());
    if (fsRootLogical < 0)
      throw new InvalidDataException("Btrfs FS-tree root could not be resolved from the root tree.");

    var fsRootPhysical = LogicalToPhysical(chunks, fsRootLogical, image.Length);
    var leaves = new List<long>();
    CollectLeaves(image, fsRootPhysical, nodeSize, chunks, leaves, new HashSet<long>());
    if (leaves.Count == 0)
      throw new InvalidDataException("Btrfs FS tree contains no reachable leaves.");

    var byInode = new Dictionary<long, List<Segment>>();
    foreach (var leaf in leaves)
      CollectExtentRecords(image, leaf, nodeSize, chunks, byInode);

    var wanted = entries.Where(e => !e.IsDirectory)
      .GroupBy(e => e.Inode)
      .ToDictionary(g => g.Key, g => g.Max(e => e.Size));
    var result = new Dictionary<long, Segment[]>();

    foreach (var (inode, size) in wanted) {
      if (inode <= 0)
        throw new InvalidDataException($"Btrfs exposes invalid inode {inode}.");
      if (size < 0)
        throw new InvalidDataException($"Btrfs inode {inode} has a negative logical size.");
      if (size == 0) {
        result[inode] = [];
        continue;
      }
      if (!byInode.TryGetValue(inode, out var raw) || raw.Count == 0)
        throw new NotSupportedException($"Btrfs inode {inode} has no decoded EXTENT_DATA mapping.");

      var ordered = raw.OrderBy(s => s.FileOffset).ThenBy(s => s.Length).ToArray();
      long previousEnd = 0;
      foreach (var segment in ordered) {
        if (segment.FileOffset < 0 || segment.Length <= 0)
          throw new InvalidDataException($"Btrfs inode {inode} contains an invalid logical extent.");
        if (segment.FileOffset < previousEnd)
          throw new NotSupportedException($"Btrfs inode {inode} has overlapping logical extents.");
        previousEnd = segment.End;
        if (segment.PhysicalOffset is { } physical &&
            (physical < 0 || physical > image.Length - segment.Length))
          throw new InvalidDataException($"Btrfs inode {inode} extent lies outside the image.");
      }
      result[inode] = ordered;
    }

    return new Map(nodeSize, sectorSize, result, UsesRealChunkMap: true);
  }

  private static void ParseSystemChunkArray(ReadOnlySpan<byte> array, List<Chunk> chunks) {
    var pos = 0;
    while (pos + 17 + 48 <= array.Length) {
      var keyType = array[pos + 8];
      var logical = BinaryPrimitives.ReadInt64LittleEndian(array[(pos + 9)..]);
      pos += 17;
      if (keyType != ChunkItem)
        throw new NotSupportedException($"Btrfs sys_chunk_array contains unexpected key type {keyType}.");

      var length = BinaryPrimitives.ReadInt64LittleEndian(array[pos..]);
      var numStripes = BinaryPrimitives.ReadUInt16LittleEndian(array[(pos + 44)..]);
      var subStripes = BinaryPrimitives.ReadUInt16LittleEndian(array[(pos + 46)..]);
      pos += 48;
      if (numStripes != 1 || subStripes > 1)
        throw new NotSupportedException(
          $"Btrfs native mounting currently requires one physical stripe per chunk (found {numStripes}, sub_stripes={subStripes}).");
      if (pos + 32 > array.Length)
        throw new InvalidDataException("Btrfs sys_chunk_array stripe is truncated.");
      var physical = BinaryPrimitives.ReadInt64LittleEndian(array[(pos + 8)..]);
      AddChunk(chunks, logical, physical, length);
      pos += 32;
    }
    if (pos != array.Length && array[pos..].IndexOfAnyExcept((byte)0) >= 0)
      throw new InvalidDataException("Btrfs sys_chunk_array has trailing non-zero bytes.");
  }

  private static void WalkChunkTree(
      Stream image,
      long physical,
      uint nodeSize,
      List<Chunk> chunks,
      HashSet<long> visited) {
    if (!visited.Add(physical)) return;
    var node = ReadNode(image, physical, nodeSize);
    var nritems = BinaryPrimitives.ReadUInt32LittleEndian(node.AsSpan(96));
    var level = node[100];
    if (level > 0) {
      for (uint i = 0; i < nritems; i++) {
        var item = 101 + checked((int)i * 33);
        if (item + 33 > node.Length) throw new InvalidDataException("Btrfs chunk-tree node item is truncated.");
        var childLogical = BinaryPrimitives.ReadInt64LittleEndian(node.AsSpan(item + 17));
        WalkChunkTree(image, LogicalToPhysical(chunks, childLogical, image.Length), nodeSize, chunks, visited);
      }
      return;
    }

    for (uint i = 0; i < nritems; i++) {
      var item = 101 + checked((int)i * 25);
      if (item + 25 > node.Length) throw new InvalidDataException("Btrfs chunk-tree leaf item is truncated.");
      if (node[item + 8] != ChunkItem) continue;
      var logical = BinaryPrimitives.ReadInt64LittleEndian(node.AsSpan(item + 9));
      var dataOffset = BinaryPrimitives.ReadUInt32LittleEndian(node.AsSpan(item + 17));
      var dataSize = BinaryPrimitives.ReadUInt32LittleEndian(node.AsSpan(item + 21));
      var data = 101 + checked((int)dataOffset);
      if (dataSize < 80 || data < 0 || data + dataSize > node.Length)
        throw new InvalidDataException("Btrfs CHUNK_ITEM value is truncated.");
      var length = BinaryPrimitives.ReadInt64LittleEndian(node.AsSpan(data));
      var numStripes = BinaryPrimitives.ReadUInt16LittleEndian(node.AsSpan(data + 44));
      var subStripes = BinaryPrimitives.ReadUInt16LittleEndian(node.AsSpan(data + 46));
      if (numStripes != 1 || subStripes > 1)
        throw new NotSupportedException(
          $"Btrfs native mounting currently requires one physical stripe per chunk (found {numStripes}, sub_stripes={subStripes}).");
      var physicalOffset = BinaryPrimitives.ReadInt64LittleEndian(node.AsSpan(data + 56));
      AddChunk(chunks, logical, physicalOffset, length);
    }
  }

  private static long FindFsRoot(
      Stream image,
      long physical,
      uint nodeSize,
      List<Chunk> chunks,
      HashSet<long> visited) {
    if (!visited.Add(physical)) return -1;
    var node = ReadNode(image, physical, nodeSize);
    var nritems = BinaryPrimitives.ReadUInt32LittleEndian(node.AsSpan(96));
    var level = node[100];
    if (level > 0) {
      for (uint i = 0; i < nritems; i++) {
        var item = 101 + checked((int)i * 33);
        if (item + 33 > node.Length) throw new InvalidDataException("Btrfs root-tree node item is truncated.");
        var childLogical = BinaryPrimitives.ReadInt64LittleEndian(node.AsSpan(item + 17));
        var found = FindFsRoot(image, LogicalToPhysical(chunks, childLogical, image.Length), nodeSize, chunks, visited);
        if (found >= 0) return found;
      }
      return -1;
    }

    for (uint i = 0; i < nritems; i++) {
      var item = 101 + checked((int)i * 25);
      if (item + 25 > node.Length) throw new InvalidDataException("Btrfs root-tree leaf item is truncated.");
      var objectId = BinaryPrimitives.ReadInt64LittleEndian(node.AsSpan(item));
      if (objectId != FsTreeObjectId || node[item + 8] != RootItem) continue;
      var dataOffset = BinaryPrimitives.ReadUInt32LittleEndian(node.AsSpan(item + 17));
      var dataSize = BinaryPrimitives.ReadUInt32LittleEndian(node.AsSpan(item + 21));
      var data = 101 + checked((int)dataOffset);
      if (dataSize < 184 || data < 0 || data + 184 > node.Length)
        throw new InvalidDataException("Btrfs ROOT_ITEM is too short to contain its bytenr.");
      return BinaryPrimitives.ReadInt64LittleEndian(node.AsSpan(data + 176));
    }
    return -1;
  }

  private static void CollectLeaves(
      Stream image,
      long physical,
      uint nodeSize,
      List<Chunk> chunks,
      List<long> leaves,
      HashSet<long> visited) {
    if (!visited.Add(physical)) return;
    var node = ReadNode(image, physical, nodeSize);
    var nritems = BinaryPrimitives.ReadUInt32LittleEndian(node.AsSpan(96));
    var level = node[100];
    if (level == 0) {
      leaves.Add(physical);
      return;
    }
    for (uint i = 0; i < nritems; i++) {
      var item = 101 + checked((int)i * 33);
      if (item + 33 > node.Length) throw new InvalidDataException("Btrfs FS-tree node item is truncated.");
      var childLogical = BinaryPrimitives.ReadInt64LittleEndian(node.AsSpan(item + 17));
      CollectLeaves(image, LogicalToPhysical(chunks, childLogical, image.Length), nodeSize, chunks, leaves, visited);
    }
  }

  private static void CollectExtentRecords(
      Stream image,
      long leafPhysical,
      uint nodeSize,
      List<Chunk> chunks,
      Dictionary<long, List<Segment>> byInode) {
    var node = ReadNode(image, leafPhysical, nodeSize);
    if (node[100] != 0) throw new InvalidDataException("Btrfs extent collector was given a non-leaf node.");
    var nritems = BinaryPrimitives.ReadUInt32LittleEndian(node.AsSpan(96));

    for (uint i = 0; i < nritems; i++) {
      var item = 101 + checked((int)i * 25);
      if (item + 25 > node.Length) throw new InvalidDataException("Btrfs FS-tree leaf item is truncated.");
      if (node[item + 8] != ExtentData) continue;

      var inode = BinaryPrimitives.ReadInt64LittleEndian(node.AsSpan(item));
      var fileOffset = BinaryPrimitives.ReadInt64LittleEndian(node.AsSpan(item + 9));
      var dataOffset = BinaryPrimitives.ReadUInt32LittleEndian(node.AsSpan(item + 17));
      var dataSize = BinaryPrimitives.ReadUInt32LittleEndian(node.AsSpan(item + 21));
      var data = 101 + checked((int)dataOffset);
      if (dataSize < 21 || data < 0 || data + dataSize > node.Length)
        throw new InvalidDataException($"Btrfs inode {inode} has a truncated EXTENT_DATA record.");

      var compression = node[data + 16];
      var encryption = node[data + 17];
      var otherEncoding = BinaryPrimitives.ReadUInt16LittleEndian(node.AsSpan(data + 18));
      var type = node[data + 20];
      if (compression != 0 || encryption != 0 || otherEncoding != 0)
        throw new NotSupportedException(
          $"Btrfs inode {inode} EXTENT_DATA at {fileOffset:N0} uses compression={compression}, encryption={encryption}, other_encoding={otherEncoding}.");

      Segment segment;
      switch (type) {
        case 0: {
          var inlineLength = checked((long)dataSize - 21);
          if (inlineLength <= 0) continue;
          segment = new Segment(fileOffset, inlineLength, checked(leafPhysical + data + 21L));
          break;
        }
        case 1:
        case 2: {
          if (dataSize < 53)
            throw new InvalidDataException($"Btrfs inode {inode} regular/prealloc extent is truncated.");
          var diskBytenr = BinaryPrimitives.ReadInt64LittleEndian(node.AsSpan(data + 21));
          var diskNumBytes = BinaryPrimitives.ReadInt64LittleEndian(node.AsSpan(data + 29));
          var extentOffset = BinaryPrimitives.ReadInt64LittleEndian(node.AsSpan(data + 37));
          var numBytes = BinaryPrimitives.ReadInt64LittleEndian(node.AsSpan(data + 45));
          if (numBytes <= 0 || extentOffset < 0 || diskNumBytes < 0 || extentOffset > diskNumBytes || numBytes > diskNumBytes - extentOffset)
            throw new InvalidDataException($"Btrfs inode {inode} has invalid regular extent lengths.");
          if (type == 2 || diskBytenr == 0) {
            segment = new Segment(fileOffset, numBytes, null);
          } else {
            var basePhysical = LogicalToPhysical(chunks, diskBytenr, image.Length);
            segment = new Segment(fileOffset, numBytes, checked(basePhysical + extentOffset));
          }
          break;
        }
        default:
          throw new NotSupportedException($"Btrfs inode {inode} uses unsupported extent type {type}.");
      }

      if (!byInode.TryGetValue(inode, out var segments))
        byInode[inode] = segments = [];
      segments.Add(segment);
    }
  }

  private static void AddChunk(List<Chunk> chunks, long logical, long physical, long length) {
    if (logical < 0 || physical < 0 || length <= 0)
      throw new InvalidDataException("Btrfs chunk mapping contains a negative address or non-positive length.");
    foreach (var existing in chunks) {
      if (existing.Logical == logical && existing.Length == length) {
        if (existing.Physical != physical)
          throw new NotSupportedException("Btrfs duplicate logical chunk maps to multiple physical stripes.");
        return;
      }
    }
    chunks.Add(new Chunk(logical, physical, length));
  }

  private static long LogicalToPhysical(List<Chunk> chunks, long logical, long imageLength) {
    foreach (var chunk in chunks) {
      if (logical < chunk.Logical || logical >= checked(chunk.Logical + chunk.Length)) continue;
      var physical = checked(chunk.Physical + logical - chunk.Logical);
      if (physical < 0 || physical >= imageLength)
        throw new InvalidDataException($"Btrfs logical address {logical:N0} maps outside the image.");
      return physical;
    }
    throw new NotSupportedException($"Btrfs logical address {logical:N0} has no decoded single-device chunk mapping.");
  }

  private static byte[] ReadNode(Stream image, long physical, uint nodeSize) {
    if (physical < 0 || physical > image.Length - nodeSize)
      throw new InvalidDataException($"Btrfs tree node at {physical:N0} lies outside the image.");
    return ReadBytes(image, physical, checked((int)nodeSize));
  }

  private static byte[] ReadBytes(Stream image, long offset, int length) {
    if (offset < 0 || length < 0 || offset > image.Length - length)
      throw new InvalidDataException("Btrfs read range lies outside the image.");
    var buffer = new byte[length];
    var original = image.Position;
    try {
      image.Position = offset;
      image.ReadExactly(buffer);
      return buffer;
    } finally {
      image.Position = original;
    }
  }
}
