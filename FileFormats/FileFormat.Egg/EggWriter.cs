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
}

/// <summary>
/// Creates EGG 1.0 archives using the published tagged-record layout.
/// The writer deliberately limits itself to the two methods that the native reader
/// can also decode: Store and raw DEFLATE. AZO, Bzip2, LZMA, encryption, solid mode,
/// and split volumes are not advertised or synthesized.
/// </summary>
public sealed class EggWriter : IDisposable {
  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private readonly uint _headerId;
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
  public EggWriter(Stream stream, bool leaveOpen = false, uint headerId = 1) {
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
      writer.Write(EggReader.EndMarker); // end of file sub-headers

      if (!entry.IsDirectory)
        WriteBlock(writer, entry.Data, entry.Method, entry.Level);
    }

    writer.Write(EggReader.EndMarker); // end of archive
    writer.Flush();
  }

  /// <inheritdoc />
  /// <summary>
  /// Releases resources held by this instance.
  /// </summary>
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

  private static void WriteBlock(
      BinaryWriter writer,
      byte[] plain,
      EggCompressionMethod requested,
      DeflateCompressionLevel level) {
    var actual = requested;
    byte[] packed;

    if (requested == EggCompressionMethod.Store) {
      packed = plain;
    } else {
      var deflated = DeflateCompressor.Compress(plain, level);
      if (requested == EggCompressionMethod.Deflate || deflated.Length < plain.Length) {
        actual = EggCompressionMethod.Deflate;
        packed = deflated;
      } else {
        actual = EggCompressionMethod.Store;
        packed = plain;
      }
    }

    var crc = new Crc32();
    crc.Update(plain);

    writer.Write(EggReader.BlockHeaderMagic);
    writer.Write((byte)actual);
    writer.Write((byte)0); // compression hint is reserved/unused
    writer.Write(checked((uint)plain.Length));
    writer.Write(checked((uint)packed.Length));
    writer.Write(crc.Value);
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
    if (method is not EggCompressionMethod.Auto and not EggCompressionMethod.Store and not EggCompressionMethod.Deflate)
      throw new ArgumentOutOfRangeException(nameof(method), method, "Native EGG creation supports Auto, Store, and Deflate.");
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
