// Header parsing follows acefile (https://github.com/droe/acefile, BSD-2-Clause, Copyright (c)
// 2017-2026 Daniel Roethlisberger and contributors). See THIRD-PARTY-NOTICE.acefile.txt.

using System.Buffers.Binary;
using System.Text;
using Compression.Core.Dictionary.Ace;

namespace FileFormat.Ace;

/// <summary>
/// Reads ACE 1.0 / 2.0 archives: main header (found within the first 512 KiB, so SFX stubs are
/// skipped), file headers, stored / LZ77 / blocked members, Blowfish-encrypted members and comments.
/// Members of a multi-volume set that continue into another volume are listed but refused.
/// </summary>
public sealed class AceReader : IDisposable {
  private const int SearchLimit = 512 * 1024;

  private readonly Stream _stream;
  private readonly bool _leaveOpen;
  private readonly string? _password;
  private readonly List<AceEntry> _entries = [];
  private AceDecompressor _decompressor = new();
  private int _nextIndex;

  public IReadOnlyList<AceEntry> Entries => this._entries;

  public string? Comment { get; private set; }

  public byte ExtractVersion { get; private set; }

  public byte CreatorVersion { get; private set; }

  public bool IsSolid { get; private set; }

  public bool IsMultiVolume { get; private set; }

  public bool IsLocked { get; private set; }

  public bool HasRecoveryRecord { get; private set; }

  /// <summary>Offset of the main header (non-zero for self-extracting archives).</summary>
  public long ArchiveOffset { get; private set; }

  public AceReader(Stream stream, bool leaveOpen = false, string? password = null) {
    this._stream = stream ?? throw new ArgumentNullException(nameof(stream));
    this._leaveOpen = leaveOpen;
    this._password = password;
    this.ReadArchive();
  }

  public void Dispose() {
    if (!this._leaveOpen)
      this._stream.Dispose();
  }

  private static string DecodeName(ReadOnlySpan<byte> raw) {
    var name = AceConstants.Oem.GetString(raw).Replace('\\', '/');
    return name.TrimStart('/');
  }

  private static DateTime FromDos(uint dos) {
    try {
      return new DateTime((int)((dos >> 25) & 0x7F) + 1980, (int)((dos >> 21) & 0x0F), (int)((dos >> 16) & 0x1F),
        (int)((dos >> 11) & 0x1F), (int)((dos >> 5) & 0x3F), (int)(dos & 0x1F) * 2);
    } catch (ArgumentOutOfRangeException) {
      return new DateTime(1980, 1, 1);
    }
  }

