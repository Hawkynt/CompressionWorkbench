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
  private readonly ZipCompatibilityProfile _compatibilityProfile;
  private readonly List<ZipEntry> _entries = [];
  private bool _finished;
  private bool _finishAttempted;
  private bool _disposed;

  /// <summary>Gets or sets the archive comment.</summary>
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
  /// <param name="compatibilityProfile">Maximum ZIP feature level the output may require.</param>
  public ZipWriter(Stream stream, bool leaveOpen = false,
    DeflateCompressionLevel compressionLevel = DeflateCompressionLevel.Default,
    string? password = null,
    ZipEncryptionMethod encryptionMethod = ZipEncryptionMethod.Aes256,
    ZipCompatibilityProfile compatibilityProfile = ZipCompatibilityProfile.Zip63) {
    this._stream = stream ?? throw new ArgumentNullException(nameof(stream));
    this._leaveOpen = leaveOpen;
    this._compressionLevel = compressionLevel;
    this._password = password;
    this._encryptionMethod = password != null ? encryptionMethod : ZipEncryptionMethod.None;
    this._compatibilityProfile = compatibilityProfile;
  }

  /// <summary>Adds a file entry from a byte array.</summary>
  public void AddEntry(string fileName, byte[] data, ZipCompressionMethod method = ZipCompressionMethod.Deflate, DateTime? lastModified = null) {
    if (this._finished)
      throw new InvalidOperationException("Cannot add entries after Finish() has been called.");

    var crc = Crc32.Compute(data);
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
        var factor = (int)method - 1;
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

    byte[]? aesExtraField = null;
    var storedMethod = method;
    var encrypted = this._password != null && this._encryptionMethod != ZipEncryptionMethod.None;

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
      WrappedCompressionMethod = storedMethod == ZipCompressionMethod.WinZipAes ? method : null,
      Crc32 = crc,
      CompressedSize = compressedData.Length,
      UncompressedSize = data.Length,
      LastModified = lastModified ?? new DateTime(1980, 1, 1),
      LocalHeaderOffset = this._stream.Position,
      ExtraField = aesExtraField,
      IsEncrypted = encrypted,
      GeneralPurposeFlags = (ushort)(method == ZipCompressionMethod.Implode ? 0x0006 : 0),
    };

    this.EnsureCompatible(entry, method);

    var writer = new BinaryWriter(this._stream, System.Text.Encoding.UTF8, leaveOpen: true);
    ZipLocalFileHeader.Write(writer, entry, encrypted);
    this._stream.Write(compressedData);
    this._entries.Add(entry);
  }

  /// <summary>
  /// Adds a pre-compressed entry. The data is already compressed and will not be re-compressed.
  /// </summary>
  public void AddRawEntry(string fileName, byte[] compressedData, ZipCompressionMethod method,
      uint crc32, long uncompressedSize, DateTime? lastModified = null) {
    if (this._finished)
      throw new InvalidOperationException("Cannot add entries after Finish() has been called.");

    byte[]? aesExtraField = null;
    var storedMethod = method;
    var encrypted = this._password != null && this._encryptionMethod != ZipEncryptionMethod.None;

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
      WrappedCompressionMethod = storedMethod == ZipCompressionMethod.WinZipAes ? method : null,
      Crc32 = crc32,
      CompressedSize = compressedData.Length,
      UncompressedSize = uncompressedSize,
      LastModified = lastModified ?? new DateTime(1980, 1, 1),
      LocalHeaderOffset = this._stream.Position,
      ExtraField = aesExtraField,
      IsEncrypted = encrypted,
    };

    this.EnsureCompatible(entry, method);

    var writer = new BinaryWriter(this._stream, System.Text.Encoding.UTF8, leaveOpen: true);
    ZipLocalFileHeader.Write(writer, entry, encrypted);
    this._stream.Write(compressedData);
    this._entries.Add(entry);
  }

  /// <summary>
  /// Adds a STORE entry whose payload is streamed in bounded chunks rather than buffered in RAM.
  /// </summary>
  public void AddStreamingStoredEntry(string fileName, long size, Stream data, DateTime? lastModified = null) {
    if (this._finished)
      throw new InvalidOperationException("Cannot add entries after Finish() has been called.");
    ArgumentNullException.ThrowIfNull(data);
    if (size < 0)
      throw new ArgumentOutOfRangeException(nameof(size));
    if (this._password != null && this._encryptionMethod != ZipEncryptionMethod.None)
      throw new NotSupportedException("Streaming STORE does not support encryption; use the buffered AddEntry.");
    if (!this._stream.CanSeek)
      throw new NotSupportedException("Streaming STORE requires a seekable output stream to patch the CRC.");

    var entry = new ZipEntry {
      FileName = fileName,
      CompressionMethod = ZipCompressionMethod.Store,
      Crc32 = 0,
      CompressedSize = size,
      UncompressedSize = size,
      LastModified = lastModified ?? new DateTime(1980, 1, 1),
      LocalHeaderOffset = this._stream.Position,
      IsEncrypted = false,
    };

    this.EnsureCompatible(entry, ZipCompressionMethod.Store);

    var headerOffset = this._stream.Position;
    var writer = new BinaryWriter(this._stream, System.Text.Encoding.UTF8, leaveOpen: true);
    ZipLocalFileHeader.Write(writer, entry, encrypted: false);
    writer.Flush();

    var crc = new Crc32();
    if (size > 0) {
      var buffer = new byte[64 * 1024];
      var remaining = size;
      while (remaining > 0) {
        var toRead = (int)Math.Min(buffer.Length, remaining);
        var read = data.Read(buffer, 0, toRead);
        if (read <= 0)
          throw new EndOfStreamException(
            $"ZIP streaming entry '{fileName}': source ended {remaining} bytes short of the declared size {size}.");
        crc.Update(buffer.AsSpan(0, read));
        this._stream.Write(buffer, 0, read);
        remaining -= read;
      }
    }
    var endOffset = this._stream.Position;
    entry.Crc32 = crc.Value;

    this._stream.Position = headerOffset + 14;
    Span<byte> crcBytes = stackalloc byte[4];
    System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(crcBytes, entry.Crc32);
    this._stream.Write(crcBytes);
    this._stream.Position = endOffset;

    this._entries.Add(entry);
  }

  /// <summary>Adds a directory entry.</summary>
  public void AddDirectory(string name, DateTime? lastModified = null) {
    if (!name.EndsWith('/'))
      name += '/';
    AddEntry(name, [], ZipCompressionMethod.Store, lastModified);
  }

  /// <summary>Writes the central directory and finishes the archive.</summary>
  public void Finish() {
    if (this._finished || this._finishAttempted)
      return;

    // One-shot: a Finish that threw leaves a half-written central directory, and
    // disposal must not run it a second time and let the same failure escape
    // Dispose, where a caller has no way to react to it.
    this._finishAttempted = true;

    var writer = new BinaryWriter(this._stream, System.Text.Encoding.UTF8, leaveOpen: true);
    var cdOffset = this._stream.Position;
    var countNeedsZip64 = this._entries.Count > ushort.MaxValue;
    var offsetNeedsZip64 = cdOffset > uint.MaxValue;
    if (countNeedsZip64 || offsetNeedsZip64)
      ZipCompatibility.EnsureSupported(this._compatibilityProfile, ZipCompressionMethod.Store, zip64: true);

    foreach (var entry in this._entries)
      ZipCentralDirectoryEntry.Write(writer, entry);
    var cdSize = this._stream.Position - cdOffset;

    if (cdSize > uint.MaxValue)
      ZipCompatibility.EnsureSupported(this._compatibilityProfile, ZipCompressionMethod.Store, zip64: true);

    ZipEndOfCentralDirectory.Write(writer, cdOffset, cdSize, this._entries.Count, Comment);
    writer.Flush();
    this._finished = true;
  }

  /// <summary>Creates a ZIP archive split into multiple volumes.</summary>
  public static byte[][] CreateSplit(long maxVolumeSize,
      IEnumerable<(string Name, byte[] Data)> entries,
      ZipCompressionMethod method = ZipCompressionMethod.Deflate,
      string? password = null,
      ZipCompatibilityProfile compatibilityProfile = ZipCompatibilityProfile.Zip63) {
    using var ms = new MemoryStream();
    using (var writer = new ZipWriter(ms, leaveOpen: true, password: password, compatibilityProfile: compatibilityProfile)) {
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

  private void EnsureCompatible(ZipEntry entry, ZipCompressionMethod actualMethod) {
    var encryption = entry.IsEncrypted ? this._encryptionMethod : ZipEncryptionMethod.None;
    ZipCompatibility.EnsureSupported(
      this._compatibilityProfile,
      actualMethod,
      encryption,
      entry.IsZip64,
      entry.IsDirectory);
  }
}
