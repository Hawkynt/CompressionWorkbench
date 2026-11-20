using System.Text;
using Compression.Core.Checksums;
using Compression.Core.Deflate;

namespace FileFormat.Egg;

/// <summary>Compression methods that the native EGG writer can emit.</summary>
public enum EggCompressionMethod {
  /// <summary>Try raw DEFLATE and keep it only when it beats Store.</summary>
  Auto = -1,
  /// <summary>Store payload bytes verbatim.</summary>
  Store = 0,
  /// <summary>Compress with raw RFC 1951 DEFLATE.</summary>
  Deflate = 1,
  /// <summary>Compress with bzip2 (a complete .bz2 stream), as ALZip's "-m4" does.</summary>
  Bzip2 = 2,
  /// <summary>Compress with LZMA behind EGG's 9-byte header, as ALZip's "-m3" does.</summary>
  Lzma = 4,
}

/// <summary>Member encryption the EGG writer can apply.</summary>
public enum EggEncryptionMethod {
  /// <summary>ZIP 2.0 ("standard") encryption.</summary>
  Zip20 = 0,
  /// <summary>AES-128, WinZip-style key derivation, CTR and HMAC-SHA1.</summary>
  Aes128 = 1,
  /// <summary>AES-256, WinZip-style key derivation, CTR and HMAC-SHA1.</summary>
  Aes256 = 2,
  /// <summary>LEA-128 in CTR mode with the same key derivation.</summary>
  Lea128 = 5,
  /// <summary>LEA-256 in CTR mode with the same key derivation.</summary>
  Lea256 = 6,
}

/// <summary>
/// Creates EGG 1.0 archives using the published tagged-record layout.
/// Writes stored, deflate, bzip2 and LZMA blocks, optionally encrypted (ZIP 2.0, AES or LEA);
/// split sets come from <see cref="EggVolumes.Split"/>. AZO and solid mode are not written.
/// </summary>
public sealed class EggWriter : IDisposable {
  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private readonly uint _headerId;
  private readonly string? _password;
  private readonly EggEncryptionMethod _encryption;
  private readonly List<PendingEntry> _entries = [];
  private readonly HashSet<string> _filePaths = new(StringComparer.Ordinal);
  private readonly HashSet<string> _directoryPaths = new(StringComparer.Ordinal);
  private readonly HashSet<string> _explicitDirectories = new(StringComparer.Ordinal);
  private bool _finished;
  private bool _disposed;

  /// <summary>Creates a writer over a writable stream.</summary>
  /// <param name="stream">Destination stream.</param>
  /// <param name="leaveOpen">Whether disposing the writer leaves the destination open.</param>
  /// <param name="headerId">Non-zero archive/volume identifier. A deterministic value is useful for reproducible builds.</param>
  /// <param name="password">Encrypts every member's data when set.</param>
  /// <param name="encryption">The cipher used with <paramref name="password"/>.</param>
  public EggWriter(Stream stream, bool leaveOpen = false, uint headerId = 1, string? password = null,
      EggEncryptionMethod encryption = EggEncryptionMethod.Aes256) {
    this._password = string.IsNullOrEmpty(password) ? null : password;
    this._encryption = encryption;
    this._stream = stream ?? throw new ArgumentNullException(nameof(stream));
    if (!stream.CanWrite)
      throw new ArgumentException("Stream must be writable.", nameof(stream));
    if (headerId == 0)
      throw new ArgumentOutOfRangeException(nameof(headerId), "EGG header IDs must be non-zero.");
    this._leaveOpen = leaveOpen;
    this._headerId = headerId;
  }

  /// <summary>Adds a regular file.</summary>
  public void AddEntry(
      string path,
      byte[] data,
      EggCompressionMethod method = EggCompressionMethod.Auto,
      DeflateCompressionLevel level = DeflateCompressionLevel.Default,
      DateTime? lastModifiedUtc = null) {
    EnsureNotFinished();
    ArgumentNullException.ThrowIfNull(data);
    ValidateMethod(method);
    var normalized = NormalizePath(path, isDirectory: false);
    RegisterFilePath(normalized);
    this._entries.Add(new PendingEntry(normalized, data, false, method, level, lastModifiedUtc));
  }

