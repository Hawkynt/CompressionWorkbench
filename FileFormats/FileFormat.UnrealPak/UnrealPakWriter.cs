#pragma warning disable CS1591
#pragma warning disable CA5350 // Unreal Pak v3 mandates SHA-1 in entry and index records.
using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Compression.Registry;

namespace FileFormat.UnrealPak;

/// <summary>
/// Deterministic writer for the interoperable Unreal Pak v3 legacy index.
/// It emits stored or independently zlib-compressed data blocks, absolute
/// compression-block offsets, per-entry SHA-1, and footer index SHA-1.
/// </summary>
internal static class UnrealPakWriter {
  private const uint Version = 3;
  private const int BaseCompressedRecordSize = 57;
  private const int Sha1Length = 20;
  private const int DefaultCompressionBlockSize = 64 * 1024;
  private const int MinimumCompressionBlockSize = 4 * 1024;
  private const int MaximumCompressionBlockSize = 16 * 1024 * 1024;

  private sealed record PreparedBlock(byte[] Bytes, long Start, long End);

  private sealed record PreparedEntry(
    string Path,
    byte[] Source,
    long RecordOffset,
    uint CompressionMethod,
    byte[] Hash,
    IReadOnlyList<PreparedBlock> Blocks,
    uint CompressionBlockSize) {
    public long StoredSize => this.CompressionMethod == UnrealPakReader.CompressionNone
      ? this.Source.LongLength
      : this.Blocks.Sum(block => (long)block.Bytes.Length);
  }

  public static void Write(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    ArgumentNullException.ThrowIfNull(options);
    if (!output.CanWrite || !output.CanSeek)
      throw new ArgumentException("Unreal Pak creation requires a writable, seekable stream.", nameof(output));
    if (!string.IsNullOrEmpty(options.Password) || options.EncryptFilenames || !string.IsNullOrEmpty(options.EncryptionMethod))
      throw new NotSupportedException("The Pak v3 writer does not implement AES data or index encryption.");

    var method = NormalizeMethod(options.MethodName);
    var blockSize = options.GetOptionInt("CompressionBlockSize", DefaultCompressionBlockSize);
    if (blockSize is < MinimumCompressionBlockSize or > MaximumCompressionBlockSize)
      throw new ArgumentOutOfRangeException(nameof(options),
        $"Pak compression block size must be between {MinimumCompressionBlockSize} and {MaximumCompressionBlockSize} bytes.");
    var mountPoint = NormalizeMountPoint(options.GetOption("MountPoint", string.Empty));

    var files = new List<(string Path, byte[] Data)>();
    var paths = new HashSet<string>(StringComparer.Ordinal);
    foreach (var input in inputs) {
      if (input.IsDirectory)
        continue;
      var path = NormalizePath(input.ArchiveName);
      if (!paths.Add(path))
        throw new ArgumentException($"Unreal Pak already contains an entry named '{path}'.", nameof(inputs));
      files.Add((path, input.ReadContent()));
    }
    files.Sort((left, right) => StringComparer.Ordinal.Compare(left.Path, right.Path));

    output.Position = 0;
    output.SetLength(0);

    var prepared = new List<PreparedEntry>(files.Count);
    foreach (var (path, data) in files) {
      var requested = method;
      if (requested == "auto" && !options.ForceCompress
          && options.IncompressiblePaths?.Contains(path) == true)
        requested = "stored";

      var entry = requested switch {
        "stored" => PrepareStored(path, data, output.Position),
        "zlib" => PrepareZlib(path, data, output.Position, blockSize, options, force: true),
        "auto" => PrepareZlib(path, data, output.Position, blockSize, options, force: false),
        _ => throw new InvalidOperationException("Unreachable Pak compression method."),
      };

      WriteDataRecord(output, entry);
      prepared.Add(entry);
    }

    var indexOffset = output.Position;
    byte[] indexBytes;
    using (var index = new MemoryStream()) {
      WriteFString(index, mountPoint);
      WriteInt32(index, prepared.Count);
      foreach (var entry in prepared) {
        WriteFString(index, entry.Path);
        WriteIndexRecord(index, entry);
      }
      indexBytes = index.ToArray();
    }

    output.Write(indexBytes);
    var indexHash = SHA1.HashData(indexBytes);

    WriteUInt32(output, UnrealPakReader.Magic);
    WriteUInt32(output, Version);
    WriteInt64(output, indexOffset);
    WriteInt64(output, indexBytes.LongLength);
    output.Write(indexHash);
    output.SetLength(output.Position);
  }

