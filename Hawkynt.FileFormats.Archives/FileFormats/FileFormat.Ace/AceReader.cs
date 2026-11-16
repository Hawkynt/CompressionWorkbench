// Header parsing follows acefile (https://github.com/droe/acefile, BSD-2-Clause, Copyright (c)
// 2017-2026 Daniel Roethlisberger and contributors). See THIRD-PARTY-NOTICE.acefile.txt.

using System.Buffers.Binary;
using System.Text;
using Compression.Core.Dictionary.Ace;

namespace FileFormat.Ace;

/// <summary>
/// Reads ACE 1.0 / 2.0 archives: main header (found within the first 512 KiB, so SFX stubs are
/// skipped), file headers, stored / LZ77 / blocked members, Blowfish-encrypted members and comments.
/// A multi-volume set is read from its volumes in order (<c>.ace</c>, <c>.c00</c>, <c>.c01</c>, …):
/// a member split across volumes is the concatenation of its parts, and its CRC is the one in the
/// last part's header.
/// </summary>
public sealed class AceReader : IDisposable {
  private const int SearchLimit = 512 * 1024;

  private readonly IReadOnlyList<Stream> _volumes;
  private readonly int _ownedFrom;
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

  /// <summary>The volume number recorded in the first volume read (0 for the <c>.ace</c> volume of a set).</summary>
  public int FirstVolumeNumber { get; private set; }

  /// <summary>How many volumes were read.</summary>
  public int VolumeCount => this._volumes.Count;

  public bool IsLocked { get; private set; }

  public bool HasRecoveryRecord { get; private set; }

  /// <summary>Offset of the main header (non-zero for self-extracting archives).</summary>
  public long ArchiveOffset { get; private set; }

  public AceReader(Stream stream, bool leaveOpen = false, string? password = null)
    : this([stream ?? throw new ArgumentNullException(nameof(stream))], leaveOpen, password) {
  }

  /// <summary>Reads the volumes of a multi-volume set, in order; a single stream is an ordinary archive.</summary>
  /// <param name="volumes">The volumes, first to last; later volumes that do not continue the set are an error.</param>
  /// <param name="leaveOpen"><see langword="true"/> to leave the streams open on dispose.</param>
  /// <param name="password">The password for encrypted members, or null.</param>
  public AceReader(IReadOnlyList<Stream> volumes, bool leaveOpen = false, string? password = null)
    : this(volumes, password, ownedFrom: leaveOpen ? int.MaxValue : 0) {
  }

  /// <summary>Reads <paramref name="volumes"/>; the streams from index <paramref name="ownedFrom"/> on are disposed with the reader.</summary>
  internal AceReader(IReadOnlyList<Stream> volumes, string? password, int ownedFrom) {
    ArgumentNullException.ThrowIfNull(volumes);
    if (volumes.Count == 0)
      throw new ArgumentException("No ACE volume given.", nameof(volumes));
    this._volumes = volumes;
    this._ownedFrom = ownedFrom;
    this._password = password;
    try {
      this.ReadArchive();
    } catch {
      this.Dispose();
      throw;
    }
  }

