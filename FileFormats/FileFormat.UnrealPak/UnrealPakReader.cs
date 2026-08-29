#pragma warning disable CS1591
#pragma warning disable CA5350 // Unreal Pak mandates SHA-1 for index and entry integrity fields.
using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace FileFormat.UnrealPak;

/// <summary>
/// Reads the legacy-index Unreal Engine <c>.pak</c> layout (versions 1-7).
/// Version 3 introduced compression blocks and entry flags; version 5 changed
/// compressed-block offsets from absolute file offsets to offsets relative to
/// the entry's record position. Version 8 and newer use a different compression
/// method/index generation and are deliberately rejected here rather than guessed at.
/// </summary>
public sealed class UnrealPakReader {
  public const uint Magic = 0x5A6F12E1;
  public const uint CompressionNone = 0;
  public const uint CompressionZlib = 1;
  public const byte FlagEncrypted = 0x01;
  public const byte FlagDeleted = 0x02;

  public sealed record CompressionBlock(long CompressedStart, long CompressedEnd) {
    public long CompressedSize => checked(this.CompressedEnd - this.CompressedStart);
  }

  // Keep the original public constructor/deconstruction shape source-compatible. Richer wire
  // metadata is additive through init properties instead of changing the positional record API.
  public sealed record UnrealPakEntry(
    string Path,
    long Offset,
    long Size,
    long UncompressedSize,
    uint CompressionMethod,
    bool IsEncrypted,
    string? UnsupportedReason) {
    public byte[] Hash { get; init; } = [];
    public IReadOnlyList<CompressionBlock> CompressionBlocks { get; init; } = [];
    public byte Flags { get; init; }
    public uint CompressionBlockSize { get; init; }
    public bool IsDeleted => (this.Flags & FlagDeleted) != 0;
  }

  private const int Sha1Length = 20;
  private const int MaxFileCount = 10_000_000;
  private const int MaxCompressionBlocks = 1_000_000;
  private const int IoBufferSize = 64 * 1024;

  private readonly Stream _stream;
  private readonly List<UnrealPakEntry> _entries = [];
  private readonly List<string> _compressionMethods = ["None", "Zlib"];
  private readonly long _indexOffset;

  public uint PakVersion { get; }
  public string MountPoint { get; }
  public IReadOnlyList<string> CompressionMethods => this._compressionMethods;
  public bool IsIndexEncrypted { get; }
  public long IndexOffset => this._indexOffset;
  public long IndexSize { get; }
  public byte[] IndexHash { get; }
  public bool IndexHashVerified { get; }
  public IReadOnlyList<UnrealPakEntry> Entries => this._entries;

