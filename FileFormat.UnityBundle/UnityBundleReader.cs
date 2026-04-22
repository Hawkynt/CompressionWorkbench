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
/// <para>
/// Supported signatures: <c>UnityFS\0</c> (modern, UnityFS version 6+),
/// <c>UnityWeb\0</c>/<c>UnityRaw\0</c> (legacy, header parsed only — no node directory is
/// extracted since the classic format uses a different container). Only the UnityFS variant
/// surfaces assets.
/// </para>
/// <para>
/// Compression for BlocksInfo and individual storage blocks is indicated by the low 6 bits
/// of a flags field: 0 = none, 1 = LZMA (raw, 5-byte properties + stream), 2 = LZ4,
/// 3 = LZ4HC (same block format as LZ4).
/// </para>
/// </summary>
public sealed class UnityBundleReader {

  /// <summary>A single UnityFS storage block (compression unit inside the bundle).</summary>
  public sealed record StorageBlock(uint UncompressedSize, uint CompressedSize, ushort Flags);

  /// <summary>A single asset (node) inside the reconstructed data stream.</summary>
  public sealed record Node(long Offset, long Size, uint Flags, string Path);

  private readonly byte[] _source;
  private readonly long _headerEnd;

  /// <summary>The signature string (e.g. "UnityFS").</summary>
  public string Signature { get; }
  /// <summary>File-format version from the header (typically 6 or 7).</summary>
  public uint FormatVersion { get; }
  /// <summary>Unity version (e.g. "5.x.x").</summary>
  public string UnityVersion { get; }
  /// <summary>Unity engine revision (e.g. "2019.4.11f1").</summary>
  public string UnityRevision { get; }
  /// <summary>Total bundle size from the header.</summary>
  public long TotalSize { get; }
  /// <summary>Compressed BlocksInfo size (bytes).</summary>
  public uint CompressedBlocksInfoSize { get; }
  /// <summary>Uncompressed BlocksInfo size (bytes).</summary>
  public uint UncompressedBlocksInfoSize { get; }
  /// <summary>Raw flags field (low 6 bits = BlocksInfo compression, bit 6 = dir combined, bit 7 = at end).</summary>
  public uint Flags { get; }

  /// <summary>Storage blocks described by BlocksInfo. Empty when the bundle isn't UnityFS.</summary>
  public IReadOnlyList<StorageBlock> Blocks { get; }
  /// <summary>Asset node directory. Empty when the bundle isn't UnityFS.</summary>
  public IReadOnlyList<Node> Nodes { get; }

  /// <summary>True if every block in the bundle uses a compression we can decode.</summary>
  public bool CanExtract { get; }