  private static PreparedEntry PrepareStored(string path, byte[] data, long recordOffset) {
    var hash = SHA1.HashData(data);
    return new PreparedEntry(
      path, data, recordOffset, UnrealPakReader.CompressionNone, hash, [], 0);
  }

  private static PreparedEntry PrepareZlib(
      string path,
      byte[] data,
      long recordOffset,
      int blockSize,
      FormatCreateOptions options,
      bool force) {
    if (data.Length == 0)
      return PrepareStored(path, data, recordOffset);

    var compressionLevel = SelectCompressionLevel(options);
    var compressed = new List<byte[]>((data.Length + blockSize - 1) / blockSize);
    for (var offset = 0; offset < data.Length; offset += blockSize) {
      var count = Math.Min(blockSize, data.Length - offset);
      compressed.Add(CompressZlib(data.AsSpan(offset, count), compressionLevel));
    }

    var compressedPayloadSize = compressed.Sum(block => (long)block.Length);
    if (!force) {
      // Compression adds a 4-byte block count plus 16 bytes per block to BOTH copies of
      // FPakEntry: one before the data and one in the index. Auto therefore compares the
      // complete on-disk cost, not merely zlib payload length.
      var extraMetadata = checked(2L * (4L + compressed.Count * 16L));
      if (compressedPayloadSize + extraMetadata >= data.LongLength)
        return PrepareStored(path, data, recordOffset);
    }

    var localHeaderSize = checked(BaseCompressedRecordSize + compressed.Count * 16);
    var cursor = checked(recordOffset + localHeaderSize);
    var blocks = new List<PreparedBlock>(compressed.Count);
    using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
    foreach (var bytes in compressed) {
      var start = cursor;
      cursor = checked(cursor + bytes.LongLength);
      blocks.Add(new PreparedBlock(bytes, start, cursor));
      hasher.AppendData(bytes);
    }

    return new PreparedEntry(
      path,
      data,
      recordOffset,
      UnrealPakReader.CompressionZlib,
      hasher.GetHashAndReset(),
      blocks,
      checked((uint)blockSize));
  }

  private static void WriteDataRecord(Stream output, PreparedEntry entry) {
    if (output.Position != entry.RecordOffset)
      throw new InvalidDataException("Pak writer data-record offset drifted from the prepared layout.");

    WriteEntryRecord(output, entry, serializedOffset: 0);
    if (entry.CompressionMethod == UnrealPakReader.CompressionNone) {
      output.Write(entry.Source);
      return;
    }

    foreach (var block in entry.Blocks) {
      if (output.Position != block.Start)
        throw new InvalidDataException("Pak writer compression-block offset drifted from the prepared layout.");
      output.Write(block.Bytes);
    }
  }

  private static void WriteIndexRecord(Stream output, PreparedEntry entry)
    => WriteEntryRecord(output, entry, entry.RecordOffset);

