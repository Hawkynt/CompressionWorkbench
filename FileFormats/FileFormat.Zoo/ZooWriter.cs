using System.Buffers.Binary;
using System.Text;
using Compression.Core.BitIO;
using Compression.Core.Checksums;
using Compression.Core.Dictionary.Lzw;

namespace FileFormat.Zoo;

/// <summary>
/// Creates Zoo archives with an explicit historical compatibility target.
/// </summary>
public sealed class ZooWriter : IDisposable {
  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private readonly ZooCompressionMethod _defaultMethod;
  private readonly ZooCompatibilityProfile _compatibilityProfile;
  private readonly List<(ZooEntry Entry, byte[] CompressedData)> _pending = [];
  private bool _finished;
  private bool _disposed;

  /// <summary>
  /// Initializes a new <see cref="ZooWriter"/>.
  /// </summary>
  /// <param name="stream">The seekable stream to write to.</param>
  /// <param name="leaveOpen">Whether to leave the stream open on dispose.</param>
  /// <param name="defaultMethod">Default packing method.</param>
  /// <param name="compatibilityProfile">Oldest Zoo generation that must fully manipulate the archive.</param>
  public ZooWriter(
      Stream stream,
      bool leaveOpen = false,
      ZooCompressionMethod defaultMethod = ZooCompressionMethod.Lzw,
      ZooCompatibilityProfile compatibilityProfile = ZooCompatibilityProfile.Zoo200) {
    this._stream = stream ?? throw new ArgumentNullException(nameof(stream));
    if (!stream.CanSeek || !stream.CanWrite)
      throw new ArgumentException("Zoo writing requires a seekable writable stream.", nameof(stream));

    _ = ZooCompatibility.GetArchiveVersion(compatibilityProfile);
    _ = ZooCompatibility.GetExtractVersion(defaultMethod);

    this._leaveOpen = leaveOpen;
    this._defaultMethod = defaultMethod;
    this._compatibilityProfile = compatibilityProfile;
    this.WriteArchiveHeader();
  }

  /// <summary>Adds a file entry to the archive.</summary>
  public void AddEntry(
      string fileName,
      byte[] data,
      ZooCompressionMethod? method = null,
      DateTime? lastModified = null) {
    ArgumentNullException.ThrowIfNull(fileName);
    ArgumentNullException.ThrowIfNull(data);
    if (this._finished)
      throw new InvalidOperationException("Cannot add entries after Finish() has been called.");
    if (fileName.Length == 0)
      throw new ArgumentException("Zoo filename must not be empty.", nameof(fileName));

    var chosenMethod = method ?? this._defaultMethod;
    _ = ZooCompatibility.GetExtractVersion(chosenMethod);

    var compressed = Compress(data, chosenMethod);
    if (chosenMethod == ZooCompressionMethod.Lzw && compressed.Length >= data.Length) {
      compressed = data;
      chosenMethod = ZooCompressionMethod.Store;
    }

    var shortName = MakeShortName(fileName);
    if (shortName.Length == 0)
      throw new ArgumentException("Zoo filename must contain a non-empty final path component.", nameof(fileName));

    var needsLongName = NeedsLongName(fileName, shortName);
    if (needsLongName && !ZooCompatibility.SupportsLongNames(this._compatibilityProfile))
      throw new NotSupportedException(
        $"Zoo compatibility profile {this._compatibilityProfile} supports type-1 short names only; '{fileName}' requires a type-2 directory entry.");

    var (majorVersion, minorVersion) = ZooCompatibility.GetExtractVersion(chosenMethod);
    var entry = new ZooEntry {
      FileName = shortName,
      LongFileName = needsLongName ? fileName.Replace('\\', '/') : null,
      CompressionMethod = chosenMethod,
      Crc16 = Crc16.Compute(data),
      OriginalSize = (uint)data.Length,
      CompressedSize = (uint)compressed.Length,
      LastModified = lastModified ?? DateTime.UtcNow,
      MajorVersion = majorVersion,
      MinorVersion = minorVersion,
    };

    var headerOffset = this._stream.Position;
    var headerSize = ZooDirectoryCodec.GetHeaderSize(entry);
    var dataOffset = checked(headerOffset + headerSize + ZooConstants.FileLeader.Length);
    if ((ulong)headerOffset > uint.MaxValue || (ulong)dataOffset > uint.MaxValue)
      throw new NotSupportedException("Zoo uses 32-bit directory and data offsets; this archive has exceeded that limit.");

    entry.HeaderOffset = headerOffset;
    entry.DataOffset = dataOffset;
    entry.DirectorySize = headerSize;

    var header = ZooDirectoryCodec.BuildHeader(entry, nextOffset: 0, dataOffset: (uint)dataOffset);
    this._stream.Write(header);
    this._stream.Write(ZooConstants.FileLeader);
    this._stream.Write(compressed);
    this._pending.Add((entry, compressed));
  }