  /// <summary>Adds an explicit directory entry. Parent directories are inferred for conflict checking.</summary>
  public void AddDirectory(string path, DateTime? lastModifiedUtc = null) {
    EnsureNotFinished();
    var normalized = NormalizePath(path, isDirectory: true);
    if (this._filePaths.Contains(normalized))
      throw new ArgumentException($"EGG path '{normalized}' is already used by a file.", nameof(path));
    if (this._explicitDirectories.Contains(normalized))
      throw new ArgumentException($"EGG already contains directory '{normalized}'.", nameof(path));

    var prefixes = DirectoryPrefixes(normalized).ToArray();
    foreach (var prefix in prefixes)
      if (this._filePaths.Contains(prefix))
        throw new ArgumentException($"EGG path '{prefix}' is already used by a file.", nameof(path));

    this._explicitDirectories.Add(normalized);
    foreach (var prefix in prefixes)
      this._directoryPaths.Add(prefix);
    this._entries.Add(new PendingEntry(normalized, [], true, EggCompressionMethod.Store, DeflateCompressionLevel.None, lastModifiedUtc));
  }

  /// <summary>Writes the archive and its final end marker. Calling this more than once is harmless.</summary>
  public void Finish() {
    if (this._finished)
      return;
    this._finished = true;

    using var writer = new BinaryWriter(this._stream, Encoding.UTF8, leaveOpen: true);
    writer.Write(EggReader.EggMagic);
    writer.Write((ushort)0x0100);
    writer.Write(this._headerId);
    writer.Write(0u);
    writer.Write(EggReader.EndMarker); // end of archive-level prefix fields

    uint fileId = 0;
    foreach (var entry in this._entries
      .OrderBy(entry => entry.Path, StringComparer.Ordinal)
      .ThenBy(entry => entry.IsDirectory ? 0 : 1)) {
      WriteFileHeader(writer, fileId++, entry.Data.LongLength);
      WriteFilename(writer, entry.Path);
      if (entry.IsDirectory || entry.LastModifiedUtc.HasValue)
        WriteWindowsInfo(writer, entry.LastModifiedUtc, entry.IsDirectory);

      // ALZip writes no block for a directory or an empty file, and readers (EggDotNet among them)
      // only look for blocks when the file header announces a non-zero size.
      var hasBlock = !entry.IsDirectory && entry.Data.Length > 0;
      var (method, packed) = hasBlock ? Pack(entry.Data, entry.Method, entry.Level) : (EggCompressionMethod.Store, []);
      var crc = Crc32.Compute(entry.Data);
      if (hasBlock && this._password != null) {
        var (field, cipher) = EggEncryption.Encrypt((byte)this._encryption, packed, this._password, crc);
        WriteExtraField(writer, EggReader.EncryptMagic, 0, field);
        packed = cipher;
      }
      writer.Write(EggReader.EndMarker); // end of file sub-headers
      if (hasBlock)
        WriteBlock(writer, entry.Data.Length, method, packed, crc);
    }

    writer.Write(EggReader.EndMarker); // end of archive
    writer.Flush();
  }

  /// <inheritdoc />
  public void Dispose() {
    if (this._disposed)
      return;
    this._disposed = true;
    if (!this._finished)
      Finish();
    if (!this._leaveOpen)
      this._stream.Dispose();
  }

  private static void WriteFileHeader(BinaryWriter writer, uint fileId, long length) {
    writer.Write(EggReader.FileHeaderMagic);
    writer.Write(fileId);
    writer.Write(length);
  }

  private static void WriteFilename(BinaryWriter writer, string path) {
    var bytes = Encoding.UTF8.GetBytes(path);
    WriteExtraField(writer, EggReader.FilenameMagic, 0, bytes);
  }

  private static void WriteWindowsInfo(BinaryWriter writer, DateTime? modifiedUtc, bool isDirectory) {
    Span<byte> data = stackalloc byte[9];
    var fileTime = 0L;
    if (modifiedUtc.HasValue) {
      try {
        var utc = modifiedUtc.Value.Kind == DateTimeKind.Utc
          ? modifiedUtc.Value
          : modifiedUtc.Value.ToUniversalTime();
        fileTime = utc.ToFileTimeUtc();
      } catch (ArgumentOutOfRangeException) {
        fileTime = 0;
      }
    }
    System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(data[..8], fileTime);
    data[8] = isDirectory ? (byte)0x80 : (byte)0;
    WriteExtraField(writer, EggReader.WindowsInfoMagic, 0, data);
  }

