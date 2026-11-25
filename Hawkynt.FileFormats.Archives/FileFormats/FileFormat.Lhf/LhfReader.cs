using System.Buffers.Binary;
using System.Text;

namespace FileFormat.Lhf;

/// <summary>One entry of an LhF archive.</summary>
/// <param name="Name">Path inside the archive, <c>/</c>-separated.</param>
/// <param name="Comment">The AmigaDOS file note, or <see langword="null"/>.</param>
/// <param name="Protection">The low eight AmigaDOS protection bits.</param>
/// <param name="Size">Unpacked size.</param>
/// <param name="StoredSize">Bytes the entry occupies in the archive.</param>
/// <param name="IsStored">The data is kept uncompressed.</param>
/// <param name="IsDirectory">The entry is a directory.</param>
/// <param name="RawDate">The packed date word as stored.</param>
/// <param name="DataOffset">Offset of the entry data in the archive.</param>
public sealed record LhfEntry(string Name, string? Comment, byte Protection, long Size, long StoredSize,
  bool IsStored, bool IsDirectory, uint RawDate, int DataOffset) {
  /// <summary>The modification time, or <see langword="null"/> when the packed date is not a valid date.</summary>
  public DateTime? LastModified => LhfReader.DecodeDate(this.RawDate);
}

/// <summary>
/// Reads an Amiga LhF 1.03 archive (magic <c>LhF\0</c>): a flat list of files, each stored or packed
/// with LhF's own LZ/Huffman coder, with AmigaDOS protection bits, file notes and dates.
/// </summary>
/// <remarks>
/// <para>Layout (big-endian): <c>LhF\0</c>, the length of the used part of the file, then entries up to
/// that length: comment flag (u8), protection (u8), name-field length (u16), packed size (u32),
/// unpacked size (u32; 0 = stored, packed size is the size; <c>0xFFFFFFFF</c> = directory), date (u32),
/// the name field (the NUL-terminated path, followed by the NUL-terminated file note when the comment
/// flag is set), the data. See <c>docs/LHF-ON-DISK.md</c>.</para>
/// <para>Sourcing: layout from libxad's LhF client (XADMaster <c>libxad/clients/LhF.c</c>,
/// LGPL-2.1-or-later), checked against archives LhF 1.03 wrote and listed.</para>
/// </remarks>
public sealed class LhfReader {
  private const int EntryHeaderSize = 16;
  private readonly byte[] _data;

  /// <summary>The entries in archive order.</summary>
  public IReadOnlyList<LhfEntry> Entries { get; }

  /// <summary>Length of the used part of the file, as the header records it.</summary>
  public long UsedLength { get; }

  /// <summary>Returns true when <paramref name="data"/> starts with the LhF magic.</summary>
  public static bool IsLhf(ReadOnlySpan<byte> data) => data.Length >= 8 && data[..4].SequenceEqual("LhF\0"u8);

  /// <summary>Reads the entry list.</summary>
  /// <exception cref="InvalidDataException">Not an LhF archive, or an entry runs past the file.</exception>
  public LhfReader(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    this._data = ms.ToArray();
    var d = this._data;
    if (!IsLhf(d))
      throw new InvalidDataException("LhF: missing LhF header.");
    this.UsedLength = BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(4));
    if (this.UsedLength > d.Length)
      throw new InvalidDataException("LhF: header claims more data than the file holds.");
    var end = (int)this.UsedLength;
    var entries = new List<LhfEntry>();
    var pos = 8;
    while (pos < end) {
      if (pos + EntryHeaderSize > end)
        throw new InvalidDataException($"LhF: truncated entry header at offset {pos}.");
      var h = d.AsSpan(pos, EntryHeaderSize);
      var hasComment = h[0] != 0;
      var protection = h[1];
      int nameLength = BinaryPrimitives.ReadUInt16BigEndian(h[2..]);
      var packed = BinaryPrimitives.ReadUInt32BigEndian(h[4..]);
      var size = BinaryPrimitives.ReadUInt32BigEndian(h[8..]);
      var date = BinaryPrimitives.ReadUInt32BigEndian(h[12..]);
      pos += EntryHeaderSize;
      if (pos + nameLength > end || packed > (uint)(end - pos - nameLength))
        throw new InvalidDataException($"LhF: entry at offset {pos - EntryHeaderSize} runs past the used length.");
      var field = d.AsSpan(pos, nameLength);
      var nul = field.IndexOf((byte)0);
      var name = Encoding.Latin1.GetString(nul < 0 ? field : field[..nul]);
      string? comment = null;
      if (hasComment && nul >= 0) {
        var rest = field[(nul + 1)..];
        var nul2 = rest.IndexOf((byte)0);
        comment = Encoding.Latin1.GetString(nul2 < 0 ? rest : rest[..nul2]);
      }
      pos += nameLength;
      var isDirectory = size == 0xFFFFFFFF;
      var isStored = !isDirectory && size == 0;
      entries.Add(new(name, comment, protection, isDirectory ? 0 : isStored ? packed : size, packed,
        isStored, isDirectory, date, pos));
      pos += (int)packed;
    }
    this.Entries = entries;
  }

  /// <summary>Returns the unpacked bytes of <paramref name="entry"/>.</summary>
  /// <exception cref="InvalidDataException">The packed data is damaged.</exception>
  public byte[] Extract(LhfEntry entry) {
    if (entry.IsDirectory)
      return [];
    var data = this._data.AsSpan(entry.DataOffset, (int)entry.StoredSize);
    return entry.IsStored ? data.ToArray() : LhfDecoder.Decode(data, checked((int)entry.Size));
  }

  /// <summary>Decodes the packed date word (seconds, minutes, hours, day, month, year).</summary>
  public static DateTime? DecodeDate(uint value) {
    var second = (int)(value & 63); value >>= 6;
    var minute = (int)(value & 63); value >>= 6;
    var hour = (int)(value & 31); value >>= 5;
    var day = (int)(value & 31); value >>= 5;
    var month = (int)(value & 15); value >>= 4;
    var year = 1976 + (int)value;
    try {
      return new DateTime(year, month, day, hour, minute, second, DateTimeKind.Unspecified);
    } catch (ArgumentOutOfRangeException) {
      return null;
    }
  }
}
