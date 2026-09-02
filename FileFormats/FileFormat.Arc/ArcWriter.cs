using System.Text;
using Compression.Core.BitIO;
using Compression.Core.Checksums;
using Compression.Core.Dictionary.Lzw;

namespace FileFormat.Arc;

/// <summary>
/// Creates an ARC archive by writing entries sequentially to a stream.
/// </summary>
public sealed class ArcWriter : IDisposable {
  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private readonly ArcCompressionMethod _defaultMethod;
  private bool _finished;
  private bool _disposed;

  /// <summary>
  /// Initializes a new <see cref="ArcWriter"/>.
  /// </summary>
  /// <param name="stream">The stream to write the ARC archive to.</param>
  /// <param name="defaultMethod">The default compression method used when adding entries.</param>
  /// <param name="leaveOpen">Whether to leave the stream open when this writer is disposed.</param>
  public ArcWriter(Stream stream, ArcCompressionMethod defaultMethod = ArcCompressionMethod.Stored, bool leaveOpen = false) {
    this._stream = stream ?? throw new ArgumentNullException(nameof(stream));
    this._defaultMethod = defaultMethod;
    this._leaveOpen = leaveOpen;
  }

  /// <summary>
  /// Adds a file entry to the archive using the writer's default compression method.
  /// </summary>
  /// <param name="fileName">The filename to store in the archive (up to 12 characters).</param>
  /// <param name="data">The uncompressed data to store.</param>
  /// <param name="lastModified">The last-modified timestamp. Defaults to <see cref="DateTimeOffset.UtcNow"/>.</param>
  public void AddEntry(string fileName, ReadOnlySpan<byte> data, DateTimeOffset lastModified = default) {
    if (this._finished)
      throw new InvalidOperationException("Cannot add entries after Finish() has been called.");

    ArgumentNullException.ThrowIfNull(fileName);
    if (fileName.Length == 0)
      throw new ArgumentException("File name must not be empty.", nameof(fileName));

    // Truncate to 12 chars to fit the 13-byte null-terminated field.
    if (fileName.Length > 12)
      fileName = fileName[..12];

    if (lastModified == default)
      lastModified = DateTimeOffset.UtcNow;

    var method = (byte)this._defaultMethod;
    var uncompressed = data.ToArray();
    var compressed = Compress(method, uncompressed);

    // If compressed data is larger than original, fall back to stored.
    if (compressed.Length > uncompressed.Length && method != ArcConstants.MethodStored && method != ArcConstants.MethodStoredOld) {
      method = ArcConstants.MethodStored;
      compressed = uncompressed;
    }

    var crc = Crc16.Compute(uncompressed);

    var entry = new ArcEntry {
      FileName = fileName,
      Method = method,
      CompressedSize = (uint)compressed.Length,
      OriginalSize = (uint)uncompressed.Length,
      Crc16 = crc,
      LastModified = lastModified,
    };

    WriteEntryHeader(entry);
    this._stream.Write(compressed);
  }

  /// <summary>
  /// Adds a file entry to the archive using a specific compression method.
  /// </summary>
  /// <param name="fileName">The filename to store in the archive (up to 12 characters).</param>
  /// <param name="data">The uncompressed data to store.</param>
  /// <param name="method">The compression method to use for this entry.</param>
  /// <param name="lastModified">The last-modified timestamp. Defaults to <see cref="DateTimeOffset.UtcNow"/>.</param>
  public void AddEntry(string fileName, ReadOnlySpan<byte> data, ArcCompressionMethod method, DateTimeOffset lastModified = default) {
    if (this._finished)
      throw new InvalidOperationException("Cannot add entries after Finish() has been called.");

    ArgumentNullException.ThrowIfNull(fileName);
    if (fileName.Length == 0)
      throw new ArgumentException("File name must not be empty.", nameof(fileName));

    if (fileName.Length > 12)
      fileName = fileName[..12];

    if (lastModified == default)
      lastModified = DateTimeOffset.UtcNow;

    var methodByte = (byte)method;
    var uncompressed = data.ToArray();
    var compressed = Compress(methodByte, uncompressed);

    if (compressed.Length > uncompressed.Length && methodByte != ArcConstants.MethodStored && methodByte != ArcConstants.MethodStoredOld) {
      methodByte = ArcConstants.MethodStored;
      compressed = uncompressed;
    }

    var crc = Crc16.Compute(uncompressed);

    var entry = new ArcEntry {
      FileName = fileName,
      Method = methodByte,
      CompressedSize = (uint)compressed.Length,
      OriginalSize = (uint)uncompressed.Length,
      Crc16 = crc,
      LastModified = lastModified,
    };

    WriteEntryHeader(entry);
    this._stream.Write(compressed);
  }

  /// <summary>
  /// Writes the end-of-archive marker and flushes the stream.
  /// </summary>
  public void Finish() {
    if (this._finished)
      return;

    this._finished = true;
    // End-of-archive: magic byte followed by zero method.
    this._stream.WriteByte(ArcConstants.Magic);
    this._stream.WriteByte(ArcConstants.MethodEndOfArchive);
    this._stream.Flush();
  }