  /// <summary>Compresses one member; Auto keeps deflate only when it beats storing.</summary>
  private static (EggCompressionMethod Method, byte[] Packed) Pack(byte[] plain, EggCompressionMethod requested, DeflateCompressionLevel level) {
    switch (requested) {
      case EggCompressionMethod.Store:
        return (requested, plain);
      case EggCompressionMethod.Bzip2: {
        using var ms = new MemoryStream();
        using (var bz = new FileFormat.Bzip2.Bzip2Stream(ms, Compression.Core.Streams.CompressionStreamMode.Compress, leaveOpen: true))
          bz.Write(plain);
        return (requested, ms.ToArray());
      }
      case EggCompressionMethod.Lzma: {
        var encoder = new Compression.Core.Dictionary.Lzma.LzmaEncoder();
        using var ms = new MemoryStream();
        ms.Write([0x04, 0x41, 0x05, 0x00]); // version and property size, as ALZip writes them
        ms.Write(encoder.Properties);
        encoder.Encode(ms, plain, writeEndMarker: false);
        return (requested, ms.ToArray());
      }
    }
    var deflated = DeflateCompressor.Compress(plain, level);
    return requested == EggCompressionMethod.Deflate || deflated.Length < plain.Length
      ? (EggCompressionMethod.Deflate, deflated)
      : (EggCompressionMethod.Store, plain);
  }

  private static void WriteBlock(BinaryWriter writer, int plainLength, EggCompressionMethod method, byte[] packed, uint crc) {
    writer.Write(EggReader.BlockHeaderMagic);
    writer.Write((byte)method);
    writer.Write((byte)0); // compression hint is reserved/unused
    writer.Write(checked((uint)plainLength));
    writer.Write(checked((uint)packed.Length));
    writer.Write(crc);
    writer.Write(EggReader.EndMarker); // end of block sub-headers
    writer.Write(packed);
  }

  private static void WriteExtraField(BinaryWriter writer, uint signature, byte flags, ReadOnlySpan<byte> data) {
    writer.Write(signature);
    if (data.Length <= ushort.MaxValue) {
      writer.Write((byte)(flags & ~0x01));
      writer.Write(checked((ushort)data.Length));
    } else {
      writer.Write((byte)(flags | 0x01));
      writer.Write(checked((uint)data.Length));
    }
    writer.Write(data);
  }

  private void RegisterFilePath(string path) {
    if (this._directoryPaths.Contains(path))
      throw new ArgumentException($"EGG path '{path}' is already used by a directory.", nameof(path));
    if (this._filePaths.Contains(path))
      throw new ArgumentException($"EGG already contains file '{path}'.", nameof(path));

    var prefixes = DirectoryPrefixes(path, excludeLeaf: true).ToArray();
    foreach (var prefix in prefixes)
      if (this._filePaths.Contains(prefix))
        throw new ArgumentException($"EGG path '{prefix}' is already used by a file.", nameof(path));

    this._filePaths.Add(path);
    foreach (var prefix in prefixes)
      this._directoryPaths.Add(prefix);
  }

  private static IEnumerable<string> DirectoryPrefixes(string path, bool excludeLeaf = false) {
    var segments = path.Split('/');
    var count = excludeLeaf ? segments.Length - 1 : segments.Length;
    var prefix = string.Empty;
    for (var i = 0; i < count; ++i) {
      prefix = prefix.Length == 0 ? segments[i] : prefix + "/" + segments[i];
      yield return prefix;
    }
  }

  private static string NormalizePath(string path, bool isDirectory) {
    ArgumentException.ThrowIfNullOrWhiteSpace(path);
    var normalized = path.Replace('\\', '/').TrimStart('/');
    if (isDirectory)
      normalized = normalized.TrimEnd('/');
    if (normalized.Length == 0 || (!isDirectory && normalized.EndsWith('/')))
      throw new ArgumentException("EGG path must name a valid archive entry.", nameof(path));
    foreach (var component in normalized.Split('/'))
      if (component is "" or "." or ".." || component.IndexOf('\0') >= 0)
        throw new ArgumentException($"EGG path '{path}' contains an unsafe component.", nameof(path));
    return normalized;
  }

  private static void ValidateMethod(EggCompressionMethod method) {
    if (method is not (EggCompressionMethod.Auto or EggCompressionMethod.Store or EggCompressionMethod.Deflate
        or EggCompressionMethod.Bzip2 or EggCompressionMethod.Lzma))
      throw new ArgumentOutOfRangeException(nameof(method), method, "Native EGG creation supports Auto, Store, Deflate, Bzip2 and LZMA.");
  }

  private void EnsureNotFinished() {
    if (this._finished)
      throw new InvalidOperationException("Cannot add entries after Finish() has been called.");
  }

  private sealed record PendingEntry(
    string Path,
    byte[] Data,
    bool IsDirectory,
    EggCompressionMethod Method,
    DeflateCompressionLevel Level,
    DateTime? LastModifiedUtc);
}
