using System.Buffers.Binary;
using System.Text;
using Compression.Core.Checksums;

namespace FileFormat.StuffIt;

/// <summary>
/// Reads StuffIt archives: the classic "SIT!" format of StuffIt 1.5-4.x (with folders) and the
/// StuffIt 5 format ("StuffIt (c)1997-..." banner) written by StuffIt 5.x-7.x and StuffIt
/// Deluxe/Standard for Windows.
/// </summary>
/// <remarks>
/// Forks are decoded with methods 0 (store), 1 (RLE90), 2 (compress LZW), 3 (Huffman), 5 (LZAH),
/// 13 (LZ+Huffman) and 15 (Arsenic). Method 8 (MW), 14 (installer) and encrypted forks are
/// listed but refused on extraction. Layouts follow XADMaster's XADStuffItParser and
/// XADStuffIt5Parser (LGPL-2.1-or-later).
/// </remarks>
public sealed class StuffItReader : IDisposable {
  private const int ClassicHeaderSize = 22;
  private const int ClassicEntrySize = 112;
  private const byte FlagEncrypted = 0x80;
  private const byte FolderStart = 0x20;
  private const byte FolderEnd = 0x21;
  private const byte FolderContainsEncrypted = 0x10;
  private const uint Sit5EntryId = 0xA5A5A5A5;
  private const byte Sit5FlagDirectory = 0x40;
  private const byte Sit5FlagCrypted = 0x20;

  private static ReadOnlySpan<byte> Sit5Banner => "StuffIt (c)1997-"u8;

  private const string MacRomanHigh =
    "ÄÅÇÉÑÖÜáàâäãåçéèêëíìîïñóòôöõúùûü†°¢£§•¶ß®©™´¨≠ÆØ∞±≤≥¥µ∂∑∏π∫ªºΩæø¿¡¬√ƒ≈∆«»… ÀÃÕŒœ–—“”‘’÷◊ÿŸ⁄€‹›ﬁﬂ‡·‚„‰ÂÊÁËÈÍÎÏÌÓÔÒÚÛÙıˆ˜¯˘˙˚¸˝˛ˇ";

  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private readonly List<StuffItEntry> _entries = [];
  private readonly long _base;
  private bool _disposed;

  /// <summary>
  /// Opens a StuffIt archive from the given stream and parses the entry directory.
  /// </summary>
  /// <param name="stream">A seekable stream positioned at a classic or StuffIt 5 archive.</param>
  /// <param name="leaveOpen"><see langword="true"/> to leave <paramref name="stream"/> open after disposal.</param>
  /// <exception cref="InvalidDataException">The stream is not a StuffIt archive or its directory is damaged.</exception>
  public StuffItReader(Stream stream, bool leaveOpen = false) {
    this._stream = stream ?? throw new ArgumentNullException(nameof(stream));
    this._leaveOpen = leaveOpen;
    this._base = stream.CanSeek ? stream.Position : 0;
    Span<byte> head = stackalloc byte[100];
    var got = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
    stream.Position = this._base;
    if (got >= 14 && BinaryPrimitives.ReadUInt32BigEndian(head[10..]) == 0x724C6175 && IsClassicSignature(head)) {
      this.IsStuffIt5 = false;
      this.ParseClassic();
    } else if (got >= 100 && head.StartsWith(Sit5Banner)) {
      this.IsStuffIt5 = true;
      this.ParseSit5();
    } else {
      throw new InvalidDataException("Not a StuffIt archive (no 'SIT!'/'rLau' header and no StuffIt 5 banner).");
    }
  }

  /// <summary>Gets whether the archive uses the StuffIt 5 layout.</summary>
  public bool IsStuffIt5 { get; }

  /// <summary>Gets the entries (folders first in archive order, then their contents).</summary>
  public IReadOnlyList<StuffItEntry> Entries => this._entries;

  private static bool IsClassicSignature(ReadOnlySpan<byte> h)
    => BinaryPrimitives.ReadUInt32BigEndian(h) == 0x53495421 // "SIT!"
       || h[0] == 'S' && h[1] == 'T' && (h[2] == 'i' && (h[3] == 'n' || h[3] is >= (byte)'0' and <= (byte)'9')
                                         || h[2] is >= (byte)'0' and <= (byte)'9' && h[3] is >= (byte)'0' and <= (byte)'9');

