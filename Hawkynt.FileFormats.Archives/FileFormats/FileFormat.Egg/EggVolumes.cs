using System.Buffers.Binary;

namespace FileFormat.Egg;

/// <summary>
/// EGG split sets: <c>name.vol1.egg</c>, <c>name.vol2.egg</c>, …. Each volume has its own EGG header
/// (magic, version, header id, reserved) whose split field names the previous and next volume's
/// header id (0 at either end). The archive is the first volume whole, followed by each later volume
/// after its header's end marker, so members and blocks run across volumes. Measured on split sets
/// ALZip 12.38 wrote.
/// </summary>
public static class EggVolumes {
  private const int FixedHeader = 14; // magic, version, header id, reserved

  /// <summary>Header id, previous and next volume id, and where the volume's payload starts.</summary>
  internal readonly record struct Header(uint Id, uint Previous, uint Next, bool IsSplit, int PayloadStart);

  internal static Header ReadHeader(ReadOnlySpan<byte> volume) {
    if (volume.Length < FixedHeader + 4 || BinaryPrimitives.ReadUInt32LittleEndian(volume) != EggReader.EggMagic)
      throw new InvalidDataException("EGG volume has no EGGA header.");
    var id = BinaryPrimitives.ReadUInt32LittleEndian(volume[6..]);
    uint previous = 0, next = 0;
    var split = false;
    var at = FixedHeader;
    while (true) {
      if (at + 4 > volume.Length) throw new InvalidDataException("EGG volume header has no end marker.");
      var magic = BinaryPrimitives.ReadUInt32LittleEndian(volume[at..]);
      at += 4;
      if (magic == EggReader.EndMarker) break;
      if (at + 1 > volume.Length) throw new InvalidDataException("EGG volume header is truncated.");
      var flags = volume[at++];
      int size;
      if ((flags & 1) != 0) {
        size = (int)BinaryPrimitives.ReadUInt32LittleEndian(volume[at..]);
        at += 4;
      } else {
        size = BinaryPrimitives.ReadUInt16LittleEndian(volume[at..]);
        at += 2;
      }
      if (size < 0 || at + size > volume.Length) throw new InvalidDataException("EGG volume header field runs past the volume.");
      if (magic == EggReader.SplitMagic && size >= 8) {
        split = true;
        previous = BinaryPrimitives.ReadUInt32LittleEndian(volume[at..]);
        next = BinaryPrimitives.ReadUInt32LittleEndian(volume[(at + 4)..]);
      }
      at += size;
    }
    return new(id, previous, next, split, at);
  }

  /// <summary>Joins the volumes of a set, in order, into the one archive they hold.</summary>
  /// <exception cref="InvalidDataException">A volume is not EGG, the chain of volume ids is broken, or the set is incomplete.</exception>
  public static byte[] Join(IReadOnlyList<byte[]> volumes) {
    ArgumentNullException.ThrowIfNull(volumes);
    if (volumes.Count == 0) throw new InvalidDataException("EGG: no volumes.");
    using var joined = new MemoryStream();
    Header? before = null;
    for (var i = 0; i < volumes.Count; ++i) {
      var h = ReadHeader(volumes[i]);
      if (!h.IsSplit) throw new InvalidDataException($"EGG volume {i + 1} is not part of a split set.");
      if (before is { } b ? h.Previous != b.Id || b.Next != h.Id : h.Previous != 0)
        throw new InvalidDataException($"EGG volume {i + 1} does not follow volume {i}.");
      if (i == volumes.Count - 1 && h.Next != 0) throw new InvalidDataException($"EGG volume {i + 2} of the split set is missing.");
      if (i == 0) joined.Write(volumes[i]);
      else joined.Write(volumes[i], h.PayloadStart, volumes[i].Length - h.PayloadStart);
      before = h;
    }
    return joined.ToArray();
  }

  /// <summary>
  /// Cuts a complete single-volume archive (as <see cref="EggWriter"/> writes it: header, end marker,
  /// members) into a split set of volumes of at most <paramref name="volumeSize"/> bytes. Volume 1 keeps
  /// the archive's header id; later ids follow from it. Each volume gets a header with the split field
  /// naming its neighbours, as <see cref="Join"/> reads them back.
  /// </summary>
  public static List<byte[]> Split(byte[] archive, long volumeSize) {
    ArgumentNullException.ThrowIfNull(archive);
    const int SplitHeader = FixedHeader + 4 + 1 + 2 + 8 + 4;
    if (volumeSize <= SplitHeader) throw new ArgumentOutOfRangeException(nameof(volumeSize), "An EGG volume must hold its header and some data.");
    var h = ReadHeader(archive);
    if (h.IsSplit) throw new ArgumentException("The archive is already a volume of a split set.", nameof(archive));
    if (archive.Length <= volumeSize) return [archive];
    var body = archive.AsSpan(h.PayloadStart);
    var capacity = (int)Math.Min(volumeSize - SplitHeader, int.MaxValue);
    var count = Math.Max(1, (body.Length + capacity - 1) / capacity);
    var ids = new uint[count];
    for (var i = 0; i < count; ++i) {
      ids[i] = unchecked(h.Id + (uint)i * 0x9E3779B9u);
      if (ids[i] == 0) ids[i] = 0x9E3779B9u;
    }
    var version = BinaryPrimitives.ReadUInt16LittleEndian(archive.AsSpan(4));
    var volumes = new List<byte[]>(count);
    for (var i = 0; i < count; ++i) {
      using var v = new MemoryStream();
      var w = new BinaryWriter(v);
      w.Write(EggReader.EggMagic);
      w.Write(version);
      w.Write(ids[i]);
      w.Write(0u);
      w.Write(EggReader.SplitMagic);
      w.Write((byte)0);
      w.Write((ushort)8);
      w.Write(i == 0 ? 0u : ids[i - 1]);
      w.Write(i == count - 1 ? 0u : ids[i + 1]);
      w.Write(EggReader.EndMarker);
      var start = i * capacity;
      w.Write(body.Slice(start, Math.Min(capacity, body.Length - start)));
      w.Flush();
      volumes.Add(v.ToArray());
    }
    return volumes;
  }

  /// <summary>Path of volume <paramref name="number"/> (from 1) of the set based at <paramref name="setBase"/>.</summary>
  public static string VolumePath(string setBase, int number) => $"{setBase}.vol{number}.egg";

  /// <summary>The set base of a volume path (<c>dir/name</c> for <c>dir/name.vol3.egg</c>), or null.</summary>
  internal static string? SetBase(string path) {
    var file = Path.GetFileName(path);
    var m = System.Text.RegularExpressions.Regex.Match(file, @"^(.*)\.vol\d+\.egg$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    return m.Success ? Path.Combine(Path.GetDirectoryName(path) ?? "", m.Groups[1].Value) : null;
  }

  /// <summary>Reads the whole set a file stream named <c>name.volN.egg</c> belongs to; null for anything else.</summary>
  internal static byte[]? JoinSiblings(Stream stream) {
    if (stream is not FileStream { Name: { Length: > 0 } name } || SetBase(name) is not { } setBase) return null;
    var volumes = new List<byte[]>();
    while (true) {
      var path = $"{setBase}.vol{volumes.Count + 1}.egg";
      if (!File.Exists(path)) throw new FileNotFoundException($"EGG: volume {volumes.Count + 1} of the split set is missing.", path);
      volumes.Add(File.ReadAllBytes(path));
      if (ReadHeader(volumes[^1]).Next == 0) break;
    }
    return Join(volumes);
  }
}
