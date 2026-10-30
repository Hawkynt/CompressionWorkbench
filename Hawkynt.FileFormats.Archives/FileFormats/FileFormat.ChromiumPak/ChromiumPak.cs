#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileFormat.ChromiumPak;

/// <summary>How a pack declares its text resources to be encoded (resources themselves are opaque bytes).</summary>
public enum ChromiumPakEncoding : byte {
  /// <summary>Binary resources (images, compressed HTML/JS).</summary>
  Binary = 0,
  /// <summary>UTF-8 strings (locale packs).</summary>
  Utf8 = 1,
  /// <summary>UTF-16 strings.</summary>
  Utf16 = 2,
}

/// <summary>One resource of a pack. <see cref="AliasOf"/> names the resource whose bytes it shares (version 5).</summary>
public sealed record ChromiumPakResource(ushort Id, byte[] Data, ushort? AliasOf);

/// <summary>
/// Chromium/Electron resource pack (<c>.pak</c>): resource ids mapped to byte blobs.
/// </summary>
/// <remarks>
/// Layout (little-endian), as Chromium's grit <c>data_pack.py</c> reads and writes it
/// (BSD-3-Clause, see THIRD-PARTY-NOTICE.ChromiumPak.txt):
/// <code>
/// v4: UInt32 version=4  UInt32 count  Byte encoding
/// v5: UInt32 version=5  Byte encoding  Byte[3] padding  UInt16 count  UInt16 aliasCount
/// (count + 1) × { UInt16 id  UInt32 offset }    — sorted by id; the last entry only ends the data
/// aliasCount × { UInt16 id  UInt16 entryIndex } — v5 only; resources sharing an entry's bytes
/// data
/// </code>
/// A resource's bytes run from its offset to the next entry's.
/// </remarks>
public static class ChromiumPak {

  private const int EntrySize = 6;
  private const int AliasSize = 4;

  /// <summary>
  /// Whether <paramref name="head"/> (at least the first 18 bytes of a file of <paramref name="length"/>
  /// bytes) is structurally a version 4 or 5 pack: known version and encoding, zero padding, and a first
  /// resource that starts exactly where the tables end.
  /// </summary>
  public static bool IsPak(ReadOnlySpan<byte> head, long length) {
    if (head.Length < 12) return false;
    var version = BinaryPrimitives.ReadUInt32LittleEndian(head);
    long tables;
    if (version == 5) {
      if (head[4] > 2 || head[5] != 0 || head[6] != 0 || head[7] != 0) return false;
      tables = 12 + (BinaryPrimitives.ReadUInt16LittleEndian(head[8..]) + 1L) * EntrySize
        + BinaryPrimitives.ReadUInt16LittleEndian(head[10..]) * (long)AliasSize;
    } else if (version == 4) {
      if (head[8] > 2) return false;
      tables = 9 + (BinaryPrimitives.ReadUInt32LittleEndian(head[4..]) + 1L) * EntrySize;
    } else
      return false;
    var firstOffset = version == 5 ? 14 : 11;
    return tables <= length && head.Length >= firstOffset + 4
      && BinaryPrimitives.ReadUInt32LittleEndian(head[firstOffset..]) == tables;
  }

  /// <summary>Reads every resource of a pack, aliases included, in id order.</summary>
  public static (uint Version, ChromiumPakEncoding Encoding, IReadOnlyList<ChromiumPakResource> Resources) Read(ReadOnlySpan<byte> image) {
    if (image.Length < 9)
      throw new InvalidDataException("PAK: shorter than its header.");
    var version = BinaryPrimitives.ReadUInt32LittleEndian(image);
    int headerSize, count, aliasCount;
    byte encoding;
    switch (version) {
      case 4:
        headerSize = 9;
        var count4 = BinaryPrimitives.ReadUInt32LittleEndian(image[4..]);
        if (count4 > (uint)(image.Length / EntrySize)) throw new InvalidDataException($"PAK: {count4} resources cannot fit the file.");
        count = (int)count4;
        encoding = image[8];
        aliasCount = 0;
        break;
      case 5:
        if (image.Length < 12) throw new InvalidDataException("PAK: shorter than its header.");
        headerSize = 12;
        encoding = image[4];
        count = BinaryPrimitives.ReadUInt16LittleEndian(image[8..]);
        aliasCount = BinaryPrimitives.ReadUInt16LittleEndian(image[10..]);
        break;
      default:
        throw new InvalidDataException($"PAK: unknown version {version} (4 and 5 exist).");
    }
    if (encoding > 2)
      throw new InvalidDataException($"PAK: unknown text encoding {encoding}.");
    var aliasStart = headerSize + (count + 1L) * EntrySize;
    var dataStart = aliasStart + aliasCount * (long)AliasSize;
    if (dataStart > image.Length)
      throw new InvalidDataException("PAK: the resource tables overrun the file.");

    var resources = new List<ChromiumPakResource>(count + aliasCount);
    var entries = new (ushort Id, uint Offset)[count + 1];
    for (var i = 0; i <= count; ++i) {
      var e = image[(headerSize + i * EntrySize)..];
      entries[i] = (BinaryPrimitives.ReadUInt16LittleEndian(e), BinaryPrimitives.ReadUInt32LittleEndian(e[2..]));
    }
    for (var i = 0; i < count; ++i) {
      var (id, start) = entries[i];
      var end = entries[i + 1].Offset;
      if (start < dataStart || end < start || end > image.Length)
        throw new InvalidDataException($"PAK: resource {id} spans [{start}, {end}) outside the data.");
      if (i > 0 && id <= entries[i - 1].Id)
        throw new InvalidDataException($"PAK: resource ids are not ascending at {id}.");
      resources.Add(new ChromiumPakResource(id, image[(int)start..(int)end].ToArray(), null));
    }
    for (var i = 0; i < aliasCount; ++i) {
      var a = image[(int)(aliasStart + i * AliasSize)..];
      var id = BinaryPrimitives.ReadUInt16LittleEndian(a);
      var index = BinaryPrimitives.ReadUInt16LittleEndian(a[2..]);
      if (index >= count)
        throw new InvalidDataException($"PAK: alias {id} points at entry {index} of {count}.");
      resources.Add(new ChromiumPakResource(id, resources[index].Data, entries[index].Id));
    }
    resources.Sort((x, y) => x.Id.CompareTo(y.Id));
    for (var i = 1; i < resources.Count; ++i)
      if (resources[i].Id == resources[i - 1].Id)
        throw new InvalidDataException($"PAK: resource {resources[i].Id} is defined twice.");
    return (version, (ChromiumPakEncoding)encoding, resources);
  }

