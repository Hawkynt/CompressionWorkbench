using System.Buffers.Binary;
using System.Text;

namespace FileFormat.StuffIt;

/// <summary>
/// Segmented ("split") StuffIt files: one file — typically a classic or StuffIt 5 archive, with
/// its resource fork — cut into parts, each opening with a 100-byte header.
/// </summary>
/// <remarks>
/// <para>
/// Header: <c>B0 56 00</c>, the part number (1-255), the file name as a Pascal string (at most 63
/// bytes, from byte 4), then from byte 68 the type and creator codes, the Finder flags (16 bits),
/// creation and modification times (seconds since 1904) and the resource and data fork lengths,
/// all big-endian; bytes 94-99 are zero. The parts' contents after their headers, joined in part
/// order, are the resource fork followed by the data fork. Parts belong together when their name
/// and the 26 bytes from offset 68 agree.
/// </para>
/// <para>The layout is the one XADMaster's <c>XADStuffItSplitParser.m</c> reads (LGPL-2.1-or-later,
/// see THIRD-PARTY-NOTICE.XADMaster.txt). No segmented file written by StuffIt was available: what
/// this class writes is verified by The Unarchiver extracting it, not against a real sample.</para>
/// </remarks>
public static class StuffItSegments {
  /// <summary>Size of the header that opens every part.</summary>
  public const int HeaderSize = 100;
  private const int MaxParts = 255;
  private static readonly DateTime MacEpoch = new(1904, 1, 1, 0, 0, 0, DateTimeKind.Utc);

  /// <summary>Whether <paramref name="head"/> (at least 100 bytes) opens a part of a segmented file.</summary>
  public static bool IsSegment(ReadOnlySpan<byte> head) {
    if (head.Length < HeaderSize || head[0] != 0xB0 || head[1] != 0x56 || head[2] != 0 || head[3] == 0) return false;
    var nameLength = head[4];
    if (nameLength is 0 or > 63) return false;
    foreach (var b in head.Slice(5, nameLength)) if (b == 0) return false;
    return true;
  }

  /// <summary>The name of the file a segmented set holds (header bytes 4-67).</summary>
  public static string FileName(ReadOnlySpan<byte> head) => Encoding.Latin1.GetString(head.Slice(5, head[4]));

  /// <summary>
  /// The file a segmented set holds, joined from all its parts: the data fork alone when it has no
  /// resource fork, otherwise a MacBinary file carrying both (which <see cref="StuffItReader"/> opens).
  /// </summary>
  /// <param name="opened">Any part; a file stream's directory is searched for the others.</param>
  /// <exception cref="FileNotFoundException">A part of the set is missing.</exception>
  public static byte[] Join(Stream opened) {
    ArgumentNullException.ThrowIfNull(opened);
    var head = ReadPart(opened);
    var name = FileName(head);
    var parts = new SortedDictionary<int, byte[]> { [head[3]] = head };
    string? path = null;
    if (opened is FileStream { Name: { Length: > 0 } openedName }) {
      path = Path.GetFullPath(openedName);
      var dir = Path.GetDirectoryName(path) ?? ".";
      var prefix = Path.GetFileNameWithoutExtension(path);
      foreach (var candidate in Directory.EnumerateFiles(dir)) {
        if (!Path.GetFileName(candidate).StartsWith(prefix, StringComparison.Ordinal)
            || string.Equals(Path.GetFullPath(candidate), path, StringComparison.OrdinalIgnoreCase)) continue;
        byte[] other;
        try {
          using var f = File.OpenRead(candidate);
          other = ReadPart(f);
        } catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException) {
          continue;
        }
        if (IsSegment(other) && SameSet(head, other)) parts.TryAdd(other[3], other);
      }
    }

