using Compression.Core.Checksums;
using Compression.Core.Deflate;
using Compression.Core.Dictionary.Zip;

namespace FileFormat.Zip;

/// <summary>
/// Creates a ZIP archive.
/// </summary>
public sealed class ZipWriter : IDisposable {
  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private readonly DeflateCompressionLevel _compressionLevel;
  private readonly string? _password;
  private readonly ZipEncryptionMethod _encryptionMethod;
  private readonly List<ZipEntry> _entries = [];
  private bool _finished;
  private bool _disposed;

  /// <summary>
  /// Gets or sets the archive comment.
  /// </summary>
  public string? Comment { get; set; }

  /// <summary>LZMA dictionary size in bytes (4096 to 1GB). Used when method is LZMA.</summary>
  public int LzmaDictionarySize { get; set; } = 1 << 23;

  /// <summary>LZMA compression level. Used when method is LZMA.</summary>
  public Compression.Core.Dictionary.Lzma.LzmaCompressionLevel LzmaLevel { get; set; }
    = Compression.Core.Dictionary.Lzma.LzmaCompressionLevel.Normal;

  /// <summary>PPMd model order (2-16). Used when method is PPMd.</summary>
  public int PpmdOrder { get; set; } = 6;

  /// <summary>PPMd memory size in megabytes (1-256). Used when method is PPMd.</summary>
  public int PpmdMemorySizeMB { get; set; } = 8;

  /// <summary>BZip2 block size multiplier 1-9 (N × 100 KB). Used when method is BZip2.</summary>
  public int Bzip2BlockSize { get; set; } = 9;

  /// <summary>
  /// Initializes a new <see cref="ZipWriter"/>.
  /// </summary>
  /// <param name="stream">The stream to write the ZIP archive to.</param>
  /// <param name="leaveOpen">Whether to leave the stream open on dispose.</param>
  /// <param name="compressionLevel">The Deflate compression level to use.</param>
  /// <param name="password">Optional password for encryption.</param>
  /// <param name="encryptionMethod">The encryption method to use when a password is set.</param>
  public ZipWriter(Stream stream, bool leaveOpen = false,
    DeflateCompressionLevel compressionLevel = DeflateCompressionLevel.Default,
    string? password = null,
    ZipEncryptionMethod encryptionMethod = ZipEncryptionMethod.Aes256) {
    this._stream = stream ?? throw new ArgumentNullException(nameof(stream));
    this._leaveOpen = leaveOpen;
    this._compressionLevel = compressionLevel;
    this._password = password;
    this._encryptionMethod = password != null ? encryptionMethod : ZipEncryptionMethod.None;
  }

