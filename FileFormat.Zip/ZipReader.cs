using Compression.Core.Checksums;
using Compression.Core.Deflate;
using Compression.Core.Dictionary.Zip;

namespace FileFormat.Zip;

/// <summary>
/// Reads entries from a ZIP archive.
/// </summary>
public sealed class ZipReader : IDisposable {
  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private readonly string? _password;
  private readonly List<ZipEntry> _entries;
  private bool _disposed;

  /// <summary>
  /// Gets the entries in the ZIP archive.
  /// </summary>
  public IReadOnlyList<ZipEntry> Entries => this._entries;

  /// <summary>
  /// Gets the archive comment.
  /// </summary>
  public string? Comment { get; }

  /// <summary>
  /// Initializes a new <see cref="ZipReader"/> from a stream.
  /// </summary>
  /// <param name="stream">A seekable stream containing the ZIP archive.</param>
  /// <param name="leaveOpen">Whether to leave the stream open on dispose.</param>
  /// <param name="password">Optional password for encrypted entries.</param>
  public ZipReader(Stream stream, bool leaveOpen = false, string? password = null) {
    this._stream = stream ?? throw new ArgumentNullException(nameof(stream));
    if (!stream.CanSeek)
      throw new ArgumentException("Stream must be seekable.", nameof(stream));
    this._leaveOpen = leaveOpen;
    this._password = password;
    this._entries = [];

    // Read the central directory
    var (cdOffset, cdSize, cdCount, comment) = ZipEndOfCentralDirectory.Read(this._stream);
    Comment = comment;

    this._stream.Position = cdOffset;
    var reader = new BinaryReader(this._stream, System.Text.Encoding.Latin1, leaveOpen: true);

    for (int i = 0; i < cdCount; ++i)
      this._entries.Add(ZipCentralDirectoryEntry.Read(reader));
  }

  /// <summary>
  /// Extracts the data for an entry.
  /// </summary>
  /// <param name="entry">The entry to extract.</param>
  /// <returns>The decompressed data.</returns>
  public byte[] ExtractEntry(ZipEntry entry) {
    this._stream.Position = entry.LocalHeaderOffset;
    var reader = new BinaryReader(this._stream, System.Text.Encoding.Latin1, leaveOpen: true);

    // Read local header (to skip past it to the data)
    var localEntry = ZipLocalFileHeader.Read(reader);

    // Read compressed data
    byte[] compressedData = new byte[entry.CompressedSize];
    var totalRead = 0;
    while (totalRead < compressedData.Length) {
      int read = this._stream.Read(compressedData, totalRead, compressedData.Length - totalRead);
      if (read == 0) throw new EndOfStreamException("Unexpected end of ZIP data.");
      totalRead += read;
    }

    // Handle decryption
    ZipCompressionMethod effectiveMethod = entry.CompressionMethod;

    if (entry.CompressionMethod == ZipCompressionMethod.WinZipAes) {
      if (this._password == null)
        throw new InvalidOperationException($"Entry '{entry.FileName}' is AES-encrypted but no password was provided.");
      effectiveMethod = ZipAesEncryption.ParseExtraField(entry.ExtraField);
      compressedData = ZipAesEncryption.Decrypt(compressedData, this._password);
    }
    else if (entry.IsEncrypted && compressedData.Length > 0) {
      if (this._password == null)
        throw new InvalidOperationException($"Entry '{entry.FileName}' is encrypted but no password was provided.");
      compressedData = ZipTraditionalEncryption.Decrypt(compressedData, this._password, entry.Crc32);
    }

    // Decompress
    byte[] data;
    switch (effectiveMethod) {
      case ZipCompressionMethod.Store:
        data = compressedData;
        break;
      case ZipCompressionMethod.Shrink:
        data = ShrinkDecoder.Decode(compressedData, (int)entry.UncompressedSize);
        break;
      case ZipCompressionMethod.Reduce1:
      case ZipCompressionMethod.Reduce2:
      case ZipCompressionMethod.Reduce3:
      case ZipCompressionMethod.Reduce4: {
        int factor = (int)effectiveMethod - 1; // methods 2-5 → factors 1-4
        data = ReduceDecoder.Decode(compressedData, (int)entry.UncompressedSize, factor);
        break;
      }
      case ZipCompressionMethod.Implode: {
        bool is8k = (entry.GeneralPurposeFlags & 0x0002) != 0;
        bool hasLitTree = (entry.GeneralPurposeFlags & 0x0004) != 0;
        data = ImplodeDecoder.Decode(compressedData, (int)entry.UncompressedSize, hasLitTree, is8k);
        break;
      }
      case ZipCompressionMethod.Deflate:
        data = DeflateDecompressor.Decompress(compressedData);
        break;
      case ZipCompressionMethod.Deflate64:
        data = Deflate64Decompressor.Decompress(compressedData);
        break;
      case ZipCompressionMethod.BZip2:
        data = ZipBzip2Helper.Decompress(compressedData);
        break;
      case ZipCompressionMethod.Lzma:
        data = ZipLzmaHelper.Decompress(compressedData);
        break;
      case ZipCompressionMethod.Zstd:
        data = ZipZstdHelper.Decompress(compressedData);
        break;
      case ZipCompressionMethod.Ppmd:
        data = ZipPpmdHelper.Decompress(compressedData, (int)entry.UncompressedSize);
        break;
      default:
        throw new NotSupportedException($"Unsupported compression method: {effectiveMethod}");
    }

    // Verify CRC
    uint crc = Crc32.Compute(data);
    if (crc != entry.Crc32)
      throw new InvalidDataException($"CRC-32 mismatch for '{entry.FileName}': expected 0x{entry.Crc32:X8}, computed 0x{crc:X8}.");

    return data;
  }

