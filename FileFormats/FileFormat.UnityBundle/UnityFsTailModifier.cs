using System.Buffers.Binary;
using System.Text;
using Compression.Core.Dictionary.Lz4;
using Compression.Core.Dictionary.Lzma;
using Compression.Registry;

namespace FileFormat.UnityBundle;

/// <summary>
/// Changed-byte editor for modern UnityFS bundles that place BlocksInfo at EOF.
/// Existing compressed storage blocks are opaque and stay byte-identical. Pure
/// additions append independent Stored blocks before a regenerated BlocksInfo
/// trailer. Removal is direct only when all removed non-empty nodes occupy whole
/// trailing storage blocks; zero-length nodes are metadata-only.
/// </summary>
internal static class UnityFsTailModifier {
  private const uint BlocksInfoAtEnd = 0x80;
  private const uint DataAligned16 = 0x200;
  private const uint CompressionMask = 0x3F;

  private sealed record Header(
    uint FormatVersion,
    long TotalSizeOffset,
    long CompressedInfoSizeOffset,
    long UncompressedInfoSizeOffset,
    long FlagsOffset,
    long DataOffset,
    long BlocksInfoOffset,
    uint Flags);

  private readonly record struct StorageBlock(uint UncompressedSize, uint CompressedSize, ushort Flags);
  private readonly record struct Node(long Offset, long Size, uint Flags, string Path);
  private sealed record State(Header Header, List<StorageBlock> Blocks, List<Node> Nodes);
  private sealed record PendingNode(string Path, byte[] Data);

  /// <summary>
  /// Appends new nodes without reading or rewriting existing storage blocks.
  /// Same-name replacement deliberately falls back because stale node bytes would
  /// otherwise remain recoverable inside an existing block.
  /// </summary>
  public static void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    ValidateStream(archive);
    ArgumentNullException.ThrowIfNull(inputs);

    var additions = new Dictionary<string, PendingNode>(StringComparer.OrdinalIgnoreCase);
    foreach (var input in inputs) {
      if (input.IsDirectory || string.IsNullOrEmpty(input.ArchiveName))
        continue;
      var path = NormalizePath(input.ArchiveName);
      additions[path] = new PendingNode(path, input.ReadContent());
    }
    if (additions.Count == 0)
      return;

    var state = ReadState(archive);
    var existing = new HashSet<string>(state.Nodes.Select(node => node.Path), StringComparer.OrdinalIgnoreCase);
    foreach (var addition in additions.Values)
      if (existing.Contains(addition.Path))
        throw new NotSupportedException(
          $"UnityFS tail add cannot replace existing node '{addition.Path}' without wiping its current storage block.");

    var blocks = new List<StorageBlock>(state.Blocks);
    var nodes = new List<Node>(state.Nodes);
    var logicalOffset = blocks.Sum(block => (long)block.UncompressedSize);
    var appendedDataLength = 0L;

    foreach (var addition in additions.Values.OrderBy(item => item.Path, StringComparer.Ordinal)) {
      if (addition.Data.Length > 0) {
        var size = checked((uint)addition.Data.Length);
        blocks.Add(new StorageBlock(size, size, 0));
      }
      nodes.Add(new Node(logicalOffset, addition.Data.LongLength, NodeFlags(addition.Path), addition.Path));
      logicalOffset = checked(logicalOffset + addition.Data.LongLength);
      appendedDataLength = checked(appendedDataLength + addition.Data.LongLength);
    }

    var blocksInfo = BuildBlocksInfo(blocks, nodes);
    var newFlags = state.Header.Flags & ~CompressionMask; // write BlocksInfo Stored
    var newTotalSize = checked(state.Header.BlocksInfoOffset + appendedDataLength + blocksInfo.LongLength);

