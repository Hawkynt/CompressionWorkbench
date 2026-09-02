#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Core.Dictionary.Lz4;
using Compression.Core.Dictionary.Lzma;

namespace FileFormat.UnityBundle;

/// <summary>
/// Reads Unity Asset Bundles (<c>.unity3d</c> / <c>.assets</c> / <c>.bundle</c>). The modern
/// UnityFS layout stores a compressed BlocksInfo record that describes a sequence of storage
/// blocks (concatenated into one data stream) and a directory of nodes (assets) that slice
/// that stream by offset/size.
/// </summary>
public sealed class UnityBundleReader {
  private const uint BlocksInfoAtEnd = 0x80;
  private const uint DataAligned16 = 0x200;

  /// <summary>
  /// Represents a storage block.
  /// </summary>
  public sealed record StorageBlock(uint UncompressedSize, uint CompressedSize, ushort Flags);
  /// <summary>
  /// Represents a node.
  /// </summary>
  public sealed record Node(long Offset, long Size, uint Flags, string Path);

  private readonly byte[] _source;
  private readonly long _headerEnd;
  private readonly long _dataStreamOffset;
  private byte[]? _dataStream;

  /// <summary>
  /// Gets the signature.
  /// </summary>
  public string Signature { get; }
  /// <summary>
  /// Gets the format version.
  /// </summary>
  public uint FormatVersion { get; }
  /// <summary>
  /// Gets the unity version.
  /// </summary>
  public string UnityVersion { get; }
  /// <summary>
  /// Gets the unity revision.
  /// </summary>
  public string UnityRevision { get; }
  /// <summary>
  /// Gets the total size.
  /// </summary>
  public long TotalSize { get; }
  /// <summary>
  /// Gets the compressed blocks info size.
  /// </summary>
  public uint CompressedBlocksInfoSize { get; }
  /// <summary>
  /// Gets the uncompressed blocks info size.
  /// </summary>
  public uint UncompressedBlocksInfoSize { get; }
  /// <summary>
  /// Gets the flags.
  /// </summary>
  public uint Flags { get; }
  /// <summary>
  /// Gets the blocks.
  /// </summary>
  public IReadOnlyList<StorageBlock> Blocks { get; }
  /// <summary>
  /// Gets the nodes.
  /// </summary>
  public IReadOnlyList<Node> Nodes { get; }
  /// <summary>
  /// Gets a value indicating whether can extract.
  /// </summary>
  public bool CanExtract { get; }

  /// <summary>
  /// Initializes a new instance of <see cref="UnityBundleReader"/>.
  /// </summary>
  public UnityBundleReader(byte[] data) {
    ArgumentNullException.ThrowIfNull(data);
    this._source = data;

    var pos = 0;
    this.Signature = ReadCString(data, ref pos);
    if (this.Signature is not ("UnityFS" or "UnityWeb" or "UnityRaw" or "UnityArchive"))
      throw new InvalidDataException($"Not a Unity bundle: unexpected signature '{this.Signature}'.");

    this.FormatVersion = ReadUInt32BE(data, ref pos);
    this.UnityVersion = ReadCString(data, ref pos);
    this.UnityRevision = ReadCString(data, ref pos);

    if (this.Signature != "UnityFS") {
      this.TotalSize = 0;
      this.Blocks = [];
      this.Nodes = [];
      this._headerEnd = pos;
      this._dataStreamOffset = pos;
      this.CanExtract = false;
      return;
    }

    if (this.FormatVersion < 6)
      throw new InvalidDataException($"UnityFS format version {this.FormatVersion} predates the supported v6+ layout.");

    this.TotalSize = ReadInt64BE(data, ref pos);
    this.CompressedBlocksInfoSize = ReadUInt32BE(data, ref pos);
    this.UncompressedBlocksInfoSize = ReadUInt32BE(data, ref pos);
    this.Flags = ReadUInt32BE(data, ref pos);
    this._headerEnd = pos;

    if (this.TotalSize < 0)
      throw new InvalidDataException("UnityFS total size is negative.");
    if (this.TotalSize != 0 && this.TotalSize != data.LongLength)
      throw new InvalidDataException(
        $"UnityFS header declares {this.TotalSize} bytes but the input contains {data.LongLength} bytes.");
    if (this.CompressedBlocksInfoSize > Array.MaxLength || this.UncompressedBlocksInfoSize > Array.MaxLength)
      throw new NotSupportedException("UnityFS BlocksInfo exceeds the maximum managed array size.");

    if (this.FormatVersion >= 7)
      pos = Align16(pos, data.Length, "UnityFS aligned header");

    long blocksInfoOffset;
    if ((this.Flags & BlocksInfoAtEnd) != 0) {
      blocksInfoOffset = data.LongLength - this.CompressedBlocksInfoSize;
    } else {
      blocksInfoOffset = pos;
      pos = checked(pos + (int)this.CompressedBlocksInfoSize);
    }

    if (blocksInfoOffset < 0 || blocksInfoOffset + this.CompressedBlocksInfoSize > data.LongLength)
      throw new InvalidDataException("UnityFS BlocksInfo offset is out of range.");

    var biCompressed = new byte[(int)this.CompressedBlocksInfoSize];
    Array.Copy(data, blocksInfoOffset, biCompressed, 0, biCompressed.Length);
    var blocksInfoCompression = checked((int)(this.Flags & 0x3F));
    var blocksInfo = DecompressBlock(
      biCompressed, checked((int)this.UncompressedBlocksInfoSize), blocksInfoCompression);

    var (blocks, nodes) = ParseBlocksInfo(blocksInfo);
    this.Blocks = blocks;
    this.Nodes = nodes;

    long dataOffset;
    if ((this.Flags & BlocksInfoAtEnd) != 0) {
      dataOffset = this._headerEnd;
      if (this.FormatVersion >= 7)
        dataOffset = Align16(dataOffset, data.LongLength, "UnityFS aligned header");
    } else {
      dataOffset = blocksInfoOffset + this.CompressedBlocksInfoSize;
    }
    if ((this.Flags & DataAligned16) != 0)
      dataOffset = Align16(dataOffset, data.LongLength, "UnityFS aligned data stream");
    this._dataStreamOffset = dataOffset;

    var compressedDataLength = blocks.Sum(block => (long)block.CompressedSize);
    var dataLimit = (this.Flags & BlocksInfoAtEnd) != 0 ? blocksInfoOffset : data.LongLength;
    if (this._dataStreamOffset < 0 || this._dataStreamOffset + compressedDataLength > dataLimit)
      throw new InvalidDataException("UnityFS storage blocks extend outside the data region.");

    var logicalLength = blocks.Sum(block => (long)block.UncompressedSize);
    foreach (var node in nodes) {
      if (node.Offset < 0 || node.Size < 0 || node.Offset > logicalLength || node.Size > logicalLength - node.Offset)
        throw new InvalidDataException(
          $"UnityFS node '{node.Path}' range [{node.Offset},{node.Offset + node.Size}) falls outside the logical data stream ({logicalLength} bytes).");
    }

    this.CanExtract = blocks.All(block => (block.Flags & 0x3F) is 0 or 1 or 2 or 3);
  }

