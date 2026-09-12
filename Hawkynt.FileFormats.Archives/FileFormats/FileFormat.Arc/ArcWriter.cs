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
  private readonly ArcCompatibilityProfile _compatibilityProfile;
  private bool _finished;
  private bool _disposed;

  /// <summary>
  /// Initializes a new <see cref="ArcWriter"/>.
  /// </summary>
  /// <param name="stream">The stream to write the ARC archive to.</param>
  /// <param name="defaultMethod">The default compression method used when adding entries.</param>
  /// <param name="leaveOpen">Whether to leave the stream open when this writer is disposed.</param>
  /// <param name="compatibilityProfile">Historical ARC-family capability ceiling.</param>
  public ArcWriter(
      Stream stream,
      ArcCompressionMethod defaultMethod = ArcCompressionMethod.Stored,
      bool leaveOpen = false,
      ArcCompatibilityProfile compatibilityProfile = ArcCompatibilityProfile.Extended) {
    this._stream = stream ?? throw new ArgumentNullException(nameof(stream));
    this._defaultMethod = defaultMethod;
    this._leaveOpen = leaveOpen;
    this._compatibilityProfile = compatibilityProfile;
  }

  /// <summary>Adds a file using the writer's default compression method.</summary>
  public void AddEntry(string fileName, ReadOnlySpan<byte> data, DateTimeOffset lastModified = default)
    => this.AddEntryCore(fileName, data, this._defaultMethod, lastModified);

  /// <summary>Adds a file using a specific compression method.</summary>
  public void AddEntry(string fileName, ReadOnlySpan<byte> data, ArcCompressionMethod method, DateTimeOffset lastModified = default)
    => this.AddEntryCore(fileName, data, method, lastModified);

  /// <summary>Writes the end-of-archive marker and flushes the stream.</summary>
  public void Finish() {
    if (this._finished)
      return;

    this._finished = true;
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

  /// <summary>Creates an ARC archive split into multiple volumes.</summary>
  public static byte[][] CreateSplit(
      long maxVolumeSize,
      IEnumerable<(string Name, byte[] Data)> entries,
      ArcCompressionMethod method = ArcCompressionMethod.Stored,
      ArcCompatibilityProfile compatibilityProfile = ArcCompatibilityProfile.Extended) {
    using var ms = new MemoryStream();
    using (var writer = new ArcWriter(ms, method, leaveOpen: true, compatibilityProfile)) {
      foreach (var (name, data) in entries)
        writer.AddEntry(name, data);
      writer.Finish();
    }

    return Compression.Core.Streams.VolumeHelper.SplitIntoVolumes(ms.ToArray(), maxVolumeSize);
  }

  private void AddEntryCore(
      string fileName,
      ReadOnlySpan<byte> data,
      ArcCompressionMethod requestedMethod,
      DateTimeOffset lastModified) {
    if (this._finished)
      throw new InvalidOperationException("Cannot add entries after Finish() has been called.");

    ArgumentNullException.ThrowIfNull(fileName);
    if (fileName.Length == 0)
      throw new ArgumentException("File name must not be empty.", nameof(fileName));

    if (fileName.Length > 12)
      fileName = fileName[..12];

    if (lastModified == default)
      lastModified = DateTimeOffset.UtcNow;

    var uncompressed = data.ToArray();
    var method = requestedMethod;
    var compressed = Compress(method, uncompressed);

    // Compatibility follows the bytes actually emitted. A requested newer
    // method that loses to STORE therefore does not unnecessarily raise the
    // archive's reader requirement.
    if (compressed.Length > uncompressed.Length && method != ArcCompressionMethod.Stored) {
      method = ArcCompressionMethod.Stored;
      compressed = uncompressed;
    }

    ArcCompatibility.EnsureSupported(this._compatibilityProfile, method);

    var entry = new ArcEntry {
      FileName = fileName,
      Method = (byte)method,
      CompressedSize = (uint)compressed.Length,
      OriginalSize = (uint)uncompressed.Length,
      Crc16 = Crc16.Compute(uncompressed),
      LastModified = lastModified,
    };

    this.WriteEntryHeader(entry);
    this._stream.Write(compressed);
  }

  private static byte[] Compress(ArcCompressionMethod method, byte[] data) => method switch {
    ArcCompressionMethod.Stored => data,
    ArcCompressionMethod.Packed => ArcRle.Encode(data),
    ArcCompressionMethod.Squeezed => ArcSqueeze.Encode(data),
    ArcCompressionMethod.Crunched5 => CompressCrunched5(data),
    ArcCompressionMethod.Crunched6 => CompressLzw12(data, useClearCode: false),
    ArcCompressionMethod.Crunched7 => CompressLzw12(data, useClearCode: true),
    ArcCompressionMethod.Crunched => CompressLzw(data, useClearCode: true),
    ArcCompressionMethod.Squashed => CompressLzw(data, useClearCode: false),
    _ => throw new NotSupportedException($"ARC compression method {(byte)method} is not supported for writing."),
  };

  private static byte[] CompressCrunched5(byte[] data) {
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
    var isNewFormat = entry.Method != ArcConstants.MethodStoredOld;
    var headerSize = isNewFormat ? ArcConstants.NewHeaderSize : ArcConstants.OldHeaderSize;
    var header = new byte[headerSize];

    header[0] = ArcConstants.Magic;
    header[1] = entry.Method;

    var nameBytes = Encoding.ASCII.GetBytes(entry.FileName);
    var nameLen = Math.Min(nameBytes.Length, ArcConstants.FileNameLength - 1);
    nameBytes.AsSpan(0, nameLen).CopyTo(header.AsSpan(2));

    WriteUInt32Le(header, 15, entry.CompressedSize);
    WriteUInt16Le(header, 19, entry.DosDate);
    WriteUInt16Le(header, 21, entry.DosTime);
    WriteUInt16Le(header, 23, entry.Crc16);
    if (isNewFormat)
      WriteUInt32Le(header, 25, entry.OriginalSize);

    this._stream.Write(header, 0, header.Length);
  }

  private static void WriteUInt16Le(byte[] buffer, int offset, ushort value) {
    buffer[offset] = (byte)(value & 0xFF);
    buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
  }

  private static void WriteUInt32Le(byte[] buffer, int offset, uint value) {
    buffer[offset] = (byte)(value & 0xFF);
    buffer[offset + 1] = (byte)((value >> 8) & 0xFF);
    buffer[offset + 2] = (byte)((value >> 16) & 0xFF);
    buffer[offset + 3] = (byte)((value >> 24) & 0xFF);
  }
}