  public void Dispose() {
    for (var i = this._ownedFrom; i < this._volumes.Count; ++i)
      this._volumes[i].Dispose();
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

  private static long FindMainHeader(Stream stream) {
    var length = stream.Length;
    var scan = (int)Math.Min(length, SearchLimit + 14);
    var buf = new byte[scan];
    stream.Position = 0;
    stream.ReadExactly(buf);
    var magic = Encoding.ASCII.GetBytes(AceConstants.Magic);
    for (var i = 0; i + 14 <= scan; ++i) {
      if (!buf.AsSpan(i + 7, 7).SequenceEqual(magic))
        continue;
      var size = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(i + 2));
      if (size < 26 || i + 4 + size > length)
        continue;
      var header = new byte[size];
      stream.Position = i + 4;
      stream.ReadExactly(header);
      if (AceConstants.AceCrc16(header) == BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(i)) && header[0] == AceConstants.HeaderTypeMain)
        return i;
    }
    throw new InvalidDataException("No ACE main header found.");
  }

  /// <summary>Reads just the main header of <paramref name="stream"/>: its volume number and whether it belongs to a multi-volume set.</summary>
  internal static bool TryReadVolumeInfo(Stream stream, out int volumeNumber, out bool multiVolume) {
    volumeNumber = 0;
    multiVolume = false;
    try {
      stream.Position = FindMainHeader(stream);
      var main = ReadHeader(stream);
      if (main == null || main.Length < 14)
        return false;
      volumeNumber = main[13];
      multiVolume = (BinaryPrimitives.ReadUInt16LittleEndian(main.AsSpan(1)) & AceConstants.FlagMultiVolume) != 0;
      return true;
    } catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException) {
      return false;
    }
  }

  private static byte[]? ReadHeader(Stream stream) {
    Span<byte> head = stackalloc byte[4];
    if (stream.ReadAtLeast(head, 4, throwOnEndOfStream: false) < 4)
      return null;
    var crc = BinaryPrimitives.ReadUInt16LittleEndian(head);
    var size = BinaryPrimitives.ReadUInt16LittleEndian(head[2..]);
    var buf = new byte[size];
    if (stream.ReadAtLeast(buf, size, throwOnEndOfStream: false) < size)
      throw new InvalidDataException("Truncated ACE header.");
    if (AceConstants.AceCrc16(buf) != crc)
      throw new InvalidDataException("ACE header CRC mismatch.");
    if (size < 3)
      throw new InvalidDataException("ACE header too short.");
    return buf;
  }

  /// <summary>One file header as read, before the parts of a split member are joined.</summary>
  private sealed record FilePart(string Name, ushort Flags, long PackSize, long OrigSize, uint DateTime, uint Attributes,
    uint Crc32, byte CompType, byte Quality, ushort Parameters, string? Comment, int Volume, long DataOffset) {
    public bool ContinuesFromPrevious => (this.Flags & AceConstants.FileFlagContinuedFromPrevious) != 0;
    public bool ContinuesInNext => (this.Flags & AceConstants.FileFlagContinuedInNext) != 0;
  }

  private void ReadArchive() {
    var parts = new List<FilePart>();
    for (var v = 0; v < this._volumes.Count; ++v) {
      var stream = this._volumes[v];
      var offset = FindMainHeader(stream);
      stream.Position = offset;
      var main = ReadHeader(stream) ?? throw new InvalidDataException("No ACE main header.");
      var flags = BinaryPrimitives.ReadUInt16LittleEndian(main.AsSpan(1));
      if ((flags & AceConstants.FlagAddSize) != 0)
        throw new InvalidDataException("ACE main header carries an add-size field.");
      var volumeNumber = main[13];
      if (v == 0)
        this.ReadMainHeader(main, flags, offset);
      else if ((flags & AceConstants.FlagMultiVolume) == 0 || volumeNumber != this.FirstVolumeNumber + v)
        throw new InvalidDataException($"ACE volume {v} does not continue the set (volume number {volumeNumber}).");
      ReadFileHeaders(stream, v, parts);
    }
    this.JoinParts(parts);
  }

  private void ReadMainHeader(byte[] main, ushort flags, long offset) {
    this.ArchiveOffset = offset;
    this.ExtractVersion = main[10];
    this.CreatorVersion = main[11];
    this.FirstVolumeNumber = main[13];
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
  }

  private static void ReadFileHeaders(Stream stream, int volume, List<FilePart> parts) {
    while (true) {
      var header = ReadHeader(stream);
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
        var dataOffset = stream.Position;
        if (packSize < 0 || dataOffset + packSize > stream.Length)
          throw new InvalidDataException($"ACE member '{name}' overruns the archive.");
        parts.Add(new(name, hflags, packSize, origSize, dateTime, attributes, crc32, compType, quality, parameters, comment, volume, dataOffset));
        stream.Position = dataOffset + packSize;
      } else {
        // recovery records and unknown headers: skip their add-size payload
        long addSize = 0;
        if ((hflags & AceConstants.FlagAddSize) != 0) {
          if ((hflags & AceConstants.Flag64Bit) != 0 && header.Length >= pos + 8)
            addSize = (long)BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(pos));
          else if (header.Length >= pos + 4)
            addSize = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(pos));
        }
        if (addSize < 0 || stream.Position + addSize > stream.Length)
          throw new InvalidDataException("ACE block overruns the archive.");
        stream.Position += addSize;
      }
    }
  }

  /// <summary>
  /// Joins the parts of members split across volumes. A part continuing from a volume that was not
  /// given (the set was opened at a later volume) is skipped; a member whose next volume is missing
  /// is listed as incomplete.
  /// </summary>
  private void JoinParts(List<FilePart> parts) {
    var pending = new List<FilePart>();
    foreach (var part in parts) {
      if (pending.Count == 0) {
        if (part.ContinuesFromPrevious) {
          if (this._entries.Count > 0)
            throw new InvalidDataException($"ACE member '{part.Name}' continues a member that did not end in the previous volume.");
          continue;
        }
      } else {
        if (!part.ContinuesFromPrevious)
          throw new InvalidDataException($"ACE member '{pending[0].Name}' is not continued in the next volume.");
        if (part.Name != pending[0].Name)
          throw new InvalidDataException($"ACE volume continues '{part.Name}' where '{pending[0].Name}' was split.");
      }
      pending.Add(part);
      if (!part.ContinuesInNext)
        this.AddEntry(pending, complete: true);
    }
    if (pending.Count > 0)
      this.AddEntry(pending, complete: false);
  }

  private void AddEntry(List<FilePart> pending, bool complete) {
    var first = pending[0];
    this._entries.Add(new AceEntry {
      Index = this._entries.Count,
      FileName = first.Name.Length > 0 ? first.Name : $"file{this._entries.Count:D4}",
      CompressedSize = pending.Sum(p => p.PackSize),
      OriginalSize = first.OrigSize,
      Crc32 = pending[^1].Crc32,
      CompressionType = first.CompType,
      Quality = first.Quality,
      DictionaryBits = (first.Parameters & 15) + 10,
      LastModified = FromDos(first.DateTime),
      Attributes = first.Attributes,
      Flags = first.Flags,
      Comment = first.Comment,
      DataOffset = first.DataOffset,
      IsComplete = complete,
      Segments = pending.Select(p => (p.Volume, p.DataOffset, p.PackSize)).ToArray(),
    });
    pending.Clear();
  }

  /// <summary>The mode instructions of the ACE 2.0 member extracted last, in stream order.</summary>
  internal IReadOnlyList<int> LastModes => this._decompressor.LastModes;

  /// <summary>Returns a member's decompressed bytes, verifying its CRC.</summary>
  public byte[] ExtractEntry(AceEntry entry) {
    ArgumentNullException.ThrowIfNull(entry);
    if (!entry.IsComplete)
      throw new InvalidDataException($"ACE member '{entry.FileName}' continues in a volume that was not found.");
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

  private byte[] ReadPacked(AceEntry entry) {
    var packed = new byte[entry.CompressedSize];
    var at = 0;
    foreach (var (volume, offset, length) in entry.Segments) {
      var stream = this._volumes[volume];
      stream.Position = offset;
      stream.ReadExactly(packed.AsSpan(at, (int)length));
      at += (int)length;
    }
    return packed;
  }

  private byte[] Decode(AceEntry entry) {
    byte[] data;
    if (entry.IsDirectory || entry.OriginalSize == 0)
      data = [];
    else {
      if (!entry.IsComplete)
        throw new InvalidDataException($"ACE member '{entry.FileName}' continues in a volume that was not found.");
      var packed = this.ReadPacked(entry);
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