  private long FindMainHeader() {
    var length = this._stream.Length;
    var scan = (int)Math.Min(length, SearchLimit + 14);
    var buf = new byte[scan];
    this._stream.Position = 0;
    this._stream.ReadExactly(buf);
    var magic = Encoding.ASCII.GetBytes(AceConstants.Magic);
    for (var i = 0; i + 14 <= scan; ++i) {
      if (!buf.AsSpan(i + 7, 7).SequenceEqual(magic))
        continue;
      var size = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(i + 2));
      if (size < 26 || i + 4 + size > length)
        continue;
      var header = new byte[size];
      this._stream.Position = i + 4;
      this._stream.ReadExactly(header);
      if (AceConstants.AceCrc16(header) == BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(i)) && header[0] == AceConstants.HeaderTypeMain)
        return i;
    }
    throw new InvalidDataException("No ACE main header found.");
  }

  private byte[]? ReadHeader(out ushort crc) {
    crc = 0;
    Span<byte> head = stackalloc byte[4];
    if (this._stream.ReadAtLeast(head, 4, throwOnEndOfStream: false) < 4)
      return null;
    crc = BinaryPrimitives.ReadUInt16LittleEndian(head);
    var size = BinaryPrimitives.ReadUInt16LittleEndian(head[2..]);
    var buf = new byte[size];
    if (this._stream.ReadAtLeast(buf, size, throwOnEndOfStream: false) < size)
      throw new InvalidDataException("Truncated ACE header.");
    if (AceConstants.AceCrc16(buf) != crc)
      throw new InvalidDataException("ACE header CRC mismatch.");
    if (size < 3)
      throw new InvalidDataException("ACE header too short.");
    return buf;
  }

  private void ReadArchive() {
    this.ArchiveOffset = this.FindMainHeader();
    this._stream.Position = this.ArchiveOffset;
    var main = this.ReadHeader(out _) ?? throw new InvalidDataException("No ACE main header.");
    var flags = BinaryPrimitives.ReadUInt16LittleEndian(main.AsSpan(1));
    if ((flags & AceConstants.FlagAddSize) != 0)
      throw new InvalidDataException("ACE main header carries an add-size field.");
    this.ExtractVersion = main[10];
    this.CreatorVersion = main[11];
    this.IsSolid = (flags & AceConstants.FlagSolid) != 0;
    this.IsMultiVolume = (flags & AceConstants.FlagMultiVolume) != 0;
    this.IsLocked = (flags & AceConstants.FlagLocked) != 0;
    this.HasRecoveryRecord = (flags & AceConstants.FlagRecovery) != 0;
    var i = 26;
    if (i + 1 <= main.Length) {
      // The advert length byte is present whether or not the advert flag is set.
      i += 1 + main[i];
      if ((flags & AceConstants.FlagComment) != 0 && i + 2 <= main.Length) {
        var size = BinaryPrimitives.ReadUInt16LittleEndian(main.AsSpan(i));
        i += 2;
        if (i + size > main.Length)
          throw new InvalidDataException("Truncated ACE main header comment.");
        this.Comment = AceConstants.Oem.GetString(AceDecompressor.DecompressComment(main.AsSpan(i, size)));
      }
    }

    while (true) {
      var header = this.ReadHeader(out _);
      if (header == null)
        break;
      var type = header[0];
      var hflags = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(1));
      var pos = 3;
      if (type is AceConstants.HeaderTypeFile32 or AceConstants.HeaderTypeFile64) {
        if ((hflags & AceConstants.FlagAddSize) == 0)
          throw new InvalidDataException("ACE file header without add-size field.");
        long packSize, origSize;
        if ((hflags & AceConstants.Flag64Bit) != 0) {
          if (type != AceConstants.HeaderTypeFile64 || header.Length < pos + 16)
            throw new InvalidDataException("Malformed 64-bit ACE file header.");
          packSize = (long)BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(pos));
          origSize = (long)BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(pos + 8));
          pos += 16;
        } else {
          if (type != AceConstants.HeaderTypeFile32 || header.Length < pos + 8)
            throw new InvalidDataException("Malformed ACE file header.");
          packSize = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(pos));
          origSize = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(pos + 4));
          pos += 8;
        }
        if (header.Length < pos + 20)
          throw new InvalidDataException("Truncated ACE file header.");
        var dateTime = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(pos));
        var attributes = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(pos + 4));
        var crc32 = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(pos + 8));
        var compType = header[pos + 12];
        var quality = header[pos + 13];
        var parameters = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(pos + 14));
        var nameSize = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(pos + 18));
        pos += 20;
        if (header.Length < pos + nameSize)
          throw new InvalidDataException("Truncated ACE file name.");
        var name = DecodeName(header.AsSpan(pos, nameSize));
        pos += nameSize;
        string? comment = null;
        if ((hflags & AceConstants.FlagComment) != 0 && header.Length >= pos + 2) {
          var size = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(pos));
          pos += 2;
          if (header.Length < pos + size)
            throw new InvalidDataException("Truncated ACE file comment.");
          comment = AceConstants.Oem.GetString(AceDecompressor.DecompressComment(header.AsSpan(pos, size)));
        }
        var dataOffset = this._stream.Position;
        if (packSize < 0 || dataOffset + packSize > this._stream.Length)
          throw new InvalidDataException($"ACE member '{name}' overruns the archive.");
        this._entries.Add(new AceEntry {
          Index = this._entries.Count,
          FileName = name.Length > 0 ? name : $"file{this._entries.Count:D4}",
          CompressedSize = packSize,
          OriginalSize = origSize,
          Crc32 = crc32,
          CompressionType = compType,
          Quality = quality,
          DictionaryBits = (parameters & 15) + 10,
          LastModified = FromDos(dateTime),
          Attributes = attributes,
          Flags = hflags,
          Comment = comment,
          DataOffset = dataOffset,
        });
        this._stream.Position = dataOffset + packSize;
      } else {
        // recovery records and unknown headers: skip their add-size payload
        long addSize = 0;
        if ((hflags & AceConstants.FlagAddSize) != 0) {
          if ((hflags & AceConstants.Flag64Bit) != 0 && header.Length >= pos + 8)
            addSize = (long)BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(pos));
          else if (header.Length >= pos + 4)
            addSize = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(pos));
        }
        if (addSize < 0 || this._stream.Position + addSize > this._stream.Length)
          throw new InvalidDataException("ACE block overruns the archive.");
        this._stream.Position += addSize;
      }
    }
  }

  /// <summary>Returns a member's decompressed bytes, verifying its CRC.</summary>
  public byte[] ExtractEntry(AceEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (entry.IsContinued)
      throw new NotSupportedException("ACE multi-volume members are not supported.");
    // Solid members depend on everything decoded before them: restart from the first member when
    // asked to go back, and decode (and verify) the members in between.
    if (this.IsSolid && entry.Index != this._nextIndex) {
      if (entry.Index < this._nextIndex) {
        this._decompressor = new AceDecompressor();
        this._nextIndex = 0;
      }
      while (this._nextIndex < entry.Index)
        this.Decode(this._entries[this._nextIndex]);
    }
    return this.Decode(entry);
  }

  private byte[] Decode(AceEntry entry) {
    byte[] data;
    if (entry.IsDirectory || entry.OriginalSize == 0)
      data = [];
    else {
      if (entry.IsContinued)
        throw new NotSupportedException("ACE multi-volume members are not supported.");
      var packed = new byte[entry.CompressedSize];
      this._stream.Position = entry.DataOffset;
      this._stream.ReadExactly(packed);
      if (entry.IsEncrypted) {
        if (string.IsNullOrEmpty(this._password))
          throw new InvalidOperationException($"ACE member '{entry.FileName}' is encrypted; a password is required.");
        if (packed.Length % 8 != 0)
          throw new InvalidDataException("Encrypted ACE member is not a whole number of cipher blocks.");
        new AceBlowfish(this._password).Decrypt(packed);
      }
      try {
        data = this._decompressor.Decompress(packed, entry.OriginalSize, entry.CompressionType, entry.DictionaryBits);
      } catch (InvalidDataException) when (entry.IsEncrypted) {
        throw new InvalidDataException($"ACE member '{entry.FileName}': wrong password or corrupt data.");
      }
      if (AceConstants.AceCrc32(data) != entry.Crc32)
        throw new InvalidDataException(entry.IsEncrypted
          ? $"ACE member '{entry.FileName}': wrong password or corrupt data."
          : $"ACE member '{entry.FileName}': CRC mismatch.");
    }
    this._nextIndex = entry.Index + 1;
    return data;
  }
}
