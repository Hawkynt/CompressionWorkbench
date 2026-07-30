#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Core.DiskImage;
using System.Text;

namespace FileSystem.Bfs;

/// <summary>
/// Reads files from a BFS filesystem image. Parses the superblock, walks each
/// directory's B+ tree leaf chain (following right_link across sibling leaves),
/// and extracts file data from direct block_run extents. Supports directories
/// whose entries span multiple chained leaves; does not traverse interior/index
/// nodes or indirect/double-indirect extents.
/// </summary>
internal sealed class BfsReader {

  private const uint InodeMagic = 0x3BDE0AD9;
  private const int InodeDataStreamOffset = 72;
  private const int NumDirectBlocks = 12;
  private const uint S_IFDIR = 0x4000;
  private const uint S_IFMT = 0xF000;
  private const int MaxDirectoryDepth = 64; // guard against cyclic/corrupt trees

  /// <summary>
  /// Random-access view over the volume. Copying it into a byte[] capped the
  /// reader at the array limit, which BFS's 64-bit block runs do not.
  /// </summary>
  private readonly ImageAccessor _image;
  private readonly int _blockSize;
  private readonly int _superblockOffset;

  /// <summary>
  /// Blocks per allocation group, from the superblock. A block_run carries its
  /// group in a u32 and its start as a u16 within that group, so an absolute
  /// block number is <c>ag * blocks_per_ag + start</c> — reading the start alone
  /// worked only while the volume fitted in one group.
  /// </summary>
  private readonly long _blocksPerAg;

  public IReadOnlyList<BfsFileEntry> Entries { get; }

  public BfsReader(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    if (stream.CanSeek) stream.Position = 0;
    _image = new ImageAccessor(stream, leaveOpen: true);

    var sb = BfsSuperblock.TryParse(_image.Read(0, (int)Math.Min(_image.Length, 64 * 1024)));
    if (!sb.Valid)
      throw new InvalidDataException("BFS: no valid superblock found.");

    _superblockOffset = sb.SuperblockOffset;
    _blockSize = (int)sb.BlockSize;
    if (_blockSize < 512 || _blockSize > 65536 || (_blockSize & (_blockSize - 1)) != 0)
      throw new InvalidDataException($"BFS: invalid block size {_blockSize}.");

    var blocksPerAg = _image.ReadUInt32(_superblockOffset + 72);
    _blocksPerAg = blocksPerAg > 0 ? blocksPerAg : long.MaxValue;

    // Read root dir inode to find the B+ tree leaf
    var rootDirRun = ReadBlockRun(_image.Read(_superblockOffset + 116, 16), 0);
    var rootDirInodeOffset = this.BlockOf(rootDirRun) * _blockSize;

    // Verify inode magic
    if (rootDirInodeOffset + InodeDataStreamOffset + NumDirectBlocks * 8 > _image.Length)
      throw new InvalidDataException("BFS: root dir inode extends past image.");

    var inodeMagic = _image.ReadUInt32(rootDirInodeOffset);
    if (inodeMagic != InodeMagic)
      throw new InvalidDataException($"BFS: root dir inode magic mismatch: 0x{inodeMagic:X8}.");

    // Walk the directory tree starting at the root directory, descending into
    // subdirectory inodes and surfacing each file (and directory) at its full
    // path. Names are joined with '/'.
    var entries = new List<BfsFileEntry>();
    var visited = new HashSet<long>();
    WalkDirectory(this.BlockOf(rootDirRun), prefix: string.Empty, depth: 0, entries, visited);
    Entries = entries;
  }

