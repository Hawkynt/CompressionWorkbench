#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Core.Dictionary.Lz4;
using Compression.Core.Dictionary.Lzma;
using Compression.Registry;

namespace FileFormat.UnityBundle;

/// <summary>
/// Clean-room UnityFS writer for modern Unity Asset Bundles. The writer emits
/// version 6-8 UnityFS containers with a combined block/directory table and
/// supports Stored, raw LZMA, LZ4 and LZ4HC storage blocks. Input files are
/// concatenated into the logical data stream, split into independently
/// compressed blocks, and described by node offsets in BlocksInfo.
/// </summary>
public static class UnityBundleWriter {
  private const uint HasDirectoryInfo = 0x40;
  private const uint BlocksInfoAtEnd = 0x80;
  private const int DefaultBlockSize = 128 * 1024;
  private const int MinBlockSize = 4 * 1024;
  private const int MaxBlockSize = 16 * 1024 * 1024;

  private sealed record InputEntry(string Path, byte[] Data);
  private sealed record NodeRecord(long Offset, long Size, uint Flags, string Path);
  private sealed record EncodedBlock(uint UncompressedSize, byte[] Data, ushort Flags);

  /// <summary>Writes a complete UnityFS bundle.</summary>
  public static void Write(
      Stream output,
      IReadOnlyList<ArchiveInputInfo> inputs,
      FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    ArgumentNullException.ThrowIfNull(options);
    if (!output.CanWrite)
      throw new ArgumentException("UnityFS output stream must be writable.", nameof(output));
    if (!string.IsNullOrEmpty(options.Password) || options.EncryptFilenames)
      throw new NotSupportedException("UnityFS creation does not define archive encryption.");

    var formatVersion = options.GetOptionInt("FormatVersion", 7);
    if (formatVersion is < 6 or > 8)
      throw new ArgumentOutOfRangeException(nameof(options), "UnityFS FormatVersion must be 6, 7, or 8.");

    var unityVersion = ValidateCString(options.GetOption("UnityVersion", "5.x.x"), "UnityVersion");
    var unityRevision = ValidateCString(options.GetOption("UnityRevision", "2022.3.0f1"), "UnityRevision");
    var blockSize = options.GetOptionInt("BlockSize", DefaultBlockSize);
    if (blockSize < MinBlockSize || blockSize > MaxBlockSize)
      throw new ArgumentOutOfRangeException(nameof(options),
        $"UnityFS BlockSize must be between {MinBlockSize} and {MaxBlockSize} bytes.");

    var requestedDataMethod = NormalizeMethod(options.MethodName, fallback: "auto");
    var requestedInfoMethod = NormalizeMethod(options.GetOption("BlocksInfoCompression", "lz4hc"), fallback: "lz4hc");
    var blocksInfoAtEnd = options.GetOptionBool("BlocksInfoAtEnd", false);

    var sourceEntries = NormalizeInputs(inputs);
    var (dataStream, nodes) = BuildLogicalDataStream(sourceEntries);
    var blocks = EncodeDataBlocks(dataStream, blockSize, requestedDataMethod, options);
    var blocksInfo = BuildBlocksInfo(blocks, nodes);
    var encodedBlocksInfo = Encode(blocksInfo, requestedInfoMethod, options);

    using var bundle = new MemoryStream();
    WriteCString(bundle, "UnityFS");
    WriteUInt32BE(bundle, checked((uint)formatVersion));
    WriteCString(bundle, unityVersion);
    WriteCString(bundle, unityRevision);

    var totalSizeOffset = checked((int)bundle.Position);
    WriteInt64BE(bundle, 0);
    WriteUInt32BE(bundle, checked((uint)encodedBlocksInfo.Data.Length));
    WriteUInt32BE(bundle, checked((uint)blocksInfo.Length));

    var headerFlags = HasDirectoryInfo | encodedBlocksInfo.Flags;
    if (blocksInfoAtEnd)
      headerFlags |= BlocksInfoAtEnd;
    WriteUInt32BE(bundle, headerFlags);

    if (formatVersion >= 7)
      Align16(bundle);

    if (!blocksInfoAtEnd)
      bundle.Write(encodedBlocksInfo.Data);

    foreach (var block in blocks)
      bundle.Write(block.Data);

    if (blocksInfoAtEnd)
      bundle.Write(encodedBlocksInfo.Data);

    var bytes = bundle.ToArray();
    BinaryPrimitives.WriteInt64BigEndian(bytes.AsSpan(totalSizeOffset, 8), bytes.LongLength);

    if (output.CanSeek) {
      output.Position = 0;
      output.SetLength(0);
    }
    output.Write(bytes);
    if (output.CanSeek)
      output.SetLength(output.Position);
  }

