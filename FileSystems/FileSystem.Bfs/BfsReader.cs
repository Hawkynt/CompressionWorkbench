#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;

namespace FileSystem.Bfs;

/// <summary>
/// Reads files from a BFS filesystem image. Parses the superblock, walks
/// the root directory B+ tree leaf, and extracts file data from direct
/// block_run extents. Only supports single-leaf B+ trees (no interior
/// node traversal) and direct extents (no indirect/double-indirect).
/// </summary>
internal sealed class BfsReader {

  private const uint InodeMagic = 0x3BDE0AD9;
  private const int InodeDataStreamOffset = 72;
  private const int NumDirectBlocks = 12;
  private const uint S_IFDIR = 0x4000;
  private const uint S_IFMT = 0xF000;
  private const int MaxDirectoryDepth = 64; // guard against cyclic/corrupt trees

  private readonly byte[] _image;
  private readonly int _blockSize;
  private readonly int _superblockOffset;

  public IReadOnlyList<BfsFileEntry> Entries { get; }

  public BfsReader(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    _image = ms.ToArray();

    var sb = BfsSuperblock.TryParse(_image);
    if (!sb.Valid)
      throw new InvalidDataException("BFS: no valid superblock found.");

    _superblockOffset = sb.SuperblockOffset;
    _blockSize = (int)sb.BlockSize;
    if (_blockSize < 512 || _blockSize > 65536 || (_blockSize & (_blockSize - 1)) != 0)
      throw new InvalidDataException($"BFS: invalid block size {_blockSize}.");

    // Read root dir inode to find the B+ tree leaf
    var rootDirRun = ReadBlockRun(_image, _superblockOffset + 116);
    var rootDirInodeOffset = rootDirRun.Start * _blockSize;

    // Verify inode magic
    if (rootDirInodeOffset + InodeDataStreamOffset + NumDirectBlocks * 8 > _image.Length)
      throw new InvalidDataException("BFS: root dir inode extends past image.");

    var inodeMagic = BinaryPrimitives.ReadUInt32LittleEndian(_image.AsSpan(rootDirInodeOffset));
    if (inodeMagic != InodeMagic)
      throw new InvalidDataException($"BFS: root dir inode magic mismatch: 0x{inodeMagic:X8}.");

    // Walk the directory tree starting at the root directory, descending into
    // subdirectory inodes and surfacing each file (and directory) at its full
    // path. Names are joined with '/'.
    var entries = new List<BfsFileEntry>();
    var visited = new HashSet<int>();
    WalkDirectory(rootDirRun.Start, prefix: string.Empty, depth: 0, entries, visited);
    Entries = entries;
  }

  private void WalkDirectory(int dirInodeBlock, string prefix, int depth, List<BfsFileEntry> entries, HashSet<int> visited) {
    if (depth > MaxDirectoryDepth) return;
    if (!visited.Add(dirInodeBlock)) return; // already walked — avoid cycles

    var dirInodeOffset = dirInodeBlock * _blockSize;
    if (dirInodeOffset < 0 || dirInodeOffset + InodeDataStreamOffset + 8 > _image.Length) return;
    if (BinaryPrimitives.ReadUInt32LittleEndian(_image.AsSpan(dirInodeOffset)) != InodeMagic) return;

    var btreeRun = ReadBlockRun(_image, dirInodeOffset + InodeDataStreamOffset);
    var btreeOffset = btreeRun.Start * _blockSize;

    foreach (var (name, inodeBlock) in ParseBtreeLeafEntries(btreeOffset)) {
      var fullName = prefix.Length == 0 ? name : prefix + "/" + name;
      var inodeOffset = inodeBlock * _blockSize;
      var (isDir, size) = ReadInodeKindAndSize(inodeOffset);

      entries.Add(new BfsFileEntry(fullName, size, inodeBlock, isDir));

      if (isDir)
        WalkDirectory(inodeBlock, fullName, depth + 1, entries, visited);
    }
  }

  /// <summary>Reads an inode's mode/size; returns whether it is a directory and its logical size.</summary>
  private (bool IsDir, long Size) ReadInodeKindAndSize(int inodeOffset) {
    if (inodeOffset < 0 || inodeOffset + InodeDataStreamOffset + 136 + 8 > _image.Length) return (false, 0);
    if (BinaryPrimitives.ReadUInt32LittleEndian(_image.AsSpan(inodeOffset)) != InodeMagic) return (false, 0);

    var mode = BinaryPrimitives.ReadUInt32LittleEndian(_image.AsSpan(inodeOffset + 20));
    var isDir = (mode & S_IFMT) == S_IFDIR;
    var size = isDir
      ? 0L
      : BinaryPrimitives.ReadInt64LittleEndian(_image.AsSpan(inodeOffset + InodeDataStreamOffset + 136));
    if (size < 0 || size > _image.Length) size = 0;
    return (isDir, size);
  }