  private void WalkDirectory(long dirInodeBlock, string prefix, int depth, List<BfsFileEntry> entries, HashSet<long> visited) {
    if (depth > MaxDirectoryDepth) return;
    if (!visited.Add(dirInodeBlock)) return; // already walked — avoid cycles

    var dirInodeOffset = dirInodeBlock * _blockSize;
    if (dirInodeOffset < 0 || dirInodeOffset + InodeDataStreamOffset + 8 > _image.Length) return;
    if (_image.ReadUInt32(dirInodeOffset) != InodeMagic) return;

    var btreeRun = ReadBlockRun(_image.Read(dirInodeOffset + InodeDataStreamOffset, 16), 0);

    foreach (var (name, inodeBlock) in ReadAllBtreeEntries(this.BlockOf(btreeRun))) {
      var fullName = prefix.Length == 0 ? name : prefix + "/" + name;
      var inodeOffset = inodeBlock * _blockSize;
      var (isDir, size) = ReadInodeKindAndSize(inodeOffset);

      entries.Add(new BfsFileEntry(fullName, size, inodeBlock, isDir));

      if (isDir)
        WalkDirectory(inodeBlock, fullName, depth + 1, entries, visited);
    }
  }

  /// <summary>Reads an inode's mode/size; returns whether it is a directory and its logical size.</summary>
  private (bool IsDir, long Size) ReadInodeKindAndSize(long inodeOffset) {
    if (inodeOffset < 0 || inodeOffset + InodeDataStreamOffset + 136 + 8 > _image.Length) return (false, 0);
    if (_image.ReadUInt32(inodeOffset) != InodeMagic) return (false, 0);

    var mode = _image.ReadUInt32(inodeOffset + 20);
    var isDir = (mode & S_IFMT) == S_IFDIR;
    var size = isDir
      ? 0L
      : _image.ReadInt64(inodeOffset + InodeDataStreamOffset + 136);
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

    var magic = _image.ReadUInt32(inodeOffset);
    if (magic != InodeMagic)
      throw new InvalidDataException($"BFS: file inode magic mismatch for '{entry.Name}'.");

    // Read file size from data_stream.size
    var fileSize = _image.ReadInt64(inodeOffset + InodeDataStreamOffset + 136);
    if (fileSize < 0 || fileSize > _image.Length)
      throw new InvalidDataException($"BFS: invalid file size {fileSize} for '{entry.Name}'.");

    if (fileSize > Array.MaxLength)
      throw new IOException(
        $"BFS: '{entry.Name}' is {fileSize:N0} bytes, past the array limit; use ExtractTo.");

    var result = new byte[fileSize];
    using var target = new MemoryStream(result, writable: true);
    this.WriteRuns(entry, inodeOffset, fileSize, target);
    return result;
  }

  /// <summary>
  /// Writes <paramref name="entry" />'s contents into <paramref name="destination" />,
  /// one block run at a time. Returns the number of bytes written.
  /// </summary>
  public long ExtractTo(BfsFileEntry entry, Stream destination) {
    ArgumentNullException.ThrowIfNull(entry);
    ArgumentNullException.ThrowIfNull(destination);
    if (entry.IsDirectory) return 0;

    var inodeOffset = entry.InodeBlock * _blockSize;
    if (inodeOffset + InodeDataStreamOffset + NumDirectBlocks * 8 + 8 > _image.Length)
      throw new InvalidDataException($"BFS: file inode for '{entry.Name}' extends past image.");
    if (_image.ReadUInt32(inodeOffset) != InodeMagic)
      throw new InvalidDataException($"BFS: file inode magic mismatch for '{entry.Name}'.");

    var fileSize = _image.ReadInt64(inodeOffset + InodeDataStreamOffset + 136);
    if (fileSize < 0)
      throw new InvalidDataException($"BFS: invalid file size {fileSize} for '{entry.Name}'.");
    return this.WriteRuns(entry, inodeOffset, fileSize, destination);
  }

