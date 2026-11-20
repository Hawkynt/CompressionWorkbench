using System.Buffers.Binary;

namespace FileFormat.AlZip;

/// <summary>
/// ALZip split sets: <c>name.alz</c>, <c>name.a00</c>, <c>name.a01</c>, …. Every volume starts with
/// the 8-byte archive header, whose bytes 6–7 hold the volume number (0 for <c>.alz</c>). Every volume
/// but the last ends with the 16-byte record <c>CLZ\x01</c>, eight zero bytes, <c>CLZ\x03</c>; the last
/// ends with the ordinary end records. The archive is the first volume up to its trailer followed by
/// each later volume without its header (and without its trailer, except the last), so members may
/// run across volumes. Measured on split sets ALZip 12.38 wrote.
/// </summary>
public static class AlZipVolumes {
  private const int HeaderLength = 8;
  private const int TrailerLength = 16;

  /// <summary>Whether <paramref name="volume"/> is a volume that continues in another one.</summary>
  public static bool ContinuesInNextVolume(ReadOnlySpan<byte> volume) =>
    volume.Length >= HeaderLength + TrailerLength && volume[..4].SequenceEqual(AlZipReader.Magic)
    && BinaryPrimitives.ReadUInt32LittleEndian(volume[^TrailerLength..]) == AlZipReader.CentralSig
    && BinaryPrimitives.ReadUInt32LittleEndian(volume[^4..]) == AlZipReader.SplitSig;

  /// <summary>Joins the volumes of a set, in order, into the one archive they hold.</summary>
  /// <exception cref="InvalidDataException">A volume is not ALZip, is out of order, or the set is incomplete.</exception>
  public static byte[] Join(IReadOnlyList<byte[]> volumes) {
    ArgumentNullException.ThrowIfNull(volumes);
    if (volumes.Count == 0) throw new InvalidDataException("ALZip: no volumes.");
    using var joined = new MemoryStream();
    for (var i = 0; i < volumes.Count; ++i) {
      var v = volumes[i];
      if (v.Length < HeaderLength || !v.AsSpan(0, 4).SequenceEqual(AlZipReader.Magic))
        throw new InvalidDataException($"ALZip volume {i} has no ALZ header.");
      var number = BinaryPrimitives.ReadUInt16LittleEndian(v.AsSpan(6));
      if (number != i) throw new InvalidDataException($"ALZip volume {i} declares itself volume {number}.");
      var last = i == volumes.Count - 1;
      if (ContinuesInNextVolume(v) == last)
        throw new InvalidDataException(last
          ? $"ALZip volume {i} continues in a volume that is missing."
          : $"ALZip volume {i} ends the archive, yet more volumes follow.");
      var start = i == 0 ? 0 : HeaderLength;
      var end = last ? v.Length : v.Length - TrailerLength;
      joined.Write(v, start, end - start);
    }
    return joined.ToArray();
  }

  /// <summary>
  /// Cuts a complete archive into a split set of volumes of at most <paramref name="volumeSize"/> bytes,
  /// laid out as <see cref="Join"/> reads them back; the last volume keeps the whole end record.
  /// An archive that fits one volume stays one.
  /// </summary>
  public static List<byte[]> Split(byte[] archive, long volumeSize) {
    ArgumentNullException.ThrowIfNull(archive);
    if (volumeSize < HeaderLength + TrailerLength * 2 + 1)
      throw new ArgumentOutOfRangeException(nameof(volumeSize), "An ALZip volume must hold its header, a trailer and some data.");
    if (archive.Length < HeaderLength + TrailerLength || !archive.AsSpan(0, 4).SequenceEqual(AlZipReader.Magic)
        || BinaryPrimitives.ReadUInt32LittleEndian(archive.AsSpan(archive.Length - 4)) != AlZipReader.EndSig)
      throw new ArgumentException("Not a complete single-volume ALZip archive.", nameof(archive));
    if (archive.Length <= volumeSize) return [archive];
    var volumes = new List<byte[]>();
    var at = 0;
    while (true) {
      var number = volumes.Count;
      var header = number == 0 ? 0 : HeaderLength;
      var rest = archive.Length - at;
      if (header + rest <= volumeSize) {
        volumes.Add(Volume(number, archive.AsSpan(at), trailer: false, archive));
        return volumes;
      }
      // leave the 16-byte end record whole for the last volume
      var take = (int)Math.Min(volumeSize - header - TrailerLength, rest - TrailerLength);
      volumes.Add(Volume(number, archive.AsSpan(at, take), trailer: true, archive));
      at += take;
    }
  }

  private static byte[] Volume(int number, ReadOnlySpan<byte> content, bool trailer, byte[] archive) {
    using var v = new MemoryStream();
    if (number > 0) {
      Span<byte> header = stackalloc byte[HeaderLength];
      archive.AsSpan(0, 6).CopyTo(header);
      BinaryPrimitives.WriteUInt16LittleEndian(header[6..], checked((ushort)number));
      v.Write(header);
    }
    v.Write(content);
    if (trailer) {
      Span<byte> t = stackalloc byte[TrailerLength];
      t.Clear();
      BinaryPrimitives.WriteUInt32LittleEndian(t, AlZipReader.CentralSig);
      BinaryPrimitives.WriteUInt32LittleEndian(t[12..], AlZipReader.SplitSig);
      v.Write(t);
    }
    return v.ToArray();
  }

  /// <summary>The path of volume <paramref name="number"/> of the set whose first volume is <paramref name="firstVolume"/>.</summary>
  public static string VolumePath(string firstVolume, int number) =>
    number == 0 ? firstVolume : Path.ChangeExtension(firstVolume, $".a{number - 1:00}");

  /// <summary>
  /// Reads the whole set a file stream belongs to: the stream itself when it is not split, else every
  /// volume beside it starting from the <c>.alz</c>. Null when the stream is not a split volume.
  /// </summary>
  internal static byte[]? JoinSiblings(Stream stream) {
    if (stream is not FileStream { Name: { Length: > 0 } name }) return null;
    var ext = Path.GetExtension(name);
    var isFirst = ext.Equals(".alz", StringComparison.OrdinalIgnoreCase);
    var isLater = ext.Length == 4 && char.ToLowerInvariant(ext[1]) == 'a' && char.IsAsciiDigit(ext[2]) && char.IsAsciiDigit(ext[3]);
    if (!isFirst && !isLater) return null;
    var first = isFirst ? name : Path.ChangeExtension(name, ".alz");
    if (!File.Exists(first)) throw new FileNotFoundException("ALZip: the first volume of the split set is missing.", first);
    var volumes = new List<byte[]> { File.ReadAllBytes(first) };
    if (!ContinuesInNextVolume(volumes[0])) return null;
    while (ContinuesInNextVolume(volumes[^1])) {
      var next = VolumePath(first, volumes.Count);
      if (!File.Exists(next)) throw new FileNotFoundException($"ALZip: volume {volumes.Count} of the split set is missing.", next);
      volumes.Add(File.ReadAllBytes(next));
    }
    return Join(volumes);
  }
}