  private static void WriteEntryRecord(Stream output, PreparedEntry entry, long serializedOffset) {
    WriteInt64(output, serializedOffset);
    WriteInt64(output, entry.StoredSize);
    WriteInt64(output, entry.Source.LongLength);
    WriteUInt32(output, entry.CompressionMethod);
    if (entry.Hash.Length != Sha1Length)
      throw new InvalidDataException("Pak entry SHA-1 must be exactly 20 bytes.");
    output.Write(entry.Hash);

    if (entry.CompressionMethod != UnrealPakReader.CompressionNone) {
      WriteInt32(output, entry.Blocks.Count);
      foreach (var block in entry.Blocks) {
        // v3 stores absolute block positions. PakFile_Version_RelativeChunkOffsets is v5.
        WriteInt64(output, block.Start);
        WriteInt64(output, block.End);
      }
    }

    output.WriteByte(0); // Flags: neither encrypted nor deleted.
    WriteUInt32(output, entry.CompressionBlockSize);
  }

  private static byte[] CompressZlib(ReadOnlySpan<byte> source, CompressionLevel level) {
    using var output = new MemoryStream();
    using (var zlib = new ZLibStream(output, level, leaveOpen: true))
      zlib.Write(source);
    return output.ToArray();
  }

  private static CompressionLevel SelectCompressionLevel(FormatCreateOptions options) {
    if (options.Optimize || options.Level >= 8)
      return CompressionLevel.SmallestSize;
    if (options.Level <= 2)
      return CompressionLevel.Fastest;
    return CompressionLevel.Optimal;
  }

  private static string NormalizeMethod(string? methodName) {
    var method = string.IsNullOrWhiteSpace(methodName) ? "auto" : methodName.Trim().ToLowerInvariant();
    return method switch {
      "auto" => "auto",
      "store" or "stored" or "none" => "stored",
      "zlib" or "deflate" => "zlib",
      _ => throw new NotSupportedException(
        $"Pak v3 writer supports only Auto, Stored, and Zlib; got '{methodName}'."),
    };
  }

  private static string NormalizeMountPoint(string mountPoint) {
    ArgumentNullException.ThrowIfNull(mountPoint);
    var normalized = mountPoint.Replace('\\', '/');
    if (normalized.IndexOf('\0') >= 0)
      throw new ArgumentException("Pak mount point may not contain NUL characters.", nameof(mountPoint));
    return normalized;
  }

  private static string NormalizePath(string path) {
    ArgumentNullException.ThrowIfNull(path);
    var normalized = path.Replace('\\', '/').TrimStart('/');
    if (normalized.Length == 0 || normalized.EndsWith('/'))
      throw new ArgumentException("Pak entry path must name a file.", nameof(path));
    foreach (var part in normalized.Split('/')) {
      if (part.Length == 0 || part is "." or ".." || part.IndexOf('\0') >= 0)
        throw new ArgumentException("Unsafe Pak entry path.", nameof(path));
    }
    return normalized;
  }

  private static void WriteFString(Stream output, string value) {
    ArgumentNullException.ThrowIfNull(value);
    if (value.IndexOf('\0') >= 0)
      throw new ArgumentException("Pak FString values may not contain embedded NUL characters.", nameof(value));

    if (value.All(ch => ch <= 0x7F)) {
      var bytes = Encoding.UTF8.GetBytes(value);
      WriteInt32(output, checked(bytes.Length + 1));
      output.Write(bytes);
      output.WriteByte(0);
      return;
    }

    var utf16 = Encoding.Unicode.GetBytes(value);
    WriteInt32(output, checked(-(value.Length + 1)));
    output.Write(utf16);
    output.WriteByte(0);
    output.WriteByte(0);
  }

  private static void WriteUInt32(Stream output, uint value) {
    Span<byte> bytes = stackalloc byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
    output.Write(bytes);
  }

  private static void WriteInt32(Stream output, int value) {
    Span<byte> bytes = stackalloc byte[4];
    BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
    output.Write(bytes);
  }

  private static void WriteInt64(Stream output, long value) {
    Span<byte> bytes = stackalloc byte[8];
    BinaryPrimitives.WriteInt64LittleEndian(bytes, value);
    output.Write(bytes);
  }
}