  /// <summary>
  /// Extracts the raw compressed bytes for an entry without decompressing.
  /// Returns the method, CRC-32, uncompressed size, and raw bitstream.
  /// Useful for restreaming between formats sharing the same codec (e.g., ZIP Deflate → Gzip).
  /// </summary>
  /// <param name="entry">The entry to extract.</param>
  /// <returns>The compression method, CRC-32, uncompressed size, and raw compressed bytes.</returns>
  public (ZipCompressionMethod Method, uint Crc32, long UncompressedSize, byte[] CompressedData) ExtractEntryRaw(ZipEntry entry) {
    this._stream.Position = entry.LocalHeaderOffset;
    var reader = new BinaryReader(this._stream, System.Text.Encoding.Latin1, leaveOpen: true);
    _ = ZipLocalFileHeader.Read(reader);

    byte[] compressedData = new byte[entry.CompressedSize];
    var totalRead = 0;
    while (totalRead < compressedData.Length) {
      int read = this._stream.Read(compressedData, totalRead, compressedData.Length - totalRead);
      if (read == 0) throw new EndOfStreamException("Unexpected end of ZIP data.");
      totalRead += read;
    }

    // Handle decryption — still needed for raw access
    ZipCompressionMethod effectiveMethod = entry.CompressionMethod;
    if (entry.CompressionMethod == ZipCompressionMethod.WinZipAes) {
      if (this._password == null)
        throw new InvalidOperationException($"Entry '{entry.FileName}' is AES-encrypted but no password was provided.");
      effectiveMethod = ZipAesEncryption.ParseExtraField(entry.ExtraField);
      compressedData = ZipAesEncryption.Decrypt(compressedData, this._password);
    }
    else if (entry.IsEncrypted && compressedData.Length > 0) {
      if (this._password == null)
        throw new InvalidOperationException($"Entry '{entry.FileName}' is encrypted but no password was provided.");
      compressedData = ZipTraditionalEncryption.Decrypt(compressedData, this._password, entry.Crc32);
    }

    return (effectiveMethod, entry.Crc32, entry.UncompressedSize, compressedData);
  }

  /// <summary>
  /// Opens a stream to read the decompressed data for an entry.
  /// </summary>
  /// <param name="entry">The entry to open.</param>
  /// <returns>A stream containing the decompressed data.</returns>
  public Stream OpenEntry(ZipEntry entry) {
    byte[] data = ExtractEntry(entry);
    return new MemoryStream(data, writable: false);
  }

  /// <inheritdoc />
  public void Dispose() {
    if (!this._disposed) {
      this._disposed = true;
      if (!this._leaveOpen)
        this._stream.Dispose();
    }
  }
}