  /// <summary>
  /// Performs the extract node operation.
  /// </summary>
  public byte[] ExtractNode(Node node) {
    ArgumentNullException.ThrowIfNull(node);
    if (this.Nodes.Count == 0)
      throw new InvalidOperationException("Bundle has no node directory (legacy format?).");
    if (node.Size > Array.MaxLength)
      throw new NotSupportedException($"UnityFS node '{node.Path}' is too large to materialize as a byte array.");

    var stream = this.GetDataStream();
    if (node.Offset < 0 || node.Size < 0 || node.Offset > stream.LongLength || node.Size > stream.LongLength - node.Offset)
      throw new InvalidDataException(
        $"Node '{node.Path}' range [{node.Offset},{node.Offset + node.Size}) falls outside the data stream ({stream.LongLength} bytes).");

    var result = new byte[(int)node.Size];
    Array.Copy(stream, node.Offset, result, 0, result.Length);
    return result;
  }

  /// <summary>
  /// Gets the data stream.
  /// </summary>
  public byte[] GetDataStream() {
    if (this._dataStream != null)
      return this._dataStream;

    var total = this.Blocks.Sum(block => (long)block.UncompressedSize);
    if (total > Array.MaxLength)
      throw new NotSupportedException("UnityFS logical data stream is too large to materialize as a byte array.");

    var output = new byte[(int)total];
    var outPos = 0;
    var inPos = this._dataStreamOffset;

    foreach (var block in this.Blocks) {
      if (block.CompressedSize > Array.MaxLength)
        throw new NotSupportedException("UnityFS storage block is too large to materialize as a byte array.");
      if (inPos < 0 || inPos + block.CompressedSize > this._source.LongLength)
        throw new InvalidDataException("UnityFS storage block extends past the end of the bundle.");

      var compressed = new byte[(int)block.CompressedSize];
      Array.Copy(this._source, inPos, compressed, 0, compressed.Length);
      inPos += block.CompressedSize;

      var decompressed = DecompressBlock(compressed, checked((int)block.UncompressedSize), block.Flags & 0x3F);
      if (decompressed.Length != block.UncompressedSize)
        throw new InvalidDataException("UnityFS storage block decoded to a different size than declared.");
      Array.Copy(decompressed, 0, output, outPos, decompressed.Length);
      outPos += decompressed.Length;
    }

    this._dataStream = output;
    return output;
  }