    // Everything format-dependent is complete before the first archive write.
    archive.Position = state.Header.BlocksInfoOffset;
    foreach (var addition in additions.Values.OrderBy(item => item.Path, StringComparer.Ordinal))
      if (addition.Data.Length > 0)
        archive.Write(addition.Data);
    archive.Write(blocksInfo);
    PatchHeader(archive, state.Header, newTotalSize, checked((uint)blocksInfo.Length), checked((uint)blocksInfo.Length), newFlags);
    archive.SetLength(newTotalSize);
    archive.Flush();
  }

  /// <summary>
  /// Removes nodes without touching survivors when the non-empty removal set maps
  /// to a suffix of whole physical storage blocks. Otherwise throws before writing
  /// so the descriptor can use its verified rebuild path.
  /// </summary>
  public static void Remove(Stream archive, IReadOnlyCollection<string> entryNames) {
    ValidateStream(archive);
    ArgumentNullException.ThrowIfNull(entryNames);
    if (entryNames.Count == 0)
      return;

    var requested = new HashSet<string>(entryNames.Where(name => !string.IsNullOrEmpty(name)), StringComparer.OrdinalIgnoreCase);
    if (requested.Count == 0)
      return;

    var state = ReadState(archive);
    var removeNode = new bool[state.Nodes.Count];
    var matchedAny = false;
    for (var i = 0; i < state.Nodes.Count; ++i) {
      if (MatchesAny(state.Nodes[i].Path, requested)) {
        removeNode[i] = true;
        matchedAny = true;
      }
    }
    if (!matchedAny)
      return;

    var blockLogicalStarts = new long[state.Blocks.Count + 1];
    var blockPhysicalStarts = new long[state.Blocks.Count + 1];
    blockPhysicalStarts[0] = state.Header.DataOffset;
    for (var i = 0; i < state.Blocks.Count; ++i) {
      blockLogicalStarts[i + 1] = checked(blockLogicalStarts[i] + state.Blocks[i].UncompressedSize);
      blockPhysicalStarts[i + 1] = checked(blockPhysicalStarts[i] + state.Blocks[i].CompressedSize);
    }

    var firstRemovedBlock = state.Blocks.Count;
    for (var i = 0; i < state.Nodes.Count; ++i) {
      if (!removeNode[i] || state.Nodes[i].Size == 0)
        continue;
      var block = FindBlock(state.Nodes[i].Offset, state.Nodes[i].Size, blockLogicalStarts);
      firstRemovedBlock = Math.Min(firstRemovedBlock, block);
    }

    if (firstRemovedBlock < state.Blocks.Count) {
      var logicalBoundary = blockLogicalStarts[firstRemovedBlock];
      for (var i = 0; i < state.Nodes.Count; ++i) {
        if (removeNode[i] || state.Nodes[i].Size == 0)
          continue;
        var end = checked(state.Nodes[i].Offset + state.Nodes[i].Size);
        if (end > logicalBoundary)
          throw new NotSupportedException(
            $"UnityFS node '{state.Nodes[i].Path}' shares or follows a storage block needed by the removal; tail-only deletion is impossible.");
      }
    }

    var keptBlocks = firstRemovedBlock < state.Blocks.Count
      ? state.Blocks.Take(firstRemovedBlock).ToList()
      : new List<StorageBlock>(state.Blocks);
    var newLogicalLength = keptBlocks.Sum(block => (long)block.UncompressedSize);
    var keptNodes = new List<Node>();
    for (var i = 0; i < state.Nodes.Count; ++i) {
      if (removeNode[i])
        continue;
      var node = state.Nodes[i];
      if (node.Size == 0 && node.Offset > newLogicalLength)
        node = node with { Offset = newLogicalLength };
      keptNodes.Add(node);
    }

    var newDataEnd = firstRemovedBlock < state.Blocks.Count
      ? blockPhysicalStarts[firstRemovedBlock]
      : state.Header.BlocksInfoOffset;
    var blocksInfo = BuildBlocksInfo(keptBlocks, keptNodes);
    var newFlags = state.Header.Flags & ~CompressionMask;
    var newTotalSize = checked(newDataEnd + blocksInfo.LongLength);

    archive.Position = newDataEnd;
    archive.Write(blocksInfo);
    PatchHeader(archive, state.Header, newTotalSize, checked((uint)blocksInfo.Length), checked((uint)blocksInfo.Length), newFlags);
    archive.SetLength(newTotalSize);
    archive.Flush();
  }

  private static State ReadState(Stream archive) {
    archive.Position = 0;
    var signature = ReadCString(archive);
    if (!string.Equals(signature, "UnityFS", StringComparison.Ordinal))
      throw new NotSupportedException("Tail editing is only defined for UnityFS bundles.");

    var formatVersion = ReadUInt32BE(archive);
    if (formatVersion is < 6 or > 8)
      throw new NotSupportedException($"UnityFS tail editing supports format versions 6-8, got {formatVersion}.");
    _ = ReadCString(archive); // UnityVersion
    _ = ReadCString(archive); // UnityRevision

    var totalSizeOffset = archive.Position;
    var totalSize = ReadInt64BE(archive);
    var compressedInfoSizeOffset = archive.Position;
    var compressedInfoSize = ReadUInt32BE(archive);
    var uncompressedInfoSizeOffset = archive.Position;
    var uncompressedInfoSize = ReadUInt32BE(archive);
    var flagsOffset = archive.Position;
    var flags = ReadUInt32BE(archive);
    var headerEnd = archive.Position;

    if (totalSize != archive.Length)
      throw new NotSupportedException(
        $"UnityFS tail editing requires TotalSize ({totalSize}) to equal the physical stream length ({archive.Length}).");
    if ((flags & BlocksInfoAtEnd) == 0)
      throw new NotSupportedException("UnityFS changed-byte editing requires BlocksInfoAtEnd (flag 0x80).");
    if (compressedInfoSize > Array.MaxLength || uncompressedInfoSize > Array.MaxLength)
      throw new NotSupportedException("UnityFS BlocksInfo exceeds the managed array limit.");

    var dataOffset = formatVersion >= 7 ? Align16(headerEnd) : headerEnd;
    if ((flags & DataAligned16) != 0)
      dataOffset = Align16(dataOffset);
    var blocksInfoOffset = checked(archive.Length - compressedInfoSize);
    if (blocksInfoOffset < dataOffset)
      throw new InvalidDataException("UnityFS BlocksInfo overlaps the data/header region.");

    var compressedInfo = new byte[(int)compressedInfoSize];
    archive.Position = blocksInfoOffset;
    archive.ReadExactly(compressedInfo);
    var blocksInfo = DecodeBlocksInfo(compressedInfo, checked((int)uncompressedInfoSize), checked((int)(flags & CompressionMask)));
    var (blocks, nodes) = ParseBlocksInfo(blocksInfo);

    var compressedDataSize = blocks.Sum(block => (long)block.CompressedSize);
    if (checked(dataOffset + compressedDataSize) != blocksInfoOffset)
      throw new NotSupportedException(
        "UnityFS tail editing requires storage blocks to be contiguous immediately before the trailing BlocksInfo record.");

    var logicalLength = blocks.Sum(block => (long)block.UncompressedSize);
    foreach (var node in nodes) {
      if (node.Offset < 0 || node.Size < 0 || node.Offset > logicalLength || node.Size > logicalLength - node.Offset)
        throw new InvalidDataException($"UnityFS node '{node.Path}' lies outside the logical data stream.");
    }

    return new State(
      new Header(formatVersion, totalSizeOffset, compressedInfoSizeOffset, uncompressedInfoSizeOffset,
        flagsOffset, dataOffset, blocksInfoOffset, flags),
      blocks,
      nodes);
  }

  private static (List<StorageBlock> Blocks, List<Node> Nodes) ParseBlocksInfo(byte[] data) {
    if (data.Length < 20)
      throw new InvalidDataException("UnityFS BlocksInfo is truncated.");
    var pos = 16;
    var blockCount = ReadInt32BE(data, ref pos);
    if (blockCount < 0 || (long)blockCount * 10 > data.Length - pos)
      throw new InvalidDataException("UnityFS BlocksInfo has an invalid block count.");

    var blocks = new List<StorageBlock>(blockCount);
    for (var i = 0; i < blockCount; ++i)
      blocks.Add(new StorageBlock(ReadUInt32BE(data, ref pos), ReadUInt32BE(data, ref pos), ReadUInt16BE(data, ref pos)));

    var nodeCount = ReadInt32BE(data, ref pos);
    if (nodeCount < 0 || (long)nodeCount * 21 > data.Length - pos)
      throw new InvalidDataException("UnityFS BlocksInfo has an invalid node count.");

    var nodes = new List<Node>(nodeCount);
    for (var i = 0; i < nodeCount; ++i)
      nodes.Add(new Node(ReadInt64BE(data, ref pos), ReadInt64BE(data, ref pos), ReadUInt32BE(data, ref pos), ReadCString(data, ref pos)));
    return (blocks, nodes);
  }

  private static byte[] BuildBlocksInfo(IReadOnlyList<StorageBlock> blocks, IReadOnlyList<Node> nodes) {
    using var output = new MemoryStream();
    output.Write(new byte[16]); // hash/reserved field: writer also emits zero
    WriteInt32BE(output, blocks.Count);
    foreach (var block in blocks) {
      WriteUInt32BE(output, block.UncompressedSize);
      WriteUInt32BE(output, block.CompressedSize);
      WriteUInt16BE(output, block.Flags);
    }
    WriteInt32BE(output, nodes.Count);
    foreach (var node in nodes) {
      WriteInt64BE(output, node.Offset);
      WriteInt64BE(output, node.Size);
      WriteUInt32BE(output, node.Flags);
      WriteCString(output, node.Path);
    }
    return output.ToArray();
  }

  private static byte[] DecodeBlocksInfo(byte[] compressed, int uncompressedSize, int compressionType) {
    return compressionType switch {
      0 => compressed.Length == uncompressedSize
        ? compressed
        : throw new InvalidDataException("Stored UnityFS BlocksInfo size mismatch."),
      1 => DecodeLzma(compressed, uncompressedSize),
      2 or 3 => Lz4BlockDecompressor.Decompress(compressed, uncompressedSize),
      _ => throw new NotSupportedException($"UnityFS BlocksInfo compression type {compressionType} is unsupported."),
    };
  }

  private static byte[] DecodeLzma(byte[] compressed, int uncompressedSize) {
    if (compressed.Length < 5)
      throw new InvalidDataException("UnityFS LZMA BlocksInfo is missing properties.");
    var properties = compressed.AsSpan(0, 5).ToArray();
    using var input = new MemoryStream(compressed, 5, compressed.Length - 5, writable: false);
    var decoder = new LzmaDecoder(input, properties, uncompressedSize);
    var result = decoder.Decode();
    if (result.Length != uncompressedSize)
      throw new InvalidDataException("UnityFS LZMA BlocksInfo decoded to the wrong size.");
    return result;
  }

  private static int FindBlock(long nodeOffset, long nodeSize, long[] logicalStarts) {
    if (nodeSize <= 0)
      throw new ArgumentOutOfRangeException(nameof(nodeSize));
    for (var i = 0; i + 1 < logicalStarts.Length; ++i)
      if (nodeOffset >= logicalStarts[i] && nodeOffset < logicalStarts[i + 1])
        return i;
    throw new InvalidDataException("UnityFS node does not start inside any declared storage block.");
  }

  private static bool MatchesAny(string path, HashSet<string> requested) {
    if (requested.Contains(path))
      return true;
    var slash = path.LastIndexOf('/');
    var leaf = slash >= 0 ? path[(slash + 1)..] : path;
    return requested.Contains(leaf);
  }

  private static string NormalizePath(string path) {
    if (string.IsNullOrWhiteSpace(path))
      throw new ArgumentException("UnityFS node path must not be empty.", nameof(path));
    var normalized = path.Replace('\\', '/').TrimStart('/');
    if (normalized.Length == 0 || normalized.EndsWith('/'))
      throw new ArgumentException("UnityFS node path must name a file.", nameof(path));
    foreach (var part in normalized.Split('/')) {
      if (part.Length == 0 || part is "." or ".." || part.IndexOf('\0') >= 0)
        throw new ArgumentException("Unsafe UnityFS node path.", nameof(path));
    }
    return normalized;
  }

  private static uint NodeFlags(string path)
    => path.EndsWith(".assets", StringComparison.OrdinalIgnoreCase) ? 0x04u : 0u;

  private static void PatchHeader(
      Stream archive,
      Header header,
      long totalSize,
      uint compressedInfoSize,
      uint uncompressedInfoSize,
      uint flags) {
    Span<byte> buffer8 = stackalloc byte[8];
    BinaryPrimitives.WriteInt64BigEndian(buffer8, totalSize);
    archive.Position = header.TotalSizeOffset;
    archive.Write(buffer8);

    Span<byte> buffer4 = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32BigEndian(buffer4, compressedInfoSize);
    archive.Position = header.CompressedInfoSizeOffset;
    archive.Write(buffer4);
    BinaryPrimitives.WriteUInt32BigEndian(buffer4, uncompressedInfoSize);
    archive.Position = header.UncompressedInfoSizeOffset;
    archive.Write(buffer4);
    BinaryPrimitives.WriteUInt32BigEndian(buffer4, flags);
    archive.Position = header.FlagsOffset;
    archive.Write(buffer4);
  }

  private static long Align16(long value) => checked((value + 15) & ~15L);

  private static string ReadCString(Stream stream) {
    using var bytes = new MemoryStream();
    while (true) {
      var value = stream.ReadByte();
      if (value < 0)
        throw new EndOfStreamException("UnityFS contains an unterminated header string.");
      if (value == 0)
        return Encoding.UTF8.GetString(bytes.ToArray());
      bytes.WriteByte((byte)value);
    }
  }

  private static string ReadCString(byte[] data, ref int pos) {
    if ((uint)pos > (uint)data.Length)
      throw new InvalidDataException("UnityFS string offset is out of range.");
    var start = pos;
    while (pos < data.Length && data[pos] != 0)
      ++pos;
    if (pos >= data.Length)
      throw new InvalidDataException("UnityFS contains an unterminated node path.");
    var result = Encoding.UTF8.GetString(data, start, pos - start);
    ++pos;
    return result;
  }

  private static uint ReadUInt32BE(Stream stream) {
    Span<byte> buffer = stackalloc byte[4];
    stream.ReadExactly(buffer);
    return BinaryPrimitives.ReadUInt32BigEndian(buffer);
  }

  private static long ReadInt64BE(Stream stream) {
    Span<byte> buffer = stackalloc byte[8];
    stream.ReadExactly(buffer);
    return BinaryPrimitives.ReadInt64BigEndian(buffer);
  }

  private static uint ReadUInt32BE(byte[] data, ref int pos) {
    Require(data, pos, 4);
    var result = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos, 4));
    pos += 4;
    return result;
  }

  private static int ReadInt32BE(byte[] data, ref int pos) {
    Require(data, pos, 4);
    var result = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(pos, 4));
    pos += 4;
    return result;
  }

  private static long ReadInt64BE(byte[] data, ref int pos) {
    Require(data, pos, 8);
    var result = BinaryPrimitives.ReadInt64BigEndian(data.AsSpan(pos, 8));
    pos += 8;
    return result;
  }

  private static ushort ReadUInt16BE(byte[] data, ref int pos) {
    Require(data, pos, 2);
    var result = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(pos, 2));
    pos += 2;
    return result;
  }

  private static void WriteCString(Stream stream, string value) {
    var bytes = Encoding.UTF8.GetBytes(value);
    stream.Write(bytes);
    stream.WriteByte(0);
  }

  private static void WriteUInt16BE(Stream stream, ushort value) {
    Span<byte> buffer = stackalloc byte[2];
    BinaryPrimitives.WriteUInt16BigEndian(buffer, value);
    stream.Write(buffer);
  }

  private static void WriteUInt32BE(Stream stream, uint value) {
    Span<byte> buffer = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32BigEndian(buffer, value);
    stream.Write(buffer);
  }

  private static void WriteInt32BE(Stream stream, int value) {
    Span<byte> buffer = stackalloc byte[4];
    BinaryPrimitives.WriteInt32BigEndian(buffer, value);
    stream.Write(buffer);
  }

  private static void WriteInt64BE(Stream stream, long value) {
    Span<byte> buffer = stackalloc byte[8];
    BinaryPrimitives.WriteInt64BigEndian(buffer, value);
    stream.Write(buffer);
  }

  private static void Require(byte[] data, int pos, int count) {
    if (pos < 0 || count < 0 || pos > data.Length - count)
      throw new InvalidDataException("UnityFS BlocksInfo is truncated.");
  }

  private static void ValidateStream(Stream archive) {
    ArgumentNullException.ThrowIfNull(archive);
    if (!archive.CanRead || !archive.CanWrite || !archive.CanSeek)
      throw new NotSupportedException("UnityFS tail editing requires a readable, writable, seekable stream.");
  }
}