  /// <summary>Extracts and verifies the data fork of <paramref name="entry"/>.</summary>
  /// <exception cref="NotSupportedException">The fork is encrypted or uses an unsupported method.</exception>
  /// <exception cref="InvalidDataException">The fork is damaged or fails its checksum.</exception>
  public byte[] Extract(StuffItEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    ObjectDisposedException.ThrowIf(this._disposed, this);
    if (entry.IsDirectory) return [];
    return this.ReadFork(entry, entry.DataForkOffset, entry.CompressedDataSize, entry.DataForkSize,
      entry.DataMethod, entry.DataForkCrc16, "data fork");
  }

  /// <summary>Extracts and verifies the resource fork of <paramref name="entry"/>.</summary>
  public byte[] ExtractResourceFork(StuffItEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    ObjectDisposedException.ThrowIf(this._disposed, this);
    if (entry.IsDirectory) return [];
    return this.ReadFork(entry, entry.ResourceDataOffset, entry.CompressedResourceSize, entry.ResourceForkSize,
      entry.ResourceMethod, entry.ResourceForkCrc16, "resource fork");
  }

  private byte[] ReadFork(StuffItEntry entry, long offset, long compressedSize, long size, int method, ushort crc, string fork) {
    if (size == 0 && compressedSize == 0) return [];
    if (entry.IsEncrypted)
      throw new NotSupportedException($"'{entry.FileName}' ({fork}) is encrypted; StuffIt decryption is not supported.");
    if (size > int.MaxValue || compressedSize > int.MaxValue)
      throw new NotSupportedException($"'{entry.FileName}' ({fork}) exceeds the 2 GiB in-memory limit.");
    if (offset < 0 || offset + compressedSize > this._stream.Length)
      throw new InvalidDataException($"'{entry.FileName}' ({fork}) points outside the archive.");

    this._stream.Position = offset;
    var compressed = new byte[compressedSize];
    this._stream.ReadExactly(compressed);
    var length = (int)size;
    var data = method switch {
      0 => compressed.Length == length ? compressed : throw new InvalidDataException($"'{entry.FileName}' ({fork}): stored size mismatch."),
      1 => StuffItRle90.Decode(compressed, length),
      2 => StuffItLzw.Decode(compressed, length),
      3 => StuffItHuffman.Decode(compressed, length),
      5 => StuffItLzahDecoder.Decode(compressed, length),
      13 => StuffIt13Decoder.Decode(compressed, length),
      15 => StuffItArsenicDecoder.Decode(compressed, length),
      _ => throw new NotSupportedException($"StuffIt compression method {method} ({MethodName(method)}) is not supported."),
    };

    // Method 15 verifies its own CRC-32; every other method carries a CRC-16/ARC of the fork.
    if (method != 15 && Crc16.Compute(data) != crc)
      throw new InvalidDataException($"CRC-16 mismatch for '{entry.FileName}' ({fork}).");
    return data;
  }

  /// <summary>Human-readable name of a StuffIt method number.</summary>
  public static string MethodName(int method) => (method & 0x0F) switch {
    0 => "None",
    1 => "RLE",
    2 => "Compress",
    3 => "Huffman",
    5 => "LZAH",
    6 => "Fixed Huffman",
    8 => "MW",
    13 => "LZ+Huffman",
    14 => "Installer",
    15 => "Arsenic",
    var m => $"Method {m}",
  };

  /// <inheritdoc />
  public void Dispose() {
    if (this._disposed) return;
    this._disposed = true;
    if (!this._leaveOpen) this._stream.Dispose();
  }

  // ── classic "SIT!" ──────────────────────────────────────────────────

  private void ParseClassic() {
    var header = this.ReadAt(this._base, ClassicHeaderSize);
    var totalSize = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(6));
    var end = Math.Min(this._base + totalSize, this._stream.Length);
    if (totalSize < ClassicHeaderSize) end = this._stream.Length;