  private static (List<StorageBlock> Blocks, List<Node> Nodes) ParseBlocksInfo(byte[] blocksInfo) {
    if (blocksInfo.Length < 20)
      throw new InvalidDataException("UnityFS BlocksInfo is truncated before the block count.");

    var pos = 16;
    var blockCount = ReadInt32BE(blocksInfo, ref pos);
    if (blockCount < 0)
      throw new InvalidDataException("Negative UnityFS block count.");
    if ((long)blockCount * 10 > blocksInfo.Length - pos)
      throw new InvalidDataException("UnityFS block table exceeds the BlocksInfo buffer.");

    var blocks = new List<StorageBlock>(blockCount);
    for (var i = 0; i < blockCount; ++i) {
      var uncompressedSize = ReadUInt32BE(blocksInfo, ref pos);
      var compressedSize = ReadUInt32BE(blocksInfo, ref pos);
      var flags = ReadUInt16BE(blocksInfo, ref pos);
      blocks.Add(new StorageBlock(uncompressedSize, compressedSize, flags));
    }

    var nodeCount = ReadInt32BE(blocksInfo, ref pos);
    if (nodeCount < 0)
      throw new InvalidDataException("Negative UnityFS node count.");
    if ((long)nodeCount * 21 > blocksInfo.Length - pos)
      throw new InvalidDataException("UnityFS node table cannot fit in the remaining BlocksInfo buffer.");

    var nodes = new List<Node>(nodeCount);
    for (var i = 0; i < nodeCount; ++i) {
      var offset = ReadInt64BE(blocksInfo, ref pos);
      var size = ReadInt64BE(blocksInfo, ref pos);
      var flags = ReadUInt32BE(blocksInfo, ref pos);
      var path = ReadCString(blocksInfo, ref pos);
      nodes.Add(new Node(offset, size, flags, path));
    }
    return (blocks, nodes);
  }

  private static byte[] DecompressBlock(byte[] compressed, int uncompressedSize, int compressionType) {
    if (uncompressedSize < 0)
      throw new InvalidDataException("UnityFS block declares a negative decoded size.");
    switch (compressionType) {
      case 0:
        if (compressed.Length != uncompressedSize)
          throw new InvalidDataException(
            $"UnityFS Stored block has {compressed.Length} bytes but declares {uncompressedSize} bytes.");
        return compressed;
      case 1: {
        if (compressed.Length < 5)
          throw new InvalidDataException("UnityFS LZMA block truncated: missing properties.");
        var props = compressed.AsSpan(0, 5).ToArray();
        using var input = new MemoryStream(compressed, 5, compressed.Length - 5, writable: false);
        var decoder = new LzmaDecoder(input, props, uncompressedSize);
        var decoded = decoder.Decode();
        if (decoded.Length != uncompressedSize)
          throw new InvalidDataException("UnityFS LZMA block decoded to a different size than declared.");
        return decoded;
      }
      case 2:
      case 3: {
        var decoded = Lz4BlockDecompressor.Decompress(compressed, uncompressedSize);
        if (decoded.Length != uncompressedSize)
          throw new InvalidDataException("UnityFS LZ4 block decoded to a different size than declared.");
        return decoded;
      }
      default:
        throw new NotSupportedException($"UnityFS block compression type {compressionType} is not supported.");
    }
  }

  private static string ReadCString(byte[] data, ref int pos) {
    if ((uint)pos > (uint)data.Length)
      throw new InvalidDataException("UnityFS string offset is out of range.");
    var start = pos;
    while (pos < data.Length && data[pos] != 0)
      pos++;
    if (pos >= data.Length)
      throw new InvalidDataException("UnityFS contains an unterminated string.");
    var value = Encoding.UTF8.GetString(data, start, pos - start);
    pos++;
    return value;
  }

  private static int Align16(int position, int length, string section) {
    var aligned = checked((position + 15) & ~15);
    if (aligned > length)
      throw new InvalidDataException($"{section} extends past end of file.");
    return aligned;
  }

  private static long Align16(long position, long length, string section) {
    var aligned = checked((position + 15L) & ~15L);
    if (aligned > length)
      throw new InvalidDataException($"{section} extends past end of file.");
    return aligned;
  }

  private static uint ReadUInt32BE(byte[] data, ref int pos) {
    Require(data, pos, 4);
    var value = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos, 4));
    pos += 4;
    return value;
  }

  private static int ReadInt32BE(byte[] data, ref int pos) {
    Require(data, pos, 4);
    var value = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(pos, 4));
    pos += 4;
    return value;
  }

  private static long ReadInt64BE(byte[] data, ref int pos) {
    Require(data, pos, 8);
    var value = BinaryPrimitives.ReadInt64BigEndian(data.AsSpan(pos, 8));
    pos += 8;
    return value;
  }

  private static ushort ReadUInt16BE(byte[] data, ref int pos) {
    Require(data, pos, 2);
    var value = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(pos, 2));
    pos += 2;
    return value;
  }

  private static void Require(byte[] data, int position, int count) {
    if (position < 0 || count < 0 || position > data.Length - count)
      throw new InvalidDataException("UnityFS structure is truncated.");
  }
}