    var resourceLength = BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(86));
    var dataLength = BinaryPrimitives.ReadUInt32BigEndian(head.AsSpan(90));
    if ((long)resourceLength + dataLength > int.MaxValue) throw new NotSupportedException("The segmented StuffIt file exceeds the 2 GiB in-memory limit.");
    var total = (long)resourceLength + dataLength;
    var content = new MemoryStream();
    for (var number = 1; content.Length < total; ++number) {
      if (!parts.TryGetValue(number, out var part))
        throw new FileNotFoundException($"Part {number} of the segmented StuffIt file '{name}' is missing"
          + (path is null ? "." : $" (it belongs beside '{Path.GetFileName(path)}', e.g. as '{PartPath(SetBase(path), number)}')."));
      content.Write(part, HeaderSize, part.Length - HeaderSize);
    }
    if (content.Length != total) throw new InvalidDataException($"The segmented StuffIt file '{name}' holds {content.Length} bytes where its header says {total}.");
    var joined = content.GetBuffer();
    var data = joined.AsSpan((int)resourceLength, (int)dataLength);
    if (resourceLength == 0) return data.ToArray();
    return MacBinary(name, head.AsSpan(68, 10), data, joined.AsSpan(0, (int)resourceLength));
  }

  private static byte[] ReadPart(Stream s) {
    if (s.CanSeek) s.Position = 0;
    using var ms = new MemoryStream();
    s.CopyTo(ms);
    var b = ms.ToArray();
    if (!IsSegment(b)) throw new InvalidDataException("Not a part of a segmented StuffIt file.");
    return b;
  }

  private static bool SameSet(byte[] a, byte[] b)
    => a.AsSpan(4, 1 + a[4]).SequenceEqual(b.AsSpan(4, 1 + b[4])) && a.AsSpan(68, 26).SequenceEqual(b.AsSpan(68, 26));

  /// <summary>A MacBinary file of the joined forks: what classic encrypted archives need to be read.</summary>
  private static byte[] MacBinary(string name, ReadOnlySpan<byte> typeCreatorFlags, ReadOnlySpan<byte> data, ReadOnlySpan<byte> resource) {
    var header = new byte[128];
    var nameBytes = Encoding.Latin1.GetBytes(name);
    header[1] = (byte)nameBytes.Length;
    nameBytes.CopyTo(header, 2);
    typeCreatorFlags[..8].CopyTo(header.AsSpan(65));
    header[73] = typeCreatorFlags[8]; // Finder flags, high byte
    BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(83), (uint)data.Length);
    BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(87), (uint)resource.Length);
    var ms = new MemoryStream();
    ms.Write(header);
    ms.Write(data);
    ms.Write(new byte[(128 - data.Length % 128) % 128]);
    ms.Write(resource);
    ms.Write(new byte[(128 - resource.Length % 128) % 128]);
    return ms.ToArray();
  }

  /// <summary>The set's name a part path belongs to: the path with a trailing ".1" (or any part number) removed.</summary>
  public static string SetBase(string path) {
    var ext = Path.GetExtension(path);
    return ext.Length > 1 && ext[1..].All(char.IsAsciiDigit) ? path[..^ext.Length] : path;
  }

  /// <summary>Where part <paramref name="number"/> (2 and up) of the set <paramref name="setBase"/> is written: <c>setBase.N</c>.</summary>
  public static string PartPath(string setBase, int number) => $"{setBase}.{number}";

  /// <summary>
  /// Cuts <paramref name="file"/> (an archive, or a MacBinary file with a resource fork) into parts
  /// of <paramref name="partSize"/> bytes, header included: part 1 to <paramref name="first"/>,
  /// the others to <paramref name="openPart"/>.
  /// </summary>
  /// <param name="file">The file to segment.</param>
  /// <param name="name">The file name the headers carry (at most 63 bytes).</param>
  /// <param name="partSize">The size of every part but the last (more than 100).</param>
  /// <param name="first">Receives part 1.</param>
  /// <param name="openPart">Opens part n (2, 3, …); the stream is disposed after it.</param>
  /// <param name="modified">The time the headers carry.</param>
  public static void Write(ReadOnlySpan<byte> file, string name, long partSize, Stream first, Func<int, Stream>? openPart, DateTime? modified = null) {
    ArgumentNullException.ThrowIfNull(first);
    if (partSize <= HeaderSize) throw new ArgumentOutOfRangeException(nameof(partSize), $"A StuffIt part holds its {HeaderSize}-byte header and at least one byte.");
    var nameBytes = Encoding.Latin1.GetBytes(name);
    if (nameBytes.Length is 0 or > 63) throw new ArgumentException("A segmented StuffIt file's name is 1-63 bytes.", nameof(name));

    // A MacBinary file (as a password-protected classic archive is written) gives both forks.
    ReadOnlySpan<byte> data = file, resource = [];
    byte[] typeCreator = "SIT!SIT!"u8.ToArray();
    if (file.Length >= 128 && file[0] == 0 && file[1] is > 0 and <= 63 && file[74] == 0 && file[82] == 0) {
      var dl = BinaryPrimitives.ReadUInt32BigEndian(file[83..]);
      var rl = BinaryPrimitives.ReadUInt32BigEndian(file[87..]);
      var rAt = 128 + ((dl + 127L) & ~127L);
      if (128 + dl <= file.Length && rAt + rl <= file.Length) {
        data = file.Slice(128, (int)dl);
        resource = file.Slice((int)rAt, (int)rl);
        typeCreator = file.Slice(65, 8).ToArray();
      }
    }

    var content = new byte[resource.Length + data.Length];
    resource.CopyTo(content);
    data.CopyTo(content.AsSpan(resource.Length));
    var perPart = partSize - HeaderSize;
    var parts = Math.Max(1, (int)Math.Min(int.MaxValue, (content.Length + perPart - 1) / perPart));
    if (parts > MaxParts) throw new ArgumentException($"A segmented StuffIt file has at most {MaxParts} parts; {parts} would be needed — choose a larger part size.", nameof(partSize));

    var header = new byte[HeaderSize];
    header[0] = 0xB0;
    header[1] = 0x56;
    header[4] = (byte)nameBytes.Length;
    nameBytes.CopyTo(header, 5);
    typeCreator.CopyTo(header, 68);
    var seconds = (uint)Math.Clamp(((modified ?? DateTime.UtcNow).ToUniversalTime() - MacEpoch).TotalSeconds, 0, uint.MaxValue);
    BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(78), seconds);
    BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(82), seconds);
    BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(86), (uint)resource.Length);
    BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(90), (uint)data.Length);
    for (var number = 1; number <= parts; ++number) {
      header[3] = (byte)number;
      var offset = (long)(number - 1) * perPart;
      var slice = content.AsSpan((int)offset, (int)Math.Min(perPart, content.Length - offset));
      if (number == 1) {
        first.Write(header);
        first.Write(slice);
        continue;
      }
      if (openPart is null) throw new ArgumentException("A segmented StuffIt file of more than one part needs a way to open its other parts.", nameof(openPart));
      using var s = openPart(number);
      s.Write(header);
      s.Write(slice);
    }
  }
}