  public UnrealPakReader(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    if (!stream.CanSeek || !stream.CanRead)
      throw new ArgumentException("Unreal Pak reading requires a readable, seekable stream.", nameof(stream));
    this._stream = stream;

    var length = stream.Length;
    if (length < 44)
      throw new InvalidDataException("Unreal Pak is shorter than the smallest footer.");

    // For v1-v7 every extension field is PREPENDED before the stable footer core, so Magic is
    // exactly 44 bytes from EOF. Do not scan blindly: the 20-byte footer hash itself may contain
    // the magic byte pattern and must never be mistaken for a second footer.
    var magicOffset = length - 44;
    stream.Position = magicOffset;
    var magic = ReadUInt32(stream);
    if (magic != Magic) {
      if (TryFindModernFooter(stream, length, out var modernVersion))
        throw new NotSupportedException(
          $"Unreal Pak version {modernVersion} uses the modern v8+ compression-name/directory/path-hash index generation. " +
          "It is intentionally outside the legacy Pak descriptor rather than being parsed heuristically.");
      throw new InvalidDataException("Unreal Pak legacy footer magic was not found 44 bytes from EOF.");
    }

    this.PakVersion = ReadUInt32(stream);
    if (this.PakVersion is < 1 or > 7)
      throw new NotSupportedException(
        $"Unreal Pak version {this.PakVersion} uses a modern index/compression-method generation. " +
        "This descriptor intentionally supports only the legacy v1-v7 index; v8+ must be handled by a dedicated modern Pak implementation.");

    this._indexOffset = ReadInt64(stream);
    this.IndexSize = ReadInt64(stream);
    this.IndexHash = ReadBytesExactly(stream, Sha1Length);

    if (this.PakVersion >= 4) {
      if (magicOffset == 0)
        throw new InvalidDataException("Unreal Pak v4+ footer is missing its encrypted-index flag.");
      stream.Position = magicOffset - 1;
      var encryptedIndex = stream.ReadByte();
      if (encryptedIndex < 0)
        throw new EndOfStreamException();
      this.IsIndexEncrypted = encryptedIndex != 0;
    }

    if (this._indexOffset < 0 || this.IndexSize <= 0
        || this._indexOffset > length - this.IndexSize
        || this._indexOffset + this.IndexSize > magicOffset)
      throw new InvalidDataException("Unreal Pak footer references an out-of-range index.");
    if (this.IndexSize > Array.MaxLength)
      throw new NotSupportedException("Unreal Pak index exceeds the managed-array limit.");

    stream.Position = this._indexOffset;
    var indexBytes = new byte[checked((int)this.IndexSize)];
    stream.ReadExactly(indexBytes);
    var actualIndexHash = SHA1.HashData(indexBytes);
    if (!CryptographicOperations.FixedTimeEquals(actualIndexHash, this.IndexHash))
      throw new InvalidDataException("Unreal Pak index SHA-1 mismatch.");
    this.IndexHashVerified = true;

    if (this.IsIndexEncrypted)
      throw new NotSupportedException("AES-encrypted Unreal Pak indexes are not supported by the legacy reader.");

    using var index = new MemoryStream(indexBytes, writable: false);
    try {
      this.MountPoint = ReadFString(index);
      var fileCount = ReadInt32(index);
      if (fileCount < 0 || fileCount > MaxFileCount)
        throw new InvalidDataException($"Unreal Pak file count ({fileCount}) is out of sane range.");

      for (var i = 0; i < fileCount; ++i) {
        var name = ReadFString(index);
        this._entries.Add(ReadEntryRecord(index, this.PakVersion, name, absoluteRecordOffset: null));
      }
    } catch (EndOfStreamException ex) {
      throw new InvalidDataException("Unreal Pak legacy index is truncated.", ex);
    }
  }

  public void VerifyEntry(UnrealPakEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    _ = this.ReadAndValidateLocalHeader(entry, verifyHash: true);
  }

  public byte[] Extract(UnrealPakEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.IsDeleted)
      throw new InvalidOperationException($"Cannot extract deleted Pak record '{entry.Path}'.");
    if (entry.UnsupportedReason != null)
      throw new NotSupportedException($"Cannot extract '{entry.Path}': {entry.UnsupportedReason}");
    if (entry.IsEncrypted)
      throw new NotSupportedException($"Cannot extract '{entry.Path}': entry is AES-encrypted.");
    if (entry.UncompressedSize > Array.MaxLength)
      throw new NotSupportedException($"Entry '{entry.Path}' exceeds the managed-array limit.");

    var headerEnd = this.ReadAndValidateLocalHeader(entry, verifyHash: true);
    if (entry.CompressionMethod == CompressionNone) {
      if (entry.Size != entry.UncompressedSize)
        throw new InvalidDataException($"Stored Pak entry '{entry.Path}' has differing stored/uncompressed sizes.");
      ValidateRange(headerEnd, entry.Size, this._indexOffset, entry.Path);
      return this.ReadRangeToArray(headerEnd, entry.Size);
    }

    if (entry.CompressionMethod != CompressionZlib)
      throw new NotSupportedException($"Pak compression method {entry.CompressionMethod} is not supported.");
    if (entry.CompressionBlockSize == 0)
      throw new InvalidDataException($"Compressed Pak entry '{entry.Path}' has a zero compression-block size.");

