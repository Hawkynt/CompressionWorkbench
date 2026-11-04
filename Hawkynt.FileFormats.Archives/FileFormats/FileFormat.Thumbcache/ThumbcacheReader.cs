using System.Buffers.Binary;
using System.Text;

namespace FileFormat.Thumbcache;

/// <summary>One cached thumbnail.</summary>
/// <param name="Offset">File offset of the entry's <c>CMMM</c> record.</param>
/// <param name="Hash">The 64-bit cache hash, as the bytes are stored.</param>
/// <param name="Identifier">The UTF-16 identifier string (normally the hash in hex).</param>
/// <param name="Extension">Vista's stored extension field; empty for later versions.</param>
/// <param name="Width">Thumbnail width (Windows 8 and later; 0 otherwise).</param>
/// <param name="Height">Thumbnail height (Windows 8 and later; 0 otherwise).</param>
/// <param name="DataOffset">File offset of the thumbnail bytes.</param>
/// <param name="DataSize">Thumbnail byte count.</param>
public sealed record ThumbcacheEntry(
  long Offset, byte[] Hash, string Identifier, string Extension, uint Width, uint Height, long DataOffset, int DataSize) {

  /// <summary>The hash as lowercase hex of the stored bytes.</summary>
  public string HashHex => Convert.ToHexStringLower(this.Hash);
}

/// <summary>
/// Reads a Windows Explorer thumbnail cache database (<c>thumbcache_&lt;size&gt;.db</c>, signature
/// <c>CMMM</c>) as written by Windows Vista through Windows 11.
/// </summary>
/// <remarks>
/// <para>File header (24 bytes, little-endian): <c>"CMMM"</c>, format version (20 Vista, 21 Windows 7,
/// 30 Windows 8, 31 Windows 8.1, 32 Windows 10/11), cache type, then — up to version 21 — offset of the
/// first entry, offset of the first free byte and the entry count; from version 30 an unknown
/// field, offset of the first entry and offset of the first free byte.</para>
/// <para>Each entry: <c>"CMMM"</c>, record size, 8-byte hash, [Vista: 4 UTF-16 extension characters],
/// identifier size, padding size, data size, [version ≥ 30: width, height], one reserved 32-bit
/// field, data checksum (8), header checksum (8), identifier (UTF-16), padding, data.</para>
/// <para>Sources: Joachim Metz, "Windows Explorer Thumbnail Cache database format" (libyal
/// documentation) for the layout; the version-dependent field placement was confirmed against the
/// Vista, 8.1, 10 and 11 databases in Fox-IT's dissect.thumbcache test corpus, with dissect.thumbcache
/// (AGPL-3.0, used only as a black-box oracle) agreeing on every entry.</para>
/// </remarks>
public sealed class ThumbcacheReader {

  /// <summary>The file and entry signature.</summary>
  public static ReadOnlySpan<byte> Signature => "CMMM"u8;

  /// <summary>Size of the file header.</summary>
  public const int HeaderSize = 24;

  /// <summary>Format version.</summary>
  public uint Version { get; }

  /// <summary>Cache type (which thumbnail size the database holds).</summary>
  public uint CacheType { get; }

  /// <summary>Offset of the first entry.</summary>
  public uint FirstEntryOffset { get; }

  /// <summary>Offset of the first free byte: entries end here.</summary>
  public uint FirstFreeOffset { get; }

  /// <summary>Entries in file order.</summary>
  public IReadOnlyList<ThumbcacheEntry> Entries { get; }

  /// <summary>Whether <paramref name="version"/> is a format version this reader knows.</summary>
  public static bool IsKnownVersion(uint version) => version is 20 or 21 or 30 or 31 or 32;

