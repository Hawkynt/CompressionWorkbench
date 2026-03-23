using System.Buffers.Binary;
using System.Text;
using Compression.Core.Dictionary.Lzms;
using Compression.Core.Dictionary.Lzx;
using Compression.Core.Dictionary.Xpress;

namespace FileFormat.Wim;

/// <summary>
/// Writes a WIM (Windows Imaging) file to a stream.
/// </summary>
/// <remarks>
/// <para>
/// Resources are compressed independently using the configured compression type.
/// Each compressed resource is prefixed with a chunk table listing the compressed
/// size of each chunk. The final WIM container includes:
/// </para>
/// <list type="number">
///   <item><description>A 208-byte file header.</description></item>
///   <item><description>Zero or more compressed resource payloads.</description></item>
///   <item><description>A flat resource table describing every resource.</description></item>
///   <item><description>A minimal UTF-8 XML metadata block.</description></item>
/// </list>
/// </remarks>
public sealed class WimWriter {
  private readonly Stream _output;
  private readonly uint _compressionType;
  private readonly int _chunkSize;

  /// <summary>
  /// Initializes a new <see cref="WimWriter"/>.
  /// </summary>
  /// <param name="output">The stream to write the WIM to. Must be seekable.</param>
  /// <param name="compressionType">
  /// The compression type to use for resources.
  /// Use one of the <c>WimConstants.Compression*</c> constants.
  /// Defaults to <see cref="WimConstants.CompressionXpress"/>.
  /// </param>
  /// <param name="chunkSize">
  /// The maximum uncompressed size of each chunk within a resource.
  /// Defaults to <see cref="WimConstants.DefaultChunkSize"/> (32 KB).
  /// </param>
  /// <exception cref="ArgumentNullException">Thrown when <paramref name="output"/> is null.</exception>
  /// <exception cref="ArgumentOutOfRangeException">
  /// Thrown when <paramref name="chunkSize"/> is not positive.
  /// </exception>
  public WimWriter(
    Stream output,
    uint compressionType = WimConstants.CompressionXpress,
    int chunkSize = WimConstants.DefaultChunkSize) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(chunkSize, 0);