  /// <summary>
  /// Adds a file entry from a byte array.
  /// </summary>
  /// <param name="fileName">The file name in the archive.</param>
  /// <param name="data">The file data.</param>
  /// <param name="method">The compression method.</param>
  /// <param name="lastModified">The last modification time.</param>
  public void AddEntry(string fileName, byte[] data, ZipCompressionMethod method = ZipCompressionMethod.Deflate, DateTime? lastModified = null) {
    if (this._finished)
      throw new InvalidOperationException("Cannot add entries after Finish() has been called.");

    // Compute CRC
    uint crc = Crc32.Compute(data);

    // Compress if needed
    byte[] compressedData;
    switch (method) {
      case ZipCompressionMethod.Store:
        compressedData = data;
        break;
      case ZipCompressionMethod.Shrink:
        compressedData = ShrinkEncoder.Encode(data);
        if (compressedData.Length >= data.Length) {
          compressedData = data;
          method = ZipCompressionMethod.Store;
        }
        break;
      case ZipCompressionMethod.Reduce1:
      case ZipCompressionMethod.Reduce2:
      case ZipCompressionMethod.Reduce3:
      case ZipCompressionMethod.Reduce4: {
        int factor = (int)method - 1;
        compressedData = ReduceEncoder.Encode(data, factor);
        if (compressedData.Length >= data.Length) {
          compressedData = data;
          method = ZipCompressionMethod.Store;
        }
        break;
      }
      case ZipCompressionMethod.Implode:
        compressedData = ImplodeEncoder.Encode(data, useLiteralTree: true, use8kDictionary: true);
        if (compressedData.Length >= data.Length) {
          compressedData = data;
          method = ZipCompressionMethod.Store;
        }
        break;
      case ZipCompressionMethod.Deflate:
        compressedData = DeflateCompressor.Compress(data, this._compressionLevel);
        if (compressedData.Length >= data.Length) {
          compressedData = data;
          method = ZipCompressionMethod.Store;
        }
        break;
      case ZipCompressionMethod.Deflate64:
        compressedData = Deflate64Compressor.Compress(data, this._compressionLevel);
        if (compressedData.Length >= data.Length) {
          compressedData = data;
          method = ZipCompressionMethod.Store;
        }
        break;
      case ZipCompressionMethod.BZip2:
        compressedData = ZipBzip2Helper.Compress(data, this.Bzip2BlockSize);
        if (compressedData.Length >= data.Length) {
          compressedData = data;
          method = ZipCompressionMethod.Store;
        }
        break;
      case ZipCompressionMethod.Lzma:
        compressedData = ZipLzmaHelper.Compress(data, this.LzmaDictionarySize, this.LzmaLevel);
        if (compressedData.Length >= data.Length) {
          compressedData = data;
          method = ZipCompressionMethod.Store;
        }
        break;
      case ZipCompressionMethod.Ppmd:
        compressedData = ZipPpmdHelper.Compress(data, this.PpmdOrder, this.PpmdMemorySizeMB);
        if (compressedData.Length >= data.Length) {
          compressedData = data;
          method = ZipCompressionMethod.Store;
        }
        break;
      case ZipCompressionMethod.Zstd:
        compressedData = ZipZstdHelper.Compress(data);
        if (compressedData.Length >= data.Length) {
          compressedData = data;
          method = ZipCompressionMethod.Store;
        }
        break;
      default:
        throw new NotSupportedException($"Unsupported compression method for writing: {method}");
    }

    // Apply encryption if password is set
    byte[]? aesExtraField = null;
    ZipCompressionMethod storedMethod = method;
    bool encrypted = this._password != null && this._encryptionMethod != ZipEncryptionMethod.None;

    if (encrypted) {
      switch (this._encryptionMethod) {
        case ZipEncryptionMethod.Aes256:
          compressedData = ZipAesEncryption.Encrypt(compressedData, this._password!);
          aesExtraField = ZipAesEncryption.BuildExtraField(method);
          storedMethod = ZipCompressionMethod.WinZipAes;
          break;
        case ZipEncryptionMethod.PkzipTraditional:
          compressedData = ZipTraditionalEncryption.Encrypt(compressedData, this._password!, crc);
          break;
      }
    }

    var entry = new ZipEntry {
      FileName = fileName,
      CompressionMethod = storedMethod,
      Crc32 = crc,
      CompressedSize = compressedData.Length,
      UncompressedSize = data.Length,
      LastModified = lastModified ?? new DateTime(1980, 1, 1),
      LocalHeaderOffset = this._stream.Position,
      ExtraField = aesExtraField,
      IsEncrypted = encrypted,
      // Set Implode flags: bit 1 = 8K dictionary, bit 2 = literal tree
      GeneralPurposeFlags = (ushort)(method == ZipCompressionMethod.Implode ? 0x0006 : 0),
    };

    // Write local header
    var writer = new BinaryWriter(this._stream, System.Text.Encoding.UTF8, leaveOpen: true);
    ZipLocalFileHeader.Write(writer, entry, encrypted);

    // Write data
    this._stream.Write(compressedData);

    this._entries.Add(entry);
  }