  /// <summary>Parses <paramref name="data"/>.</summary>
  /// <exception cref="InvalidDataException">Not a thumbnail cache, an unknown version, or an entry that runs off its record.</exception>
  public ThumbcacheReader(ReadOnlySpan<byte> data) {
    if (data.StartsWith("IMMM"u8))
      throw new InvalidDataException("This is a thumbcache_idx.db index, which holds no thumbnails.");
    if (data.Length < HeaderSize || !data[..4].SequenceEqual(Signature))
      throw new InvalidDataException("Not a thumbnail cache database (missing CMMM signature).");
    this.Version = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);
    if (!IsKnownVersion(this.Version))
      throw new InvalidDataException($"Unknown thumbnail cache version {this.Version}.");
    this.CacheType = BinaryPrimitives.ReadUInt32LittleEndian(data[8..]);
    var legacy = this.Version <= 21;
    this.FirstEntryOffset = BinaryPrimitives.ReadUInt32LittleEndian(data[(legacy ? 12 : 16)..]);
    this.FirstFreeOffset = BinaryPrimitives.ReadUInt32LittleEndian(data[(legacy ? 16 : 20)..]);
    if (this.FirstEntryOffset < HeaderSize || this.FirstFreeOffset < this.FirstEntryOffset || this.FirstFreeOffset > data.Length)
      throw new InvalidDataException("Thumbnail cache entry area lies outside the file.");

    var fixedSize = this.Version switch { 20 => 0x38, 21 => 0x30, _ => 0x38 };
    var entries = new List<ThumbcacheEntry>();
    long pos = this.FirstEntryOffset;
    while (pos + 8 <= this.FirstFreeOffset) {
      var r = data[(int)pos..];
      if (!r[..4].SequenceEqual(Signature))
        throw new InvalidDataException($"Thumbnail cache entry at 0x{pos:X} lacks its CMMM signature.");
      var size = BinaryPrimitives.ReadUInt32LittleEndian(r[4..]);
      if (size < fixedSize || pos + size > this.FirstFreeOffset)
        throw new InvalidDataException($"Thumbnail cache entry at 0x{pos:X} has an impossible size {size}.");
      var hash = r.Slice(8, 8).ToArray();
      var p = 16;
      var extension = "";
      if (this.Version == 20) {
        extension = Encoding.Unicode.GetString(r.Slice(p, 8)).TrimEnd('\0');
        p += 8;
      }
      var idSize = BinaryPrimitives.ReadUInt32LittleEndian(r[p..]);
      var padSize = BinaryPrimitives.ReadUInt32LittleEndian(r[(p + 4)..]);
      var dataSize = BinaryPrimitives.ReadUInt32LittleEndian(r[(p + 8)..]);
      uint width = 0, height = 0;
      if (this.Version >= 30) {
        width = BinaryPrimitives.ReadUInt32LittleEndian(r[(p + 12)..]);
        height = BinaryPrimitives.ReadUInt32LittleEndian(r[(p + 16)..]);
      }
      if ((ulong)fixedSize + idSize + padSize + dataSize > size || (idSize & 1) != 0)
        throw new InvalidDataException($"Thumbnail cache entry at 0x{pos:X} overruns its record.");
      var identifier = Encoding.Unicode.GetString(r.Slice(fixedSize, (int)idSize));
      entries.Add(new ThumbcacheEntry(pos, hash, identifier, extension, width, height,
        pos + fixedSize + idSize + padSize, (int)dataSize));
      pos += size;
    }
    this.Entries = entries;
  }

  /// <summary>
  /// A file name for the entry: its identifier (or hash) made path-safe, with the extension Vista
  /// stored or one sniffed from the image signature.
  /// </summary>
  public static string EntryName(ThumbcacheEntry e, ReadOnlySpan<byte> image) {
    var stem = new StringBuilder();
    foreach (var c in e.Identifier.Length > 0 ? e.Identifier : e.HashHex)
      stem.Append(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '_');
    var ext = e.Extension.Length > 0 ? e.Extension.ToLowerInvariant()
      : image.StartsWith("BM"u8) ? "bmp"
      : image.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF]) ? "jpg"
      : image.StartsWith((ReadOnlySpan<byte>)[0x89, (byte)'P', (byte)'N', (byte)'G']) ? "png"
      : "bin";
    return $"{stem}.{ext}";
  }
}