  /// <inheritdoc />
  public void Dispose() {
    if (!this._disposed) {
      this._disposed = true;
      if (!this._finished)
        Finish();
      if (!this._leaveOpen)
        this._stream.Dispose();
    }
  }

  private static byte[] Compress(byte method, byte[] data) =>
    method switch {
      ArcConstants.MethodStoredOld or ArcConstants.MethodStored => data,
      ArcConstants.MethodPacked => ArcRle.Encode(data),
      ArcConstants.MethodSqueezed => ArcSqueeze.Encode(data),
      ArcConstants.MethodCrunched5 => CompressCrunched5(data),
      ArcConstants.MethodCrunched6 => CompressLzw12(data, useClearCode: false),
      ArcConstants.MethodCrunched7 => CompressLzw12(data, useClearCode: true),
      ArcConstants.MethodCrunched8 => CompressLzw(data, useClearCode: true),
      ArcConstants.MethodSquashed => CompressLzw(data, useClearCode: false),
      _ => throw new NotSupportedException($"ARC compression method {method} is not supported for writing."),
    };

  private static byte[] CompressCrunched5(byte[] data) {
    // Method 5: RLE pre-pass then LZW 9-12 bit
    var rleEncoded = ArcRle.Encode(data);
    return CompressLzw12(rleEncoded, useClearCode: true);
  }

  private static byte[] CompressLzw12(byte[] data, bool useClearCode) {
    using var ms = new MemoryStream();
    var encoder = new LzwEncoder(
      ms,
      minBits: ArcConstants.LzwMinBits,
      maxBits: 12,
      useClearCode: useClearCode,
      useStopCode: false,
      bitOrder: BitOrder.LsbFirst);
    encoder.Encode(data);
    return ms.ToArray();
  }

  private static byte[] CompressLzw(byte[] data, bool useClearCode) {
    using var ms = new MemoryStream();
    var encoder = new LzwEncoder(
      ms,
      minBits: ArcConstants.LzwMinBits,
      maxBits: ArcConstants.LzwMaxBits,
      useClearCode: useClearCode,
      useStopCode: false,
      bitOrder: BitOrder.LsbFirst);
    encoder.Encode(data);
    return ms.ToArray();
  }

  private void WriteEntryHeader(ArcEntry entry) {
    // Every new-format entry header is 29 bytes: 2 magic+method + 27 remaining.
    // Old stored (method 1) would be 25 bytes, but we always write new format.
    var isNewFormat = entry.Method != ArcConstants.MethodStoredOld;
    var headerSize = isNewFormat ? ArcConstants.NewHeaderSize : ArcConstants.OldHeaderSize;
    var header = new byte[headerSize];

    header[0] = ArcConstants.Magic;
    header[1] = entry.Method;

    // Filename: 13 bytes, null-terminated, ASCII.
    var nameBytes = Encoding.ASCII.GetBytes(entry.FileName);
    var nameLen = Math.Min(nameBytes.Length, ArcConstants.FileNameLength - 1);
    nameBytes.AsSpan(0, nameLen).CopyTo(header.AsSpan(2));
    // Remaining bytes in the name field stay zero (null-terminated).

    // Compressed size at offset 15.
    WriteUInt32Le(header, 15, entry.CompressedSize);

    // Date at offset 19.
    WriteUInt16Le(header, 19, entry.DosDate);

    // Time at offset 21.
    WriteUInt16Le(header, 21, entry.DosTime);

    // CRC-16 at offset 23.
    WriteUInt16Le(header, 23, entry.Crc16);

    // Original size at offset 25 (new format only).
    if (isNewFormat)
      WriteUInt32Le(header, 25, entry.OriginalSize);

    this._stream.Write(header, 0, header.Length);
  }

  private static void WriteUInt16Le(byte[] buffer, int offset, ushort value) {
    buffer[offset] = (byte)(value & 0xFF);
    buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
  }

  /// <summary>
  /// Creates an ARC archive split into multiple volumes.
  /// </summary>
  /// <param name="maxVolumeSize">Maximum size of each volume in bytes.</param>
  /// <param name="entries">The entries to add (name, data pairs).</param>
  /// <param name="method">The compression method.</param>
  /// <returns>An array of byte arrays, one per volume.</returns>
  public static byte[][] CreateSplit(long maxVolumeSize,
      IEnumerable<(string Name, byte[] Data)> entries,
      ArcCompressionMethod method = ArcCompressionMethod.Stored) {
    using var ms = new MemoryStream();
    using (var writer = new ArcWriter(ms, method, leaveOpen: true)) {
      foreach (var (name, data) in entries)
        writer.AddEntry(name, data);
      writer.Finish();
    }

    return Compression.Core.Streams.VolumeHelper.SplitIntoVolumes(ms.ToArray(), maxVolumeSize);
  }

  private static void WriteUInt32Le(byte[] buffer, int offset, uint value) {
    buffer[offset] = (byte)(value & 0xFF);
    buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
    buffer[offset + 2] = (byte)((value >> 16) & 0xFF);
    buffer[offset + 3] = (byte)((value >> 24) & 0xFF);
  }
}