  private static List<InputEntry> NormalizeInputs(IReadOnlyList<ArchiveInputInfo> inputs) {
    var result = new List<InputEntry>();
    var paths = new HashSet<string>(StringComparer.Ordinal);
    foreach (var input in inputs) {
      if (input.IsDirectory)
        continue;
      var path = NormalizePath(input.ArchiveName);
      if (!paths.Add(path))
        throw new ArgumentException($"UnityFS contains duplicate node path '{path}'.", nameof(inputs));
      result.Add(new InputEntry(path, input.ReadContent()));
    }
    result.Sort((a, b) => StringComparer.Ordinal.Compare(a.Path, b.Path));
    return result;
  }

  private static string NormalizePath(string path) {
    if (string.IsNullOrWhiteSpace(path))
      throw new ArgumentException("UnityFS node path must not be empty.", nameof(path));
    var normalized = path.Replace('\\', '/').TrimStart('/');
    if (normalized.Length == 0 || normalized.EndsWith('/'))
      throw new ArgumentException("UnityFS node path must name a file.", nameof(path));
    foreach (var part in normalized.Split('/')) {
      if (part.Length == 0 || part is "." or "..")
        throw new ArgumentException("UnityFS node paths may not contain empty, '.' or '..' components.", nameof(path));
      if (part.IndexOf('\0') >= 0)
        throw new ArgumentException("UnityFS node paths may not contain NUL characters.", nameof(path));
    }
    return normalized;
  }

  private static (byte[] Data, List<NodeRecord> Nodes) BuildLogicalDataStream(List<InputEntry> entries) {
    using var data = new MemoryStream();
    var nodes = new List<NodeRecord>(entries.Count);
    foreach (var entry in entries) {
      var offset = data.Position;
      if (entry.Data.Length > 0)
        data.Write(entry.Data);
      nodes.Add(new NodeRecord(offset, entry.Data.LongLength, NodeFlags(entry.Path), entry.Path));
    }
    return (data.ToArray(), nodes);
  }

  private static uint NodeFlags(string path)
    => path.EndsWith(".assets", StringComparison.OrdinalIgnoreCase) ? 0x04u : 0u;

  private static List<EncodedBlock> EncodeDataBlocks(
      byte[] data,
      int blockSize,
      string method,
      FormatCreateOptions options) {
    var blocks = new List<EncodedBlock>();
    for (var offset = 0; offset < data.Length; offset += blockSize) {
      var length = Math.Min(blockSize, data.Length - offset);
      var encoded = Encode(data.AsSpan(offset, length), method, options);
      blocks.Add(new EncodedBlock(checked((uint)length), encoded.Data, encoded.Flags));
    }
    return blocks;
  }

  private static byte[] BuildBlocksInfo(
      IReadOnlyList<EncodedBlock> blocks,
      IReadOnlyList<NodeRecord> nodes) {
    using var ms = new MemoryStream();
    // The first 16 bytes are the UnityFS BlocksInfo hash/reserved field. Readers
    // do not require it for addressing or integrity, and zero is accepted by
    // established tooling; keeping it zero also avoids pretending this legacy
    // field is a cryptographic integrity mechanism.
    ms.Write(new byte[16]);
    WriteInt32BE(ms, blocks.Count);
    foreach (var block in blocks) {
      WriteUInt32BE(ms, block.UncompressedSize);
      WriteUInt32BE(ms, checked((uint)block.Data.Length));
      WriteUInt16BE(ms, block.Flags);
    }

    WriteInt32BE(ms, nodes.Count);
    foreach (var node in nodes) {
      WriteInt64BE(ms, node.Offset);
      WriteInt64BE(ms, node.Size);
      WriteUInt32BE(ms, node.Flags);
      WriteCString(ms, node.Path);
    }
    return ms.ToArray();
  }