  /// <summary>
  /// Writes a pack as grit does: ids ascending; in version 5 a resource whose bytes equal a lower id's is
  /// stored once and aliased, version 4 (which has no alias table) stores every resource.
  /// </summary>
  public static void Write(Stream output, IReadOnlyDictionary<ushort, byte[]> resources, ChromiumPakEncoding encoding, uint version = 5) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(resources);
    if (version is not (4 or 5))
      throw new ArgumentOutOfRangeException(nameof(version), version, "PAK: versions 4 and 5 exist.");
    var ids = resources.Keys.Order().ToArray();
    if (version == 4) {
      WriteVersion4(output, resources, ids, encoding);
      return;
    }
    // The lowest id holding each distinct content owns it; every other id holding it is an alias.
    var owner = new Dictionary<byte[], ushort>(ByteArrayComparer.Instance);
    foreach (var id in ids) owner.TryAdd(resources[id], id);
    var stored = ids.Where(id => owner[resources[id]] == id).ToArray();
    var aliases = ids.Where(id => owner[resources[id]] != id).ToArray();
    if (stored.Length > ushort.MaxValue || aliases.Length > ushort.MaxValue)
      throw new ArgumentException("PAK: a version 5 pack holds at most 65535 resources and aliases.", nameof(resources));

    using var ms = new MemoryStream();
    Span<byte> b = stackalloc byte[12];
    BinaryPrimitives.WriteUInt32LittleEndian(b, 5);
    b[4] = (byte)encoding;
    b[5] = b[6] = b[7] = 0;
    BinaryPrimitives.WriteUInt16LittleEndian(b[8..], (ushort)stored.Length);
    BinaryPrimitives.WriteUInt16LittleEndian(b[10..], (ushort)aliases.Length);
    ms.Write(b);

    var offset = checked(12L + (stored.Length + 1L) * EntrySize + aliases.Length * (long)AliasSize);
    var indexOf = new Dictionary<ushort, ushort>();
    foreach (var id in stored) {
      indexOf[id] = (ushort)indexOf.Count;
      WriteEntry(ms, id, checked((uint)offset));
      offset += resources[id].Length;
    }
    WriteEntry(ms, 0, checked((uint)offset));
    Span<byte> a = stackalloc byte[AliasSize];
    foreach (var id in aliases) {
      BinaryPrimitives.WriteUInt16LittleEndian(a, id);
      BinaryPrimitives.WriteUInt16LittleEndian(a[2..], indexOf[owner[resources[id]]]);
      ms.Write(a);
    }
    foreach (var id in stored) ms.Write(resources[id]);
    ms.Position = 0;
    ms.CopyTo(output);
  }

  private static void WriteVersion4(Stream output, IReadOnlyDictionary<ushort, byte[]> resources, ushort[] ids, ChromiumPakEncoding encoding) {
    using var ms = new MemoryStream();
    Span<byte> b = stackalloc byte[9];
    BinaryPrimitives.WriteUInt32LittleEndian(b, 4);
    BinaryPrimitives.WriteUInt32LittleEndian(b[4..], (uint)ids.Length);
    b[8] = (byte)encoding;
    ms.Write(b);
    var offset = checked(9L + (ids.Length + 1L) * EntrySize);
    foreach (var id in ids) {
      WriteEntry(ms, id, checked((uint)offset));
      offset += resources[id].Length;
    }
    WriteEntry(ms, 0, checked((uint)offset));
    foreach (var id in ids) ms.Write(resources[id]);
    ms.Position = 0;
    ms.CopyTo(output);
  }

  private static void WriteEntry(Stream s, ushort id, uint offset) {
    Span<byte> e = stackalloc byte[EntrySize];
    BinaryPrimitives.WriteUInt16LittleEndian(e, id);
    BinaryPrimitives.WriteUInt32LittleEndian(e[2..], offset);
    s.Write(e);
  }

  private sealed class ByteArrayComparer : IEqualityComparer<byte[]> {
    public static readonly ByteArrayComparer Instance = new();
    public bool Equals(byte[]? x, byte[]? y) => x.AsSpan().SequenceEqual(y);
    public int GetHashCode(byte[] obj) {
      var h = new HashCode();
      h.AddBytes(obj);
      return h.ToHashCode();
    }
  }
}