    this._output = output;
    this._compressionType = compressionType;
    this._chunkSize = chunkSize;
  }

  /// <summary>
  /// Writes a complete WIM file containing the given resources to the output stream.
  /// </summary>
  /// <param name="resources">
  /// The list of resource byte arrays to store in the WIM.
  /// Each element becomes one resource entry in the resource table.
  /// </param>
  /// <exception cref="ArgumentNullException">Thrown when <paramref name="resources"/> is null.</exception>
  public void Write(IReadOnlyList<byte[]> resources) {
    ArgumentNullException.ThrowIfNull(resources);

    // Reserve space for the header — we will seek back and fill it at the end.
    Span<byte> zeroes = stackalloc byte[WimConstants.HeaderSize];
    zeroes.Clear();
    this._output.Write(zeroes);

    // Compress and write each resource, recording where it landed.
    var entries = new List<WimResourceEntry>(resources.Count);
    foreach (var resource in resources) {
      var entry = this.WriteResource(resource);
      entries.Add(entry);
    }

    // Write the flat resource table.
    var offsetTableOffset = this._output.Position;
    var offsetTableSize   = WriteResourceTable(entries);

    // Write minimal XML metadata.
    var xmlOffset     = this._output.Position;
    var xmlSize       = WriteXmlMetadata(resources.Count);
    var xmlOffsetEnd  = this._output.Position;
    _ = xmlOffsetEnd; // suppress unused warning

    // Determine compression flags for the header.
    var wimFlags = this._compressionType switch {
      WimConstants.CompressionXpress         => WimConstants.FlagXpressCompression,
      WimConstants.CompressionLzx            => WimConstants.FlagLzxCompression,
      WimConstants.CompressionLzms           => WimConstants.FlagLzmsCompression,
      WimConstants.CompressionXpressHuffman  => WimConstants.FlagXpressHuffmanCompression,
      _                                      => 0u,
    };

    // Seek back and write the real header.
    this._output.Seek(0, SeekOrigin.Begin);
    var header = new WimHeader {
      WimFlags              = wimFlags,
      CompressionType       = this._compressionType,
      ChunkSize             = (uint)this._chunkSize,
      ImageCount            = (uint)resources.Count,
      OffsetTableResource   = new WimResourceEntry(
        CompressedSize: offsetTableSize,
        OriginalSize:   offsetTableSize,
        Offset:         offsetTableOffset,
        Flags:          WimConstants.ResourceFlagUncompressed),
      XmlDataResource = new WimResourceEntry(
        CompressedSize: xmlSize,
        OriginalSize:   xmlSize,
        Offset:         xmlOffset,
        Flags:          WimConstants.ResourceFlagUncompressed),
    };
    header.Write(this._output);
    this._output.Seek(0, SeekOrigin.End);
  }

  // -------------------------------------------------------------------------
  // Resource writing
  // -------------------------------------------------------------------------

  /// <summary>
  /// Compresses <paramref name="data"/> in chunks and writes it to the output stream.
  /// Returns the resource table entry describing the written payload.
  /// </summary>
  private WimResourceEntry WriteResource(byte[] data) {
    var resourceOffset = this._output.Position;

    if (data.Length == 0 || this._compressionType == WimConstants.CompressionNone) {
      // Write raw, uncompressed.
      this._output.Write(data);
      return new WimResourceEntry(
        CompressedSize: data.Length,
        OriginalSize:   data.Length,
        Offset:         resourceOffset,
        Flags:          WimConstants.ResourceFlagUncompressed);
    }

    // Split data into chunks and compress each independently.
    var chunkCount = (data.Length + this._chunkSize - 1) / this._chunkSize;

    // The chunk table holds (chunkCount - 1) entries, each an 8-byte LE compressed size.
    // The last chunk's size is implicit (total compressed size minus sum of others).
    // We write a placeholder chunk table, then fill it in after compressing.
    var chunkTableOffset = this._output.Position;
    var chunkTableBytes   = (chunkCount - 1) * 8;

    if (chunkTableBytes > 0) {
      var chunkTablePlaceholder = chunkTableBytes <= 512
        ? stackalloc byte[chunkTableBytes]
        : new byte[chunkTableBytes];
      chunkTablePlaceholder.Clear();
      this._output.Write(chunkTablePlaceholder);
    }

    // Compress each chunk and remember their compressed sizes.
    var firstChunkDataOffset = this._output.Position;
    var compressedSizes = new long[chunkCount];

    for (var i = 0; i < chunkCount; ++i) {
      var chunkStart  = i * this._chunkSize;
      var chunkLength = Math.Min(this._chunkSize, data.Length - chunkStart);
      var chunkData   = data.AsSpan(chunkStart, chunkLength);

      var compressed = this.CompressChunk(chunkData);

      // If compression expanded the data, store the chunk uncompressed.
      // (WIM readers are expected to handle this, but for simplicity we always
      //  store the compressed form as the format intends.)
      this._output.Write(compressed);
      compressedSizes[i] = compressed.Length;
    }

    var resourceEnd     = this._output.Position;
    var totalCompressed = resourceEnd - chunkTableOffset;

    // Seek back and write the real chunk table (all but the last entry).
    if (chunkTableBytes > 0) {
      this._output.Seek(chunkTableOffset, SeekOrigin.Begin);
      Span<byte> entry = stackalloc byte[8];
      var runningOffset = firstChunkDataOffset - chunkTableOffset; // offset of first chunk data relative to resource start
      // The chunk table stores the compressed offset of each chunk except the first.
      // More precisely: the chunk table stores compressed sizes of chunks 0..N-2.
      for (var i = 0; i < chunkCount - 1; ++i) {
        BinaryPrimitives.WriteInt64LittleEndian(entry, compressedSizes[i]);
        this._output.Write(entry);
      }
      this._output.Seek(resourceEnd, SeekOrigin.Begin);
    }

    return new WimResourceEntry(
      CompressedSize: totalCompressed,
      OriginalSize:   data.Length,
      Offset:         resourceOffset,
      Flags:          WimConstants.ResourceFlagCompressed);
  }

  // -------------------------------------------------------------------------
  // Compression dispatch
  // -------------------------------------------------------------------------

  private byte[] CompressChunk(ReadOnlySpan<byte> chunk) =>
    this._compressionType switch {
      WimConstants.CompressionXpress        => new XpressCompressor().Compress(chunk),
      WimConstants.CompressionXpressHuffman => new XpressHuffmanCompressor().Compress(chunk),
      WimConstants.CompressionLzx           => new LzxCompressor(WimConstants.LzxWindowBits).Compress(chunk),
      WimConstants.CompressionLzms => new LzmsCompressor().Compress(chunk),
      _ => throw new NotSupportedException(
        $"Unsupported WIM compression type: {this._compressionType}.")
    };

  // -------------------------------------------------------------------------
  // Resource table
  // -------------------------------------------------------------------------

  /// <summary>
  /// Writes the flat resource table to the output and returns the number of bytes written.
  /// </summary>
  private long WriteResourceTable(List<WimResourceEntry> entries) {
    var start = this._output.Position;
    Span<byte> buf = stackalloc byte[WimConstants.ResourceEntrySize];

    foreach (var e in entries) {
      buf.Clear();
      BinaryPrimitives.WriteInt64LittleEndian(buf,       e.CompressedSize);
      BinaryPrimitives.WriteInt64LittleEndian(buf[8..],  e.OriginalSize);
      BinaryPrimitives.WriteInt64LittleEndian(buf[16..], e.Offset);
      BinaryPrimitives.WriteUInt32LittleEndian(buf[24..], e.Flags);
      this._output.Write(buf);
    }

    return this._output.Position - start;
  }

  // -------------------------------------------------------------------------
  // XML metadata
  // -------------------------------------------------------------------------

  /// <summary>
  /// Writes a minimal XML metadata block and returns its byte length.
  /// </summary>
  private long WriteXmlMetadata(int imageCount) {
    var start = this._output.Position;

    var sb = new StringBuilder();
    sb.Append("<WIM>");
    for (var i = 1; i <= imageCount; ++i) {
      sb.Append($"<IMAGE Index=\"{i}\"><NAME>Data</NAME></IMAGE>");
    }
    sb.Append("</WIM>");

    var xmlBytes = Encoding.UTF8.GetBytes(sb.ToString());
    this._output.Write(xmlBytes);

    return this._output.Position - start;
  }

  /// <summary>
  /// Creates a WIM file split into multiple volumes.
  /// </summary>
  /// <param name="maxVolumeSize">Maximum size of each volume in bytes.</param>
  /// <param name="resources">The resource data to store.</param>
  /// <param name="compressionType">The compression type to use.</param>
  /// <returns>An array of byte arrays, one per volume.</returns>
  public static byte[][] CreateSplit(long maxVolumeSize,
      IReadOnlyList<byte[]> resources,
      uint compressionType = WimConstants.CompressionXpress) {
    using var ms = new MemoryStream();
    var writer = new WimWriter(ms, compressionType);
    writer.Write(resources);
    return Compression.Core.Streams.VolumeHelper.SplitIntoVolumes(ms.ToArray(), maxVolumeSize);
  }
}