  /// <summary>Extracts file data for the given entry. Directories yield no bytes.</summary>
  public byte[] Extract(BfsFileEntry entry) {
    if (entry.IsDirectory || entry.Size == 0) return [];

    // Read the file inode
    var inodeOffset = entry.InodeBlock * _blockSize;
    if (inodeOffset + InodeDataStreamOffset + NumDirectBlocks * 8 + 8 > _image.Length)
      throw new InvalidDataException($"BFS: file inode for '{entry.Name}' extends past image.");

    var magic = BinaryPrimitives.ReadUInt32LittleEndian(_image.AsSpan(inodeOffset));
    if (magic != InodeMagic)
      throw new InvalidDataException($"BFS: file inode magic mismatch for '{entry.Name}'.");

    // Read file size from data_stream.size
    var fileSize = BinaryPrimitives.ReadInt64LittleEndian(
      _image.AsSpan(inodeOffset + InodeDataStreamOffset + 136));
    if (fileSize < 0 || fileSize > _image.Length)
      throw new InvalidDataException($"BFS: invalid file size {fileSize} for '{entry.Name}'.");

    // Read direct block_runs and concatenate data
    var result = new byte[fileSize];
    var destOffset = 0L;
    for (var i = 0; i < NumDirectBlocks && destOffset < fileSize; i++) {
      var run = ReadBlockRun(_image, inodeOffset + InodeDataStreamOffset + i * 8);
      if (run.Length == 0) break;

      var srcOffset = run.Start * _blockSize;
      var runBytes = (long)run.Length * _blockSize;
      var toCopy = Math.Min(runBytes, fileSize - destOffset);

      if (srcOffset + toCopy > _image.Length)
        throw new InvalidDataException($"BFS: data run for '{entry.Name}' extends past image.");

      _image.AsSpan((int)srcOffset, (int)toCopy).CopyTo(result.AsSpan((int)destOffset));
      destOffset += toCopy;
    }

    return result;
  }

  private List<(string Name, int InodeBlock)> ParseBtreeLeafEntries(int leafOffset) {
    var entries = new List<(string Name, int InodeBlock)>();

    if (leafOffset < 0 || leafOffset + 28 > _image.Length)
      return entries;

    // B+ tree leaf header:
    //  0: left_link (i64)
    //  8: right_link (i64)
    // 16: overflow_link (i64)
    // 24: all_key_count (u16)
    // 26: all_key_length (u16)

    var keyCount = BinaryPrimitives.ReadUInt16LittleEndian(_image.AsSpan(leafOffset + 24));
    var totalKeyLength = BinaryPrimitives.ReadUInt16LittleEndian(_image.AsSpan(leafOffset + 26));

    if (keyCount == 0) return entries;

    var headerSize = 28;
    var keyLenTableOffset = leafOffset + headerSize;
    var keyDataOffset = keyLenTableOffset + keyCount * 2;

    // Validate bounds
    if (keyDataOffset + totalKeyLength > _image.Length)
      return entries;

    // Read cumulative key lengths
    var cumulativeLengths = new ushort[keyCount];
    for (var i = 0; i < keyCount; i++)
      cumulativeLengths[i] = BinaryPrimitives.ReadUInt16LittleEndian(_image.AsSpan(keyLenTableOffset + i * 2));

    // Read values from end of block
    var valuesStart = leafOffset + _blockSize - keyCount * 8;
    if (valuesStart < keyDataOffset + totalKeyLength)
      return entries; // overlap — malformed

    var prevLen = 0;
    for (var i = 0; i < keyCount; i++) {
      var nameLen = cumulativeLengths[i] - prevLen;
      var name = Encoding.UTF8.GetString(_image, keyDataOffset + prevLen, nameLen);
      prevLen = cumulativeLengths[i];

      var inodeBlockOffT = BinaryPrimitives.ReadInt64LittleEndian(_image.AsSpan(valuesStart + i * 8));
      // For single-AG with AG=0: off_t = block number
      var inodeBlock = (int)inodeBlockOffT;

      entries.Add((name, inodeBlock));
    }

    return entries;
  }

  private static (uint Ag, int Start, int Length) ReadBlockRun(byte[] image, int offset) {
    if (offset + 8 > image.Length)
      return (0, 0, 0);
    var ag = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(offset));
    var start = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(offset + 4));
    var length = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(offset + 6));
    return (ag, start, length);
  }
}

/// <summary>Represents a file or directory entry in a BFS image, named by its full path.</summary>
internal sealed record BfsFileEntry(string Name, long Size, int InodeBlock, bool IsDirectory = false);
