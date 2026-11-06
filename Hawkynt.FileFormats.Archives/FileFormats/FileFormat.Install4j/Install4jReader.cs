using System.Buffers.Binary;
using System.Text;
using Compression.Core.Dictionary.Lzma;

namespace FileFormat.Install4j;

/// <summary>One data archive of an install4j installer.</summary>
/// <param name="Name">The archive's name (<c>0.dat</c>, ...), or <c>launcher.dat</c> for the launcher block.</param>
/// <param name="Offset">Offset of its stored bytes in the installer.</param>
/// <param name="StoredSize">Stored size.</param>
/// <param name="Size">Size once unpacked.</param>
/// <param name="IsLzma">Stored as an LZMA stream (5 property bytes, the 64-bit unpacked size, the data); otherwise as is.</param>
public sealed record Install4jEntry(string Name, long Offset, long StoredSize, long Size, bool IsLzma);

/// <summary>
/// install4j installers (ej-technologies): a native launcher followed by an overlay that starts
/// with <c>D5 13 E4 E8</c>, holds the launcher block (its settings and any bundled Java runtime)
/// up to <c>E8 E4 13 D5</c>, then a big-endian table of data archives (name, size) and the
/// archives themselves, each a ZIP stored as is or LZMA-compressed.
/// </summary>
/// <remarks>
/// Converted from the <c>inst4j</c> module of Observer (lazyhamster, LGPL-3.0-or-later), revision
/// <c>ddbe6b6d</c>; see THIRD-PARTY-NOTICE.Observer.txt. Deviations: the end marker is accepted only
/// where the archive table after it is consistent (sane names, archives that end within the
/// overlay, no further than 64 KiB before its end), since the launcher block can contain the
/// marker's bytes by chance; the launcher block itself is listed as <c>launcher.dat</c>; and an
/// LZMA archive must unpack to exactly the size its header gives.
/// </remarks>
public sealed class Install4jReader {
  private static ReadOnlySpan<byte> StartMagic => [0xD5, 0x13, 0xE4, 0xE8];
  private static ReadOnlySpan<byte> EndMagic => [0xE8, 0xE4, 0x13, 0xD5];

  /// <summary>Most data an archive table may leave unaccounted for before the overlay ends.</summary>
  private const int MaxTrailingSlack = 64 * 1024;
  private const int MaxArchives = 100;

  private readonly byte[] _data;

  private Install4jReader(byte[] data, List<Install4jEntry> entries) {
    this._data = data;
    this.Entries = entries;
  }

  /// <summary>The launcher block and the data archives.</summary>
  public IReadOnlyList<Install4jEntry> Entries { get; }

  /// <summary>Whether the PE overlay starts with the install4j start marker.</summary>
  public static bool IsInstall4j(Stream stream) {
    try {
      var (start, _) = Overlay(stream);
      if (start <= 0 || start + 4 > stream.Length)
        return false;
      Span<byte> head = stackalloc byte[4];
      stream.Position = start;
      stream.ReadExactly(head);
      return head.SequenceEqual(StartMagic);
    } catch (Exception e) when (e is IOException or EndOfStreamException or ArgumentException) {
      return false;
    }
  }

  /// <summary>Opens an install4j installer held in memory.</summary>
  /// <exception cref="InvalidDataException">No start marker after the launcher, or no consistent archive table.</exception>
  public static Install4jReader Open(byte[] data) {
    using var stream = new MemoryStream(data, writable: false);
    var (start, end) = Overlay(stream);
    if (start <= 0 || start + 4 > end || !data.AsSpan((int)start, 4).SequenceEqual(StartMagic))
      throw new InvalidDataException("no install4j start marker where the launcher ends");

    for (var at = (int)start + 4; at < end;) {
      var hit = data.AsSpan(at, (int)(end - at)).IndexOf(EndMagic);
      if (hit < 0)
        break;
      var marker = at + hit;
      if (TryTable(data, marker + 4, end) is { } archives) {
        var entries = new List<Install4jEntry> { new("launcher.dat", start + 4, marker - (start + 4), marker - (start + 4), false) };
        entries.AddRange(archives);
        return new(data, entries);
      }
      at = marker + 1;
    }
    throw new InvalidDataException("no consistent install4j archive table after the launcher block");
  }