  /// <summary>
  /// Adds a pre-compressed entry. The data is already compressed and will not be
  /// re-compressed. Useful for restreaming between formats (e.g., Gzip → ZIP) or
  /// for injecting optimally-compressed data.
  /// </summary>
  /// <param name="fileName">The file name in the archive.</param>
  /// <param name="compressedData">The pre-compressed data.</param>
  /// <param name="method">The compression method that was used.</param>
  /// <param name="crc32">CRC-32 of the original uncompressed data.</param>
  /// <param name="uncompressedSize">Size of the original uncompressed data.</param>
  /// <param name="lastModified">The last modification time.</param>
  public void AddRawEntry(string fileName, byte[] compressedData, ZipCompressionMethod method,
      uint crc32, long uncompressedSize, DateTime? lastModified = null) {
    if (this._finished)
      throw new InvalidOperationException("Cannot add entries after Finish() has been called.");

    byte[]? aesExtraField = null;
    ZipCompressionMethod storedMethod = method;
    bool encrypted = this._password != null && this._encryptionMethod != ZipEncryptionMethod.None;

    if (encrypted) {
      switch (this._encryptionMethod) {
        case ZipEncryptionMethod.Aes256:
          compressedData = ZipAesEncryption.Encrypt(compressedData, this._password!);
          aesExtraField = ZipAesEncryption.BuildExtraField(method);
          storedMethod = ZipCompressionMethod.WinZipAes;
          break;
        case ZipEncryptionMethod.PkzipTraditional:
          compressedData = ZipTraditionalEncryption.Encrypt(compressedData, this._password!, crc32);
          break;
      }
    }

    var entry = new ZipEntry {
      FileName = fileName,
      CompressionMethod = storedMethod,
      Crc32 = crc32,
      CompressedSize = compressedData.Length,
      UncompressedSize = uncompressedSize,
      LastModified = lastModified ?? new DateTime(1980, 1, 1),
      LocalHeaderOffset = this._stream.Position,
      ExtraField = aesExtraField,
      IsEncrypted = encrypted,
    };

    var writer = new BinaryWriter(this._stream, System.Text.Encoding.UTF8, leaveOpen: true);
    ZipLocalFileHeader.Write(writer, entry, encrypted);
    this._stream.Write(compressedData);
    this._entries.Add(entry);
  }

  /// <summary>
  /// Adds a directory entry.
  /// </summary>
  /// <param name="name">The directory name (should end with '/').</param>
  /// <param name="lastModified">The last modification time.</param>
  public void AddDirectory(string name, DateTime? lastModified = null) {
    if (!name.EndsWith('/'))
      name += '/';

    AddEntry(name, [], ZipCompressionMethod.Store, lastModified);
  }

  /// <summary>
  /// Writes the central directory and finishes the archive.
  /// </summary>
  public void Finish() {
    if (this._finished)
      return;

    this._finished = true;

    var writer = new BinaryWriter(this._stream, System.Text.Encoding.UTF8, leaveOpen: true);

    // Write central directory
    var cdOffset = this._stream.Position;
    foreach (var entry in this._entries)
      ZipCentralDirectoryEntry.Write(writer, entry);
    var cdSize = this._stream.Position - cdOffset;

    // Write end of central directory
    ZipEndOfCentralDirectory.Write(writer, cdOffset, cdSize, this._entries.Count, Comment);

    writer.Flush();
  }

  /// <summary>
  /// Creates a ZIP archive split into multiple volumes.
  /// </summary>
  /// <param name="maxVolumeSize">Maximum size of each volume in bytes.</param>
  /// <param name="entries">The entries to add (name, data pairs).</param>
  /// <param name="method">The compression method.</param>
  /// <param name="password">Optional password for encryption.</param>
  /// <returns>An array of byte arrays, one per volume.</returns>
  public static byte[][] CreateSplit(long maxVolumeSize,
      IEnumerable<(string Name, byte[] Data)> entries,
      ZipCompressionMethod method = ZipCompressionMethod.Deflate,
      string? password = null) {
    using var ms = new MemoryStream();
    using (var writer = new ZipWriter(ms, leaveOpen: true, password: password)) {
      foreach (var (name, data) in entries)
        writer.AddEntry(name, data, method);
      writer.Finish();
    }

    return Compression.Core.Streams.VolumeHelper.SplitIntoVolumes(ms.ToArray(), maxVolumeSize);
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
}