  private static EncodedBlock Encode(
      ReadOnlySpan<byte> data,
      string method,
      FormatCreateOptions options) {
    return method switch {
      "stored" => new EncodedBlock(checked((uint)data.Length), data.ToArray(), 0),
      "lzma" => new EncodedBlock(checked((uint)data.Length), EncodeLzma(data, options), 1),
      "lz4" => new EncodedBlock(checked((uint)data.Length),
        Lz4BlockCompressor.Compress(data, Lz4CompressionLevel.Fast), 2),
      "lz4hc" => new EncodedBlock(checked((uint)data.Length),
        Lz4BlockCompressor.Compress(data, ResolveLz4HcLevel(options)), 3),
      "auto" => EncodeAuto(data, options),
      _ => throw new NotSupportedException($"UnityFS compression method '{method}' is not supported."),
    };
  }

  private static EncodedBlock EncodeAuto(ReadOnlySpan<byte> data, FormatCreateOptions options) {
    var compressed = Lz4BlockCompressor.Compress(data, ResolveLz4HcLevel(options));
    return compressed.Length < data.Length
      ? new EncodedBlock(checked((uint)data.Length), compressed, 3)
      : new EncodedBlock(checked((uint)data.Length), data.ToArray(), 0);
  }

  private static byte[] EncodeLzma(ReadOnlySpan<byte> data, FormatCreateOptions options) {
    var dictionary = options.DictSize <= 0 ? 1 << 23 : options.DictSize;
    if (dictionary < 4096 || dictionary > int.MaxValue)
      throw new ArgumentOutOfRangeException(nameof(options),
        "UnityFS LZMA dictionary size must fit Int32 and be at least 4096 bytes.");

    var encoder = new LzmaEncoder(checked((int)dictionary), level: ResolveLzmaLevel(options));
    using var body = new MemoryStream();
    encoder.Encode(body, data, writeEndMarker: false);
    var compressed = body.ToArray();
    var result = new byte[encoder.Properties.Length + compressed.Length];
    encoder.Properties.CopyTo(result, 0);
    compressed.CopyTo(result, encoder.Properties.Length);
    return result;
  }

  private static Lz4CompressionLevel ResolveLz4HcLevel(FormatCreateOptions options)
    => options.Optimize || options.Level is >= 9 ? Lz4CompressionLevel.Max : Lz4CompressionLevel.Hc;

  private static LzmaCompressionLevel ResolveLzmaLevel(FormatCreateOptions options) {
    if (options.Level is int level) {
      if (level <= 2)
        return LzmaCompressionLevel.Fast;
      if (level >= 8)
        return LzmaCompressionLevel.Best;
    }
    return options.Optimize ? LzmaCompressionLevel.Best : LzmaCompressionLevel.Normal;
  }

  private static string NormalizeMethod(string? method, string fallback) {
    var value = string.IsNullOrWhiteSpace(method) ? fallback : method.Trim().ToLowerInvariant();
    return value switch {
      "none" or "store" or "stored" => "stored",
      "lzma" => "lzma",
      "lz4" => "lz4",
      "lz4hc" or "lz4-hc" => "lz4hc",
      "auto" => "auto",
      _ => throw new NotSupportedException($"UnityFS compression method '{method}' is not supported."),
    };
  }

  private static string ValidateCString(string value, string optionName) {
    if (value.IndexOf('\0') >= 0)
      throw new ArgumentException($"UnityFS {optionName} may not contain a NUL character.", optionName);
    return value;
  }

  private static void Align16(Stream stream) {
    while ((stream.Position & 0xF) != 0)
      stream.WriteByte(0);
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
}