  private static List<Install4jEntry>? TryTable(byte[] data, long p, long end) {
    if (p + 4 > end)
      return null;
    var count = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan((int)p));
    if (count is 0 or > MaxArchives)
      return null;
    p += 4;
    var table = new List<(string Name, long Size)>();
    for (var i = 0; i < count; ++i) {
      if (p + 2 > end)
        return null;
      int nameLength = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan((int)p));
      p += 2;
      if (nameLength is 0 or > 260 || p + nameLength + 8 > end)
        return null;
      var raw = data.AsSpan((int)p, nameLength);
      foreach (var b in raw)
        if (b < 0x20 || b is (byte)'/' or (byte)'\\')
          return null;
      var name = Encoding.UTF8.GetString(raw);
      p += nameLength + 4; // name, a reserved word
      long size = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan((int)p));
      p += 4;
      table.Add((name, size));
    }

    var archives = new List<Install4jEntry>();
    var offset = p;
    foreach (var (name, size) in table) {
      if (offset + size > end)
        return null;
      var lzma = size >= 2 && !(data[offset] == 'P' && data[offset + 1] == 'K');
      long unpacked = size;
      if (lzma) {
        if (size < 13 || data[offset] > 224)
          return null;
        unpacked = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan((int)offset + 5));
        if (unpacked < 0)
          return null;
      }
      archives.Add(new(name, offset, size, unpacked, lzma));
      offset += size;
    }
    return end - offset <= MaxTrailingSlack ? archives : null;
  }

  /// <summary>Unpacks one entry.</summary>
  /// <exception cref="InvalidDataException">An LZMA archive does not unpack to the size its header gives.</exception>
  public byte[] Extract(Install4jEntry entry) {
    var stored = this._data.AsSpan((int)entry.Offset, (int)entry.StoredSize);
    if (!entry.IsLzma)
      return stored.ToArray();
    using var input = new MemoryStream(this._data, (int)entry.Offset + 13, (int)entry.StoredSize - 13, writable: false);
    byte[] content;
    try {
      content = new LzmaDecoder(input, stored[..5].ToArray(), entry.Size).Decode();
    } catch (Exception e) when (e is EndOfStreamException or IndexOutOfRangeException or ArgumentException) {
      throw new InvalidDataException($"{entry.Name}: the LZMA stream is damaged", e);
    }
    if (content.LongLength != entry.Size)
      throw new InvalidDataException($"{entry.Name} unpacks to {content.LongLength} bytes, not {entry.Size}");
    return content;
  }

  /// <summary>Start of the PE overlay and where it ends (the certificate table, else the end of the file).</summary>
  private static (long Start, long End) Overlay(Stream stream) {
    Span<byte> b = stackalloc byte[64];
    if (stream.Length < 64)
      return (-1, -1);
    stream.Position = 0;
    stream.ReadExactly(b);
    if (b[0] != 'M' || b[1] != 'Z')
      return (-1, -1);
    long pe = BinaryPrimitives.ReadUInt32LittleEndian(b[0x3C..]);
    if (pe + 24 > stream.Length)
      return (-1, -1);
    Span<byte> h = stackalloc byte[24];
    stream.Position = pe;
    stream.ReadExactly(h);
    if (h[0] != 'P' || h[1] != 'E' || h[2] != 0 || h[3] != 0)
      return (-1, -1);
    int sections = BinaryPrimitives.ReadUInt16LittleEndian(h[6..]);
    int optional = BinaryPrimitives.ReadUInt16LittleEndian(h[20..]);
    long end = 0;
    Span<byte> s = stackalloc byte[40];
    for (var i = 0; i < sections; ++i) {
      var at = pe + 24 + optional + 40L * i;
      if (at + 40 > stream.Length)
        return (-1, -1);
      stream.Position = at;
      stream.ReadExactly(s);
      end = Math.Max(end, BinaryPrimitives.ReadUInt32LittleEndian(s[20..]) + (long)BinaryPrimitives.ReadUInt32LittleEndian(s[16..]));
    }

    var limit = stream.Length;
    Span<byte> magic = stackalloc byte[2];
    stream.Position = pe + 24;
    stream.ReadExactly(magic);
    var directories = pe + 24 + (BinaryPrimitives.ReadUInt16LittleEndian(magic) == 0x20B ? 112 : 96);
    if (directories + 40 <= pe + 24 + optional) {
      Span<byte> security = stackalloc byte[8];
      stream.Position = directories + 32;
      stream.ReadExactly(security);
      var certificate = BinaryPrimitives.ReadUInt32LittleEndian(security);
      if (certificate > end && certificate <= stream.Length)
        limit = certificate;
    }
    return (end, limit);
  }
}