  /// <summary>Finalises linked directory offsets and flushes the archive.</summary>
  public void Finish() {
    if (this._finished)
      return;

    this._finished = true;
    var endPosition = this._stream.Position;

    if (this._pending.Count == 0) {
      this._stream.Position = 24;
      WriteUInt32LittleEndian(this._stream, 0);
      WriteUInt32LittleEndian(this._stream, 0);
      this._stream.Position = endPosition;
      this._stream.Flush();
      return;
    }

    for (var i = 0; i < this._pending.Count; ++i) {
      var entry = this._pending[i].Entry;
      var nextOffset = i + 1 < this._pending.Count ? this._pending[i + 1].Entry.HeaderOffset : 0;
      if ((ulong)nextOffset > uint.MaxValue)
        throw new NotSupportedException("Zoo uses 32-bit directory offsets; this archive has exceeded that limit.");

      var header = ZooDirectoryCodec.BuildHeader(entry, (uint)nextOffset, (uint)entry.DataOffset);
      if (header.Length != entry.DirectorySize)
        throw new InvalidOperationException("Zoo directory entry size changed while finalising the archive.");

      this._stream.Position = entry.HeaderOffset;
      this._stream.Write(header);
    }

    this._stream.Position = endPosition;
    this._stream.Flush();
  }

  /// <summary>Creates a Zoo archive split into multiple volumes.</summary>
  public static byte[][] CreateSplit(
      long maxVolumeSize,
      IEnumerable<(string Name, byte[] Data)> entries,
      ZooCompressionMethod method = ZooCompressionMethod.Lzw,
      ZooCompatibilityProfile compatibilityProfile = ZooCompatibilityProfile.Zoo200) {
    using var ms = new MemoryStream();
    using (var writer = new ZooWriter(
             ms,
             leaveOpen: true,
             defaultMethod: method,
             compatibilityProfile: compatibilityProfile)) {
      foreach (var (name, data) in entries)
        writer.AddEntry(name, data);
      writer.Finish();
    }

    return Compression.Core.Streams.VolumeHelper.SplitIntoVolumes(ms.ToArray(), maxVolumeSize);
  }

  /// <inheritdoc />
  public void Dispose() {
    if (this._disposed)
      return;

    this._disposed = true;
    if (!this._finished)
      this.Finish();
    if (!this._leaveOpen)
      this._stream.Dispose();
  }

  private void WriteArchiveHeader() {
    var modern = this._compatibilityProfile == ZooCompatibilityProfile.Zoo200;
    var headerSize = modern ? ZooConstants.ArchiveHeaderSize : ZooConstants.MinimumArchiveHeaderSize;
    var header = new byte[headerSize];
    var textBytes = Encoding.ASCII.GetBytes(ZooConstants.DefaultHeaderText);
    textBytes.AsSpan(0, Math.Min(textBytes.Length, 20)).CopyTo(header);

    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(20), ZooConstants.Magic);
    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(24), (uint)headerSize);
    BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(28), -headerSize);
    var (majorVersion, minorVersion) = ZooCompatibility.GetArchiveVersion(this._compatibilityProfile);
    header[32] = majorVersion;
    header[33] = minorVersion;

    if (modern) {
      header[34] = ZooConstants.ArchiveHeaderTypeExtended;
      BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(35), 0); // archive comment offset
      BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(39), 0); // archive comment length
      header[41] = 0; // version-generation data
    }

    this._stream.Write(header);
  }

  private static byte[] Compress(byte[] data, ZooCompressionMethod method) {
    if (method == ZooCompressionMethod.Store)
      return data;
    if (method != ZooCompressionMethod.Lzw)
      throw new NotSupportedException($"Unsupported Zoo packing method: {(byte)method}.");

    using var ms = new MemoryStream();
    var encoder = new LzwEncoder(
      ms,
      minBits: ZooConstants.LzwMinBits,
      maxBits: ZooConstants.LzwMaxBits,
      useClearCode: true,
      useStopCode: false,
      bitOrder: BitOrder.LsbFirst);
    encoder.Encode(data);
    return ms.ToArray();
  }

  private static bool NeedsLongName(string original, string shortName) =>
    !string.Equals(original, shortName, StringComparison.Ordinal) || original.Length > ZooConstants.MaxShortNameLength;

  private static string MakeShortName(string name) {
    var slash = name.LastIndexOfAny(['/', '\\']);
    if (slash >= 0)
      name = name[(slash + 1)..];
    if (name.Length > ZooConstants.MaxShortNameLength)
      name = name[..ZooConstants.MaxShortNameLength];
    return name;
  }

  private static void WriteUInt32LittleEndian(Stream stream, uint value) {
    Span<byte> bytes = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
    stream.Write(bytes);
  }
}
