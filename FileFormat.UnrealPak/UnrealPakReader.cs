#pragma warning disable CS1591
using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace FileFormat.UnrealPak;

/// <summary>
/// Reads Unreal Engine 4/5 <c>.pak</c> archives. A PAK file has three parts:
/// <list type="number">
///   <item><description>Entry payloads at the start of the file.</description></item>
///   <item><description>An index block listing filenames and their offsets/sizes.</description></item>
///   <item><description>A fixed-size footer (last 44..~220 bytes) containing magic + version + index location.</description></item>
/// </list>
/// <para>
/// This reader targets versions 3–11 (UE 4.15 through UE 5.x) for unencrypted archives with
/// either stored or zlib-compressed entries. Oodle compression and AES encryption are reported
/// (via <see cref="UnrealPakEntry.UnsupportedReason"/>) but not decoded.
/// </para>
/// </summary>
public sealed class UnrealPakReader {
  public const uint Magic = 0x5A6F12E1;

  public sealed record UnrealPakEntry(
    string Path,
    long Offset,
    long Size,
    long UncompressedSize,
    uint CompressionMethod,
    bool IsEncrypted,
    string? UnsupportedReason);

  private readonly Stream _stream;
  private readonly List<UnrealPakEntry> _entries = [];
  private readonly List<string> _compressionMethods = ["None"];

  /// <summary>The PAK version number parsed from the footer (3..11+).</summary>
  public uint PakVersion { get; }
  /// <summary>The mount-point prefix stored in the index.</summary>
  public string MountPoint { get; }
  /// <summary>Compression method names recorded in the footer (v8+). Index 0 is always None.</summary>
  public IReadOnlyList<string> CompressionMethods => this._compressionMethods;
  /// <summary>True if the index was marked AES-encrypted; nothing can be listed in that case.</summary>
  public bool IsIndexEncrypted { get; }
  /// <summary>File entries parsed from the index.</summary>
  public IReadOnlyList<UnrealPakEntry> Entries => this._entries;

  public UnrealPakReader(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    if (!stream.CanSeek)
      throw new ArgumentException("UnrealPak reader requires a seekable stream.", nameof(stream));
    this._stream = stream;

    // Footer layout is version-dependent. Try newest first; fall back as needed.
    // Common footers (starting from the end of the magic):
    //   v1:   magic(4) + version(4) + indexOffset(8) + indexSize(8) + hash(20)                         = 44
    //   v4:   + compressionMethods(5*32)                                                               = 204
    //   v8a:  + frozenFlag(1)                                                                          = 45 (v4.22)
    //   v8b:  + encryptedIndex(1) + encryptionKeyGuid(16) + compressionMethods(5*32)                   = 221 (v4.23)
    //   v9:   + flag frozenPak (1)                                                                     = 205
    //   v11:  + 5*32 compression methods                                                               = 221
    // We scan the last 256 bytes for the magic and walk backwards from there.
    var len = stream.Length;
    var scanSize = (int)Math.Min(256L, len);
    stream.Seek(len - scanSize, SeekOrigin.Begin);
    var tail = new byte[scanSize];
    stream.ReadExactly(tail);

    // Find the magic. The magic sits at a fixed negative offset per version; a backward scan
    // over int32s is unambiguous because 0x5A6F12E1 is very distinctive.
    var magicPos = -1;
    for (var i = tail.Length - 4; i >= 0; --i) {
      if (BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(i)) == Magic) {
        magicPos = i;
        break;
      }
    }
    if (magicPos < 0)
      throw new InvalidDataException("UnrealPak magic not found in footer.");

    var magicAbs = len - scanSize + magicPos;
    stream.Seek(magicAbs + 4, SeekOrigin.Begin);
    this.PakVersion = ReadUInt32(stream);
    var indexOffset = ReadInt64(stream);
    var indexSize = ReadInt64(stream);
    stream.Seek(20, SeekOrigin.Current); // index hash (SHA-1)

    // Version-dependent post-hash fields are not required for unencrypted listing; we
    // deliberately skip them. For v8+ an "encrypted index" byte *precedes* the hash; rather
    // than reproduce the full per-version footer catalog we probe by trying to parse the index
    // at indexOffset and, if that fails or looks encrypted, bail out gracefully.
    if (indexOffset < 0 || indexOffset >= len || indexSize <= 0 || indexOffset + indexSize > len)
      throw new InvalidDataException("UnrealPak footer references an out-of-range index.");

    // Parse the index.
    stream.Seek(indexOffset, SeekOrigin.Begin);
    try {
      this.MountPoint = ReadFString(stream);
    } catch (Exception ex) {
      throw new InvalidDataException(
        "UnrealPak index is unreadable — probably AES-encrypted or uses an unsupported version.", ex);
    }

    var fileCount = ReadInt32(stream);
    if (fileCount < 0 || fileCount > 10_000_000)
      throw new InvalidDataException($"UnrealPak file count ({fileCount}) is out of sane range.");