    var expectedBlockCount = entry.UncompressedSize == 0
      ? 0L
      : (entry.UncompressedSize + entry.CompressionBlockSize - 1L) / entry.CompressionBlockSize;
    if (expectedBlockCount != entry.CompressionBlocks.Count)
      throw new InvalidDataException(
        $"Pak entry '{entry.Path}' declares {entry.CompressionBlocks.Count} compression blocks; expected {expectedBlockCount}.");

    var totalCompressed = entry.CompressionBlocks.Sum(block => block.CompressedSize);
    if (totalCompressed != entry.Size)
      throw new InvalidDataException(
        $"Pak entry '{entry.Path}' block sizes sum to {totalCompressed}, but the entry declares {entry.Size} stored bytes.");

    var output = new byte[checked((int)entry.UncompressedSize)];
    var outputOffset = 0;
    foreach (var block in entry.CompressionBlocks) {
      ValidateRange(block.CompressedStart, block.CompressedSize, this._indexOffset, entry.Path);
      var expected = Math.Min((long)entry.CompressionBlockSize, entry.UncompressedSize - outputOffset);
      if (expected < 0 || expected > int.MaxValue)
        throw new InvalidDataException($"Pak entry '{entry.Path}' has an invalid decompressed block size.");
      var compressed = this.ReadRangeToArray(block.CompressedStart, block.CompressedSize);
      var decoded = DecompressZlibBlock(compressed, checked((int)expected), entry.Path);
      decoded.CopyTo(output, outputOffset);
      outputOffset += decoded.Length;
    }