    var dirs = new Stack<string>();
    var pos = this._base + ClassicHeaderSize;
    while (pos + ClassicEntrySize <= end) {
      var h = this.ReadAt(pos, ClassicEntrySize);
      if (BinaryPrimitives.ReadUInt16BigEndian(h.AsSpan(110)) != Crc16.Compute(h.AsSpan(0, 110)))
        throw new InvalidDataException($"StuffIt entry header at offset {pos} fails its CRC.");
      pos += ClassicEntrySize;

      int resMethod = h[0], dataMethod = h[1];
      var nameLength = Math.Min((int)h[2], 31);
      var name = Component(h.AsSpan(3, nameLength));
      var path = dirs.Count == 0 ? name : dirs.Peek() + "/" + name;
      var modified = MacDate(BinaryPrimitives.ReadUInt32BigEndian(h.AsSpan(80)));

      var dataFolder = dataMethod & ~(FlagEncrypted | FolderContainsEncrypted);
      var resFolder = resMethod & ~(FlagEncrypted | FolderContainsEncrypted);
      if (dataFolder == FolderStart || resFolder == FolderStart) {
        this._entries.Add(new StuffItEntry {
          FileName = path,
          IsDirectory = true,
          LastModified = modified,
          IsEncrypted = ((dataMethod | resMethod) & FolderContainsEncrypted) != 0,
        });
        dirs.Push(path);
        continue;
      }
      if (dataFolder == FolderEnd || resFolder == FolderEnd) {
        if (dirs.Count > 0) dirs.Pop();
        continue;
      }

      var resLength = BinaryPrimitives.ReadUInt32BigEndian(h.AsSpan(84));
      var dataLength = BinaryPrimitives.ReadUInt32BigEndian(h.AsSpan(88));
      var resCompressed = BinaryPrimitives.ReadUInt32BigEndian(h.AsSpan(92));
      var dataCompressed = BinaryPrimitives.ReadUInt32BigEndian(h.AsSpan(96));
      this._entries.Add(new StuffItEntry {
        FileName = path,
        FileType = Encoding.Latin1.GetString(h, 66, 4),
        FileCreator = Encoding.Latin1.GetString(h, 70, 4),
        LastModified = modified,
        ResourceMethod = resMethod & 0x7F,
        DataMethod = dataMethod & 0x7F,
        IsEncrypted = ((resMethod | dataMethod) & FlagEncrypted) != 0,
        ResourceForkSize = resLength,
        DataForkSize = dataLength,
        CompressedResourceSize = resCompressed,
        CompressedDataSize = dataCompressed,
        ResourceForkCrc16 = BinaryPrimitives.ReadUInt16BigEndian(h.AsSpan(100)),
        DataForkCrc16 = BinaryPrimitives.ReadUInt16BigEndian(h.AsSpan(102)),
        ResourceDataOffset = pos,
        DataForkOffset = pos + resCompressed,
      });
      pos += (long)resCompressed + dataCompressed;
    }
  }

  // ── StuffIt 5 ───────────────────────────────────────────────────────

  private void ParseSit5() {
    var h = this.ReadAt(this._base, 100);
    if (h[82] != 5)
      throw new InvalidDataException($"StuffIt 5 archive version {h[82]} is not supported.");
    var rootCount = BinaryPrimitives.ReadUInt16BigEndian(h.AsSpan(92));
    var firstOffset = BinaryPrimitives.ReadUInt32BigEndian(h.AsSpan(94));

    var dirs = new Dictionary<long, string>();
    var pos = this._base + firstOffset;
    var remaining = (long)rootCount;
    var guard = 0;
    while (remaining-- > 0) {
      if (++guard > 1_000_000) throw new InvalidDataException("StuffIt 5 directory does not terminate.");
      var entryOffset = pos;
      var fixedPart = this.ReadAt(pos, 48);
      if (BinaryPrimitives.ReadUInt32BigEndian(fixedPart) != Sit5EntryId)
        throw new InvalidDataException($"StuffIt 5 entry at offset {pos} has no A5A5A5A5 marker.");
      int version = fixedPart[4];
      int headerSize = BinaryPrimitives.ReadUInt16BigEndian(fixedPart.AsSpan(6));
      int flags = fixedPart[9];
      var created = BinaryPrimitives.ReadUInt32BigEndian(fixedPart.AsSpan(10));
      var modified = BinaryPrimitives.ReadUInt32BigEndian(fixedPart.AsSpan(14));
      var parentOffset = BinaryPrimitives.ReadUInt32BigEndian(fixedPart.AsSpan(26));
      int nameLength = BinaryPrimitives.ReadUInt16BigEndian(fixedPart.AsSpan(30));
      var dataLength = BinaryPrimitives.ReadUInt32BigEndian(fixedPart.AsSpan(34));
      var dataCompressed = BinaryPrimitives.ReadUInt32BigEndian(fixedPart.AsSpan(38));
      var dataCrc = BinaryPrimitives.ReadUInt16BigEndian(fixedPart.AsSpan(42));
      _ = created;

      pos += 46;
      var isDir = (flags & Sit5FlagDirectory) != 0;
      var crypted = (flags & Sit5FlagCrypted) != 0;
      int dataMethod = 0, childCount = 0;
      if (isDir) {
        childCount = BinaryPrimitives.ReadUInt16BigEndian(this.ReadAt(pos, 2));
        pos += 2;
        if (dataLength == 0xFFFFFFFF) {
          // Marker records that follow every folder: 48 bytes, not counted among its children.
          ++remaining;
          continue;
        }
      } else {
        var mp = this.ReadAt(pos, 2);
        dataMethod = mp[0];
        int passLength = mp[1];
        pos += 2 + (crypted && dataLength != 0 ? passLength : 0);
        if (!crypted && passLength != 0)
          throw new InvalidDataException("StuffIt 5 entry has key material but is not marked encrypted.");
      }

      var name = Component(this.ReadAt(pos, nameLength));
      pos += nameLength;
      if (pos < entryOffset + headerSize) {
        var commentSize = BinaryPrimitives.ReadUInt16BigEndian(this.ReadAt(pos, 2));
        pos += 4 + commentSize;
      }
      // Second block: fork flags, type/creator, Finder flags, then (optionally) the resource fork record.
      var second = this.ReadAt(pos, 14);
      var hasResource = (BinaryPrimitives.ReadUInt16BigEndian(second) & 0x01) != 0;
      var fileType = Encoding.Latin1.GetString(second, 4, 4);
      var fileCreator = Encoding.Latin1.GetString(second, 8, 4);
      pos += 14 + (version == 1 ? 22 : 18);

      uint resLength = 0, resCompressed = 0;
      ushort resCrc = 0;
      var resMethod = 0;
      if (hasResource) {
        var r = this.ReadAt(pos, 14);
        resLength = BinaryPrimitives.ReadUInt32BigEndian(r);
        resCompressed = BinaryPrimitives.ReadUInt32BigEndian(r.AsSpan(4));
        resCrc = BinaryPrimitives.ReadUInt16BigEndian(r.AsSpan(8));
        resMethod = r[12];
        int passLength = r[13];
        pos += 14 + (crypted && resLength != 0 ? passLength : 0);
      }

      var parent = dirs.GetValueOrDefault(parentOffset - this._base) ?? dirs.GetValueOrDefault(parentOffset);
      var path = parent is null ? name : parent + "/" + name;

      if (isDir) {
        dirs[entryOffset - this._base] = path;
        this._entries.Add(new StuffItEntry { FileName = path, IsDirectory = true, LastModified = MacDate(modified), IsEncrypted = crypted });
        remaining += childCount;
        continue;
      }

      this._entries.Add(new StuffItEntry {
        FileName = path,
        FileType = fileType,
        FileCreator = fileCreator,
        LastModified = MacDate(modified),
        IsEncrypted = crypted,
        DataMethod = dataMethod,
        ResourceMethod = resMethod,
        DataForkSize = dataLength,
        CompressedDataSize = dataCompressed,
        DataForkCrc16 = dataCrc,
        ResourceForkSize = resLength,
        CompressedResourceSize = resCompressed,
        ResourceForkCrc16 = resCrc,
        ResourceDataOffset = pos,
        DataForkOffset = pos + resCompressed,
      });
      pos += (long)resCompressed + dataCompressed;
    }
  }

  // ── helpers ─────────────────────────────────────────────────────────

  private byte[] ReadAt(long offset, int count) {
    if (offset < 0 || offset + count > this._stream.Length)
      throw new InvalidDataException($"StuffIt structure at offset {offset} runs past the end of the archive.");
    this._stream.Position = offset;
    var buffer = new byte[count];
    this._stream.ReadExactly(buffer);
    return buffer;
  }

  private static string Component(ReadOnlySpan<byte> raw) {
    var sb = new StringBuilder(raw.Length);
    foreach (var b in raw) sb.Append(b < 0x80 ? (char)b : MacRomanHigh[b - 0x80]);
    // A Mac name may contain '/', which would read as a folder separator here.
    return sb.Replace('/', ':').ToString();
  }

  private static DateTime MacDate(uint seconds)
    => seconds == 0 ? DateTime.MinValue : new DateTime(1904, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(seconds);
}