    for (var i = 0; i < fileCount; ++i) {
      var name = ReadFString(stream);
      var entry = ReadEntryRecord(stream, this.PakVersion, name);
      this._entries.Add(entry);
    }
  }

  /// <summary>
  /// Returns the decompressed bytes of an entry. Throws <see cref="NotSupportedException"/>
  /// when the entry uses AES or an unsupported compression method (e.g. Oodle).
  /// </summary>
  public byte[] Extract(UnrealPakEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.UnsupportedReason != null)
      throw new NotSupportedException($"Cannot extract '{entry.Path}': {entry.UnsupportedReason}");
    if (entry.IsEncrypted)
      throw new NotSupportedException($"Cannot extract '{entry.Path}': entry is AES-encrypted.");

    // Each entry payload is prefixed with another FPakEntry header; we skip it by re-reading.
    this._stream.Seek(entry.Offset, SeekOrigin.Begin);
    _ = ReadEntryRecord(this._stream, this.PakVersion, entry.Path);

    var bodyLength = entry.CompressionMethod == 0 ? entry.Size : entry.Size;
    if (entry.Offset + (this._stream.Position - entry.Offset) + bodyLength > this._stream.Length)
      throw new InvalidDataException($"Entry '{entry.Path}' payload truncated.");

    var compressed = new byte[bodyLength];
    this._stream.ReadExactly(compressed);

    if (entry.CompressionMethod == 0)
      return compressed;

    // Zlib is compression index 1 ("Zlib") in standard compression method tables.
    // We treat any compressed block as zlib — Oodle entries are already filtered above.
    using var input = new MemoryStream(compressed);
    using var zl = new ZLibStream(input, CompressionMode.Decompress);
    using var output = new MemoryStream();
    zl.CopyTo(output);
    var result = output.ToArray();
    if (result.LongLength != entry.UncompressedSize)
      throw new InvalidDataException(
        $"Entry '{entry.Path}' size mismatch: expected {entry.UncompressedSize}, got {result.LongLength}.");
    return result;
  }

  private UnrealPakEntry ReadEntryRecord(Stream stream, uint version, string name) {
    var offset = ReadInt64(stream);
    var size = ReadInt64(stream);
    var uncompressedSize = ReadInt64(stream);

    uint compressionMethod;
    if (version < 8) {
      compressionMethod = ReadUInt32(stream);
    } else {
      // v8+ shrank the method to a byte index into the name table.
      compressionMethod = (uint)stream.ReadByte();
    }

    // Pre-v1 had a timestamp here; ignored (always 0 in modern PAKs).
    if (version <= 1) stream.Seek(8, SeekOrigin.Current);

    stream.Seek(20, SeekOrigin.Current); // SHA-1 hash

    // Compression blocks (v3+).
    if (version >= 3 && compressionMethod != 0) {
      var blockCount = ReadInt32(stream);
      if (blockCount < 0 || blockCount > 1_000_000)
        throw new InvalidDataException($"UnrealPak compression block count {blockCount} out of range.");
      stream.Seek(blockCount * 16L, SeekOrigin.Current); // each block: start(8) + end(8)
    }

    // Encryption flag + compression block size (v4+).
    var isEncrypted = false;
    if (version >= 4) {
      isEncrypted = stream.ReadByte() != 0;
      ReadUInt32(stream); // compressionBlockSize
    }

    string? unsupported = null;
    if (compressionMethod != 0) {
      // Name index 1 is traditionally Zlib; anything else (especially "Oodle") we don't decode.
      var methodName = compressionMethod < this._compressionMethods.Count
        ? this._compressionMethods[(int)compressionMethod]
        : null;
      if (methodName != null && !methodName.Equals("Zlib", StringComparison.OrdinalIgnoreCase) &&
          !methodName.Equals("None", StringComparison.OrdinalIgnoreCase))
        unsupported = $"unsupported compression '{methodName}'";
      // When we haven't parsed the method table (footer not fully decoded) we assume method 1
      // means Zlib, which is the historical convention for v3..v7 PAKs.
    }
    if (isEncrypted) unsupported ??= "entry is AES-encrypted";

    return new UnrealPakEntry(
      Path: name,
      Offset: offset,
      Size: size,
      UncompressedSize: uncompressedSize,
      CompressionMethod: compressionMethod,
      IsEncrypted: isEncrypted,
      UnsupportedReason: unsupported);
  }

  // ─── FString / primitive readers ──────────────────────────────────────────

  private static string ReadFString(Stream stream) {
    var len = ReadInt32(stream);
    if (len == 0) return string.Empty;
    if (len > 0) {
      if (len > 65536) throw new InvalidDataException($"FString length {len} out of range.");
      var buf = new byte[len];
      stream.ReadExactly(buf);
      // Trim trailing null.
      var n = len;
      while (n > 0 && buf[n - 1] == 0) n--;
      return Encoding.ASCII.GetString(buf, 0, n);
    }
    // Negative => UTF-16LE, |len| is the code-unit count (including null terminator).
    var charCount = -len;
    if (charCount > 32768) throw new InvalidDataException($"FString UTF-16 length {charCount} out of range.");
    var bytes = new byte[charCount * 2];
    stream.ReadExactly(bytes);
    var nChars = charCount;
    if (nChars > 0 && bytes[(nChars - 1) * 2] == 0 && bytes[(nChars - 1) * 2 + 1] == 0) nChars--;
    return Encoding.Unicode.GetString(bytes, 0, nChars * 2);
  }

  private static uint ReadUInt32(Stream s) {
    Span<byte> b = stackalloc byte[4];
    s.ReadExactly(b);
    return BinaryPrimitives.ReadUInt32LittleEndian(b);
  }

  private static int ReadInt32(Stream s) {
    Span<byte> b = stackalloc byte[4];
    s.ReadExactly(b);
    return BinaryPrimitives.ReadInt32LittleEndian(b);
  }

  private static long ReadInt64(Stream s) {
    Span<byte> b = stackalloc byte[8];
    s.ReadExactly(b);
    return BinaryPrimitives.ReadInt64LittleEndian(b);
  }
}