    if (outputOffset != output.Length)
      throw new InvalidDataException($"Pak entry '{entry.Path}' decoded {outputOffset} bytes; expected {output.Length}.");
    return output;
  }

  private long ReadAndValidateLocalHeader(UnrealPakEntry indexed, bool verifyHash) {
    if (indexed.Offset < 0 || indexed.Offset >= this._indexOffset)
      throw new InvalidDataException($"Pak entry '{indexed.Path}' has an out-of-range record offset.");

    this._stream.Position = indexed.Offset;
    UnrealPakEntry local;
    try {
      local = ReadEntryRecord(this._stream, this.PakVersion, indexed.Path, indexed.Offset);
    } catch (EndOfStreamException ex) {
      throw new InvalidDataException($"Pak entry '{indexed.Path}' has a truncated local header.", ex);
    }
    var headerEnd = this._stream.Position;

    if (local.Offset != 0)
      throw new InvalidDataException($"Pak entry '{indexed.Path}' local header offset must be zero, got {local.Offset}.");
    if (local.Size != indexed.Size
        || local.UncompressedSize != indexed.UncompressedSize
        || local.CompressionMethod != indexed.CompressionMethod
        || local.Flags != indexed.Flags
        || local.CompressionBlockSize != indexed.CompressionBlockSize
        || !CryptographicOperations.FixedTimeEquals(local.Hash, indexed.Hash)
        || !local.CompressionBlocks.SequenceEqual(indexed.CompressionBlocks))
      throw new InvalidDataException($"Pak entry '{indexed.Path}' local header does not match its index record.");

    ValidateEntryRanges(indexed, headerEnd, this._indexOffset);
    if (verifyHash) {
      var actual = this.ComputeStoredPayloadHash(indexed, headerEnd);
      if (!CryptographicOperations.FixedTimeEquals(actual, indexed.Hash))
        throw new InvalidDataException($"Pak entry '{indexed.Path}' SHA-1 mismatch.");
    }
    return headerEnd;
  }

  private byte[] ComputeStoredPayloadHash(UnrealPakEntry entry, long headerEnd) {
    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
    if (entry.CompressionMethod == CompressionNone) {
      AppendRangeToHash(hash, headerEnd, entry.Size, this._stream);
    } else {
      foreach (var block in entry.CompressionBlocks)
        AppendRangeToHash(hash, block.CompressedStart, block.CompressedSize, this._stream);
    }
    return hash.GetHashAndReset();
  }

  private static void ValidateEntryRanges(UnrealPakEntry entry, long headerEnd, long indexOffset) {
    if (headerEnd > indexOffset)
      throw new InvalidDataException($"Pak entry '{entry.Path}' local header overlaps the index.");

    if (entry.CompressionMethod == CompressionNone) {
      ValidateRange(headerEnd, entry.Size, indexOffset, entry.Path);
      return;
    }

    long sum = 0;
    long previousEnd = headerEnd;
    foreach (var block in entry.CompressionBlocks) {
      if (block.CompressedStart < headerEnd || block.CompressedStart < previousEnd || block.CompressedEnd < block.CompressedStart)
        throw new InvalidDataException($"Pak entry '{entry.Path}' contains an invalid or overlapping compression-block range.");
      ValidateRange(block.CompressedStart, block.CompressedSize, indexOffset, entry.Path);
      sum = checked(sum + block.CompressedSize);
      previousEnd = block.CompressedEnd;
    }
    if (sum != entry.Size)
      throw new InvalidDataException($"Pak entry '{entry.Path}' compression blocks total {sum} bytes, expected {entry.Size}.");
  }

  private UnrealPakEntry ReadEntryRecord(Stream stream, uint version, string name, long? absoluteRecordOffset) {
    var serializedOffset = ReadInt64(stream);
    var size = ReadInt64(stream);
    var uncompressedSize = ReadInt64(stream);
    if (serializedOffset < 0 || size < 0 || uncompressedSize < 0)
      throw new InvalidDataException($"Pak entry '{name}' contains a negative offset or size.");

    var compressionMethod = ReadUInt32(stream);
    if (version <= 1)
      _ = ReadInt64(stream);

    var hash = ReadBytesExactly(stream, Sha1Length);
    var blocks = new List<CompressionBlock>();
    if (version >= 3 && compressionMethod != CompressionNone) {
      var blockCount = ReadInt32(stream);
      if (blockCount < 0 || blockCount > MaxCompressionBlocks)
        throw new InvalidDataException($"Pak entry '{name}' compression-block count {blockCount} is out of range.");

      var baseOffset = absoluteRecordOffset ?? serializedOffset;
      for (var i = 0; i < blockCount; ++i) {
        var start = ReadInt64(stream);
        var end = ReadInt64(stream);
        if (version >= 5) {
          start = checked(baseOffset + start);
          end = checked(baseOffset + end);
        }
        if (start < 0 || end < start)
          throw new InvalidDataException($"Pak entry '{name}' has an invalid compression-block range.");
        blocks.Add(new CompressionBlock(start, end));
      }
    }

    byte flags = 0;
    uint compressionBlockSize = 0;
    if (version >= 3) {
      var flagValue = stream.ReadByte();
      if (flagValue < 0)
        throw new EndOfStreamException();
      flags = checked((byte)flagValue);
      compressionBlockSize = ReadUInt32(stream);
    }

    string? unsupported = compressionMethod switch {
      CompressionNone => null,
      CompressionZlib => null,
      _ => $"unsupported legacy compression method 0x{compressionMethod:X8}",
    };
    var isEncrypted = (flags & FlagEncrypted) != 0;
    if (isEncrypted)
      unsupported ??= "entry is AES-encrypted";

    return new UnrealPakEntry(
      name, serializedOffset, size, uncompressedSize, compressionMethod, isEncrypted, unsupported) {
      Hash = hash,
      CompressionBlocks = blocks,
      Flags = flags,
      CompressionBlockSize = compressionBlockSize,
    };
  }

  private static bool TryFindModernFooter(Stream stream, long length, out uint version) {
    version = 0;
    var scanSize = checked((int)Math.Min(512L, length));
    var start = length - scanSize;
    stream.Position = start;
    var tail = new byte[scanSize];
    stream.ReadExactly(tail);

    for (var i = tail.Length - 44; i >= 0; --i) {
      if (BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(i, 4)) != Magic)
        continue;
      var candidateVersion = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(i + 4, 4));
      if (candidateVersion is < 8 or > 12)
        continue;
      var indexOffset = BinaryPrimitives.ReadInt64LittleEndian(tail.AsSpan(i + 8, 8));
      var indexSize = BinaryPrimitives.ReadInt64LittleEndian(tail.AsSpan(i + 16, 8));
      var absoluteMagic = start + i;
      if (indexOffset < 0 || indexSize <= 0 || indexOffset > length - indexSize || indexOffset + indexSize > absoluteMagic)
        continue;
      version = candidateVersion;
      return true;
    }
    return false;
  }

  private static byte[] DecompressZlibBlock(byte[] compressed, int expectedSize, string entryName) {
    using var input = new MemoryStream(compressed, writable: false);
    using var zlib = new ZLibStream(input, CompressionMode.Decompress, leaveOpen: false);
    var result = new byte[expectedSize];
    var offset = 0;
    while (offset < result.Length) {
      var read = zlib.Read(result.AsSpan(offset));
      if (read == 0)
        break;
      offset += read;
    }
    if (offset != result.Length || zlib.ReadByte() != -1)
      throw new InvalidDataException(
        $"Pak zlib block in '{entryName}' decoded to an unexpected size (expected {expectedSize}).");
    return result;
  }

  private byte[] ReadRangeToArray(long offset, long length) {
    if (length > Array.MaxLength)
      throw new NotSupportedException("Pak stored range exceeds the managed-array limit.");
    var result = new byte[checked((int)length)];
    this._stream.Position = offset;
    this._stream.ReadExactly(result);
    return result;
  }

  private static void AppendRangeToHash(IncrementalHash hash, long offset, long length, Stream stream) {
    stream.Position = offset;
    var buffer = new byte[IoBufferSize];
    var remaining = length;
    while (remaining > 0) {
      var count = checked((int)Math.Min(buffer.Length, remaining));
      stream.ReadExactly(buffer.AsSpan(0, count));
      hash.AppendData(buffer.AsSpan(0, count));
      remaining -= count;
    }
  }

  private static void ValidateRange(long offset, long length, long exclusiveEnd, string entryName) {
    if (offset < 0 || length < 0 || length > exclusiveEnd || offset > exclusiveEnd - length)
      throw new InvalidDataException($"Pak entry '{entryName}' references bytes outside the data region.");
  }

  private static string ReadFString(Stream stream) {
    var length = ReadInt32(stream);
    if (length == 0)
      return string.Empty;

    if (length > 0) {
      if (length > 1_048_576)
        throw new InvalidDataException($"Pak FString length {length} is out of range.");
      var bytes = ReadBytesExactly(stream, length);
      if (bytes[^1] != 0)
        throw new InvalidDataException("Pak ANSI FString is not null-terminated.");
      return Encoding.UTF8.GetString(bytes, 0, bytes.Length - 1);
    }

    if (length == int.MinValue)
      throw new InvalidDataException("Pak UTF-16 FString length is invalid.");
    var charCount = -length;
    if (charCount > 524_288)
      throw new InvalidDataException($"Pak UTF-16 FString length {charCount} is out of range.");
    var byteCount = checked(charCount * 2);
    var utf16 = ReadBytesExactly(stream, byteCount);
    if (byteCount < 2 || utf16[^2] != 0 || utf16[^1] != 0)
      throw new InvalidDataException("Pak UTF-16 FString is not null-terminated.");
    return Encoding.Unicode.GetString(utf16, 0, byteCount - 2);
  }

  private static byte[] ReadBytesExactly(Stream stream, int count) {
    var bytes = new byte[count];
    stream.ReadExactly(bytes);
    return bytes;
  }

  private static uint ReadUInt32(Stream stream) {
    Span<byte> bytes = stackalloc byte[4];
    stream.ReadExactly(bytes);
    return BinaryPrimitives.ReadUInt32LittleEndian(bytes);
  }

  private static int ReadInt32(Stream stream) {
    Span<byte> bytes = stackalloc byte[4];
    stream.ReadExactly(bytes);
    return BinaryPrimitives.ReadInt32LittleEndian(bytes);
  }

  private static long ReadInt64(Stream stream) {
    Span<byte> bytes = stackalloc byte[8];
    stream.ReadExactly(bytes);
    return BinaryPrimitives.ReadInt64LittleEndian(bytes);
  }
}