  /// <summary>
  /// Copies the file's data runs out in order: the twelve in the inode's direct[],
  /// then the block_run array the indirect run points at.
  /// </summary>
  private long WriteRuns(BfsFileEntry entry, long inodeOffset, long fileSize, Stream destination) {
    var written = 0L;

    long Copy((uint Ag, int Start, int Length) run) {
      if (run.Length == 0 || written >= fileSize) return 0;
      var srcOffset = this.BlockOf(run) * _blockSize;
      var toCopy = Math.Min((long)run.Length * _blockSize, fileSize - written);
      if (srcOffset < 0 || srcOffset + toCopy > _image.Length)
        throw new InvalidDataException($"BFS: data run for '{entry.Name}' extends past image.");
      _image.CopyTo(srcOffset, destination, toCopy);
      written += toCopy;
      return toCopy;
    }

    for (var i = 0; i < NumDirectBlocks && written < fileSize; i++) {
      var run = ReadBlockRun(_image.Read(inodeOffset + InodeDataStreamOffset + i * 8, 16), 0);
      if (run.Length == 0) break;
      Copy(run);
    }

    if (written < fileSize) {
      // The indirect run points at blocks holding a plain array of block_runs.
      var indirect = ReadBlockRun(
        _image.Read(inodeOffset + InodeDataStreamOffset + NumDirectBlocks * 8 + 8, 16), 0);
      if (indirect.Length > 0) {
        var runsPerBlock = _blockSize / 8;
        var first = this.BlockOf(indirect);
        for (var b = 0; b < indirect.Length && written < fileSize; ++b) {
          var block = _image.Read((first + b) * _blockSize, _blockSize);
          for (var j = 0; j < runsPerBlock && written < fileSize; ++j) {
            var run = ReadBlockRun(block, j * 8);
            if (run.Length == 0) break;
            Copy(run);
          }
        }
      }
    }

    return written;
  }

  /// <summary>Absolute block number a run addresses: its group index times the group size, plus its start.</summary>
  private long BlockOf((uint Ag, int Start, int Length) run)
    => run.Ag * this._blocksPerAg + run.Start;

  /// <summary>
  /// Reads every directory entry by walking the chain of B+ tree leaf nodes
  /// starting at <paramref name="firstLeafBlock"/> and following each leaf's
  /// right_link. A directory whose children overflow a single 1024-byte leaf is
  /// stored across several sibling leaves linked this way.
  /// </summary>
  private List<(string Name, long InodeBlock)> ReadAllBtreeEntries(long firstLeafBlock) {
    var all = new List<(string Name, long InodeBlock)>();
    var visited = new HashSet<long>();
    var leafBlock = (long)firstLeafBlock;

    while (leafBlock >= 0 && visited.Add(leafBlock)) {
      var leafOffset = (int)(leafBlock * _blockSize);
      if (leafOffset < 0 || leafOffset + 28 > _image.Length) break;

      all.AddRange(ParseBtreeLeafEntries(leafOffset));

      // right_link (i64) at leaf offset +8 points at the next sibling leaf, or -1.
      leafBlock = _image.ReadInt64(leafOffset + 8);
    }

    return all;
  }

  private List<(string Name, long InodeBlock)> ParseBtreeLeafEntries(long leafOffset) {
    var entries = new List<(string Name, long InodeBlock)>();

    if (leafOffset < 0 || leafOffset + 28 > _image.Length)
      return entries;

    // B+ tree leaf header:
    //  0: left_link (i64)
    //  8: right_link (i64)
    // 16: overflow_link (i64)
    // 24: all_key_count (u16)
    // 26: all_key_length (u16)

    var keyCount = _image.ReadUInt16(leafOffset + 24);
    var totalKeyLength = _image.ReadUInt16(leafOffset + 26);

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
      cumulativeLengths[i] = _image.ReadUInt16(keyLenTableOffset + i * 2);

    // Read values from end of block
    var valuesStart = leafOffset + _blockSize - keyCount * 8;
    if (valuesStart < keyDataOffset + totalKeyLength)
      return entries; // overlap — malformed

    var prevLen = 0;
    for (var i = 0; i < keyCount; i++) {
      var nameLen = cumulativeLengths[i] - prevLen;
      var name = Encoding.UTF8.GetString(_image.Read(keyDataOffset + prevLen, nameLen));
      prevLen = cumulativeLengths[i];

      var inodeBlockOffT = _image.ReadInt64(valuesStart + i * 8);
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
internal sealed record BfsFileEntry(string Name, long Size, long InodeBlock, bool IsDirectory = false);