  private readonly long _dataStreamOffset;
  private byte[]? _dataStream;

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
      // Legacy bundle — we don't decode the old Web/Raw layouts; just expose the header info.
      this.TotalSize = 0;
      this.Blocks = [];
      this.Nodes = [];
      this._headerEnd = pos;
      this._dataStreamOffset = pos;
      this.CanExtract = false;
      return;
    }

    this.TotalSize = ReadInt64BE(data, ref pos);
    this.CompressedBlocksInfoSize = ReadUInt32BE(data, ref pos);
    this.UncompressedBlocksInfoSize = ReadUInt32BE(data, ref pos);
    this.Flags = ReadUInt32BE(data, ref pos);
    this._headerEnd = pos;

    // UnityFS v7+ aligns the header to a 16-byte boundary.
    if (this.FormatVersion >= 7) {
      while ((pos & 0xF) != 0) pos++;
    }

    // Flag bit 7 (0x80): BlocksInfo is at end of file, not immediately after the header.
    long blocksInfoOffset;
    if ((this.Flags & 0x80) != 0) {
      blocksInfoOffset = data.LongLength - this.CompressedBlocksInfoSize;
    } else {
      blocksInfoOffset = pos;
      pos += (int)this.CompressedBlocksInfoSize;
    }

    if (blocksInfoOffset < 0 || blocksInfoOffset + this.CompressedBlocksInfoSize > data.LongLength)
      throw new InvalidDataException("UnityFS BlocksInfo offset is out of range.");

    var biCompressed = new byte[this.CompressedBlocksInfoSize];
    Array.Copy(data, blocksInfoOffset, biCompressed, 0, (int)this.CompressedBlocksInfoSize);

    var blocksInfoCompression = (int)(this.Flags & 0x3F);
    var blocksInfo = DecompressBlock(
      biCompressed, (int)this.UncompressedBlocksInfoSize, blocksInfoCompression);

    // Parse BlocksInfo: 16 byte hash + int32 block count + block[] + int32 node count + node[].
    var (blocks, nodes) = ParseBlocksInfo(blocksInfo);
    this.Blocks = blocks;
    this.Nodes = nodes;

    // The storage data stream starts immediately after the BlocksInfo when the flag says so,
    // or right after the header otherwise.
    if ((this.Flags & 0x80) != 0) {
      this._dataStreamOffset = this._headerEnd;
      if (this.FormatVersion >= 7) {
        while ((this._dataStreamOffset & 0xF) != 0) this._dataStreamOffset++;
      }
    } else {
      this._dataStreamOffset = blocksInfoOffset + this.CompressedBlocksInfoSize;
    }

    // Can we extract? Only if every block uses a compression we understand.
    this.CanExtract = blocks.All(b => {
      var c = b.Flags & 0x3F;
      return c is 0 or 1 or 2 or 3;
    });
  }

  /// <summary>
  /// Returns the decompressed bytes of a single asset node. Nodes are resolved against the
  /// concatenated (decompressed) storage stream. Throws when the bundle isn't UnityFS or when
  /// any contributing storage block uses an unsupported compression type.
  /// </summary>
  public byte[] ExtractNode(Node node) {
    ArgumentNullException.ThrowIfNull(node);
    if (this.Nodes.Count == 0)
      throw new InvalidOperationException("Bundle has no node directory (legacy format?).");

    var stream = this.GetDataStream();
    if (node.Offset < 0 || node.Offset + node.Size > stream.LongLength)
      throw new InvalidDataException(
        $"Node '{node.Path}' range [{node.Offset},{node.Offset + node.Size}) falls outside the data stream ({stream.LongLength} bytes).");

    var result = new byte[node.Size];
    Array.Copy(stream, node.Offset, result, 0, (int)node.Size);
    return result;
  }

  /// <summary>
  /// Returns (or materializes) the concatenated, decompressed storage data stream described
  /// by <see cref="Blocks"/>.
  /// </summary>
  public byte[] GetDataStream() {
    if (this._dataStream != null) return this._dataStream;

    var total = this.Blocks.Sum(b => (long)b.UncompressedSize);
    var output = new byte[total];
    var outPos = 0L;
    var inPos = this._dataStreamOffset;

    foreach (var block in this.Blocks) {
      if (inPos + block.CompressedSize > this._source.LongLength)
        throw new InvalidDataException("UnityFS storage block extends past the end of the bundle.");
      var compressed = new byte[block.CompressedSize];
      Array.Copy(this._source, inPos, compressed, 0, (int)block.CompressedSize);
      inPos += block.CompressedSize;

      var decompressed = DecompressBlock(compressed, (int)block.UncompressedSize, block.Flags & 0x3F);
      Array.Copy(decompressed, 0, output, outPos, decompressed.Length);
      outPos += decompressed.Length;
    }

    this._dataStream = output;
    return output;
  }

  private static (List<StorageBlock> Blocks, List<Node> Nodes) ParseBlocksInfo(byte[] blocksInfo) {
    var pos = 16; // skip 16-byte hash
    var blockCount = ReadInt32BE(blocksInfo, ref pos);
    if (blockCount < 0) throw new InvalidDataException("Negative UnityFS block count.");

    var blocks = new List<StorageBlock>(blockCount);
    for (var i = 0; i < blockCount; ++i) {
      var uncompressedSize = ReadUInt32BE(blocksInfo, ref pos);
      var compressedSize = ReadUInt32BE(blocksInfo, ref pos);
      var flags = ReadUInt16BE(blocksInfo, ref pos);
      blocks.Add(new StorageBlock(uncompressedSize, compressedSize, flags));
    }

    var nodeCount = ReadInt32BE(blocksInfo, ref pos);
    if (nodeCount < 0) throw new InvalidDataException("Negative UnityFS node count.");

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
    switch (compressionType) {
      case 0:
        // Stored — length may equal uncompressedSize; copy verbatim (trim if oversized).
        if (compressed.Length == uncompressedSize) return compressed;
        var stored = new byte[uncompressedSize];
        Array.Copy(compressed, 0, stored, 0, Math.Min(compressed.Length, uncompressedSize));
        return stored;
      case 1: {
        // Raw LZMA: 5 property bytes followed by raw LZMA stream. No uncompressed-size prefix.
        if (compressed.Length < 5)
          throw new InvalidDataException("UnityFS LZMA block truncated: missing properties.");
        var props = new byte[5];
        Array.Copy(compressed, 0, props, 0, 5);
        using var input = new MemoryStream(compressed, 5, compressed.Length - 5);
        var decoder = new LzmaDecoder(input, props, uncompressedSize);
        return decoder.Decode();
      }
      case 2:
      case 3:
        // LZ4 / LZ4HC share the same block format.
        return Lz4BlockDecompressor.Decompress(compressed, uncompressedSize);
      default:
        throw new NotSupportedException(
          $"UnityFS block compression type {compressionType} is not supported.");
    }
  }

  // ─── primitive readers ────────────────────────────────────────────────────

  private static string ReadCString(byte[] data, ref int pos) {
    var start = pos;
    while (pos < data.Length && data[pos] != 0) pos++;
    var s = Encoding.UTF8.GetString(data, start, pos - start);
    if (pos < data.Length) pos++; // skip null terminator
    return s;
  }

  private static uint ReadUInt32BE(byte[] data, ref int pos) {
    if (pos + 4 > data.Length) throw new InvalidDataException("UnityFS header truncated.");
    var v = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos));
    pos += 4;
    return v;
  }

  private static int ReadInt32BE(byte[] data, ref int pos) {
    if (pos + 4 > data.Length) throw new InvalidDataException("UnityFS header truncated.");
    var v = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(pos));
    pos += 4;
    return v;
  }

  private static long ReadInt64BE(byte[] data, ref int pos) {
    if (pos + 8 > data.Length) throw new InvalidDataException("UnityFS header truncated.");
    var v = BinaryPrimitives.ReadInt64BigEndian(data.AsSpan(pos));
    pos += 8;
    return v;
  }

  private static ushort ReadUInt16BE(byte[] data, ref int pos) {
    if (pos + 2 > data.Length) throw new InvalidDataException("UnityFS header truncated.");
    var v = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(pos));
    pos += 2;
    return v;
  }
}
