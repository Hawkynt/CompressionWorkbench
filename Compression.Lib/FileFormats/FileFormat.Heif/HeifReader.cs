#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using FileFormat.Mp4;

namespace FileFormat.Heif;

/// <summary>
/// Parses HEIF / HEIC (ISO/IEC 23008-12) and AVIF (AV1 Image File Format) files.
/// Both are ISOBMFF variants whose <c>meta</c> box drives everything: <c>pitm</c>
/// names the primary item, <c>iinf</c> carries per-item type info, <c>iloc</c>
/// gives file-offset extents, <c>iprp</c> groups item properties (decoder config
/// / dimensions / colour info). Only listing and bytewise extraction are implemented
/// — we do not decode the underlying HEVC/AV1 streams.
/// </summary>
public sealed class HeifReader {
  /// <summary>Marker brands for HEIC / HEIF.</summary>
  public static readonly string[] HeifBrands = ["heic", "heix", "heim", "heis", "hevc", "hevm", "hevs", "heif", "mif1", "msf1"];

  /// <summary>Marker brands for AVIF.</summary>
  public static readonly string[] AvifBrands = ["avif", "avis"];

  /// <summary>
  /// Represents an item info.
  /// </summary>
  public sealed record ItemInfo(uint Id, string Type, string? Name, string? ContentType);
  /// <summary>
  /// Represents an item extent.
  /// </summary>
  public sealed record ItemExtent(long Offset, long Length);
  /// <summary>
  /// Represents an item location.
  /// </summary>
  public sealed record ItemLocation(uint Id, long BaseOffset, IReadOnlyList<ItemExtent> Extents, uint ConstructionMethod);

  internal readonly record struct SourceRange(int Offset, int Length);
  internal sealed record StructuralLayout(
    string? MajorBrand,
    IReadOnlyList<string> CompatibleBrands,
    uint PrimaryItemId,
    IReadOnlyList<ItemInfo> Items,
    IReadOnlyList<ItemLocation> Locations);

  /// <summary>
  /// Gets the major brand.
  /// </summary>
  public string? MajorBrand { get; }
  /// <summary>
  /// Gets the compatible brands.
  /// </summary>
  public IReadOnlyList<string> CompatibleBrands { get; }
  /// <summary>
  /// Gets the primary item id.
  /// </summary>
  public uint PrimaryItemId { get; }
  /// <summary>
  /// Gets the items.
  /// </summary>
  public IReadOnlyList<ItemInfo> Items { get; }
  /// <summary>
  /// Gets the locations.
  /// </summary>
  public IReadOnlyList<ItemLocation> Locations { get; }
  /// <summary>
  /// Gets the item properties.
  /// </summary>
  public IReadOnlyDictionary<uint, byte[]> ItemProperties { get; }
  /// <summary>
  /// Gets the item property associations.
  /// </summary>
  public IReadOnlyList<(uint ItemId, IReadOnlyList<uint> PropertyIndexes)> ItemPropertyAssociations { get; }

  private readonly byte[] _data;

  internal static StructuralLayout ReadLayout(ReadOnlySpan<byte> data) {
    var parser = new BoxParser();
    var boxes = parser.Parse(data);

    var ftyp = BoxParser.Find(boxes, "ftyp")
      ?? throw new InvalidDataException("HEIF: missing 'ftyp'.");
    var (majorBrand, compatibleBrands) = ParseFtypLayout(data, ftyp);

    var meta = BoxParser.Find(boxes, "meta")
      ?? throw new InvalidDataException("HEIF: missing 'meta'.");
    var metaChildren = ParseMetaChildrenLayout(data, meta);

    var pitm = metaChildren.FirstOrDefault(static box => box.Type == "pitm");
    var primaryItemId = pitm != null ? ParsePitmLayout(data, pitm) : 0u;

    var iinf = metaChildren.FirstOrDefault(static box => box.Type == "iinf");
    var items = iinf != null ? ParseIinfLayout(data, iinf) : [];

    var iloc = metaChildren.FirstOrDefault(static box => box.Type == "iloc");
    var locations = iloc != null ? ParseIlocLayout(data, iloc) : [];

    return new StructuralLayout(
      majorBrand,
      compatibleBrands,
      primaryItemId,
      items,
      locations);
  }

  internal static bool MatchesAnyBrand(StructuralLayout layout, IEnumerable<string> brands) {
    var set = new HashSet<string>(brands, StringComparer.Ordinal);
    return layout.MajorBrand != null && set.Contains(layout.MajorBrand)
      || layout.CompatibleBrands.Any(set.Contains);
  }

  internal static IReadOnlyList<SourceRange> GetItemRanges(
      StructuralLayout layout, uint itemId, int sourceLength) {
    var location = layout.Locations.FirstOrDefault(item => item.Id == itemId);
    if (location == null || location.ConstructionMethod != 0)
      return [];

    var result = new List<SourceRange>(location.Extents.Count);
    foreach (var extent in location.Extents) {
      long start;
      try {
        start = checked(location.BaseOffset + extent.Offset);
      } catch (OverflowException) {
        continue;
      }

      if (start < 0 || extent.Length < 0 || start > sourceLength
          || extent.Length > sourceLength - start
          || start > int.MaxValue || extent.Length > int.MaxValue)
        continue;

      result.Add(new SourceRange((int)start, (int)extent.Length));
    }

    return result;
  }

  internal static IReadOnlyList<SourceRange> SkipPrefix(
      IReadOnlyList<SourceRange> ranges, int byteCount) {
    if (byteCount <= 0)
      return ranges;

    var total = 0L;
    foreach (var range in ranges)
      total += range.Length;
    if (total <= byteCount)
      return ranges;

    var result = new List<SourceRange>(ranges.Count);
    var remaining = byteCount;
    foreach (var range in ranges) {
      if (remaining >= range.Length) {
        remaining -= range.Length;
        continue;
      }

      var offset = range.Offset + remaining;
      var length = range.Length - remaining;
      remaining = 0;
      if (length > 0)
        result.Add(new SourceRange(offset, length));
    }

    return result;
  }

  internal static int GetTotalLength(IReadOnlyList<SourceRange> ranges) {
    var total = 0;
    foreach (var range in ranges)
      total = checked(total + range.Length);
    return total;
  }

  private static (string? Major, IReadOnlyList<string> Compatible) ParseFtypLayout(
      ReadOnlySpan<byte> data, BoxParser.Box ftyp) {
    if (ftyp.BodyLength < 8 || ftyp.BodyOffset < 0
        || ftyp.BodyOffset + ftyp.BodyLength > data.Length)
      return (null, []);

    var offset = (int)ftyp.BodyOffset;
    var major = Encoding.ASCII.GetString(data.Slice(offset, 4));
    var compatible = new List<string>();
    for (var pos = offset + 8; pos + 4 <= offset + (int)ftyp.BodyLength; pos += 4)
      compatible.Add(Encoding.ASCII.GetString(data.Slice(pos, 4)));
    return (major, compatible);
  }

  private static List<BoxParser.Box> ParseMetaChildrenLayout(
      ReadOnlySpan<byte> data, BoxParser.Box meta) {
    var start = checked((int)meta.BodyOffset + 4);
    var end = checked((int)(meta.BodyOffset + meta.BodyLength));
    if (start < 0 || end < start || end > data.Length)
      return [];

    var children = new BoxParser().Parse(data.Slice(start, end - start));
    return children.Select(child => Rebase(child, start)).ToList();
  }

  private static uint ParsePitmLayout(ReadOnlySpan<byte> data, BoxParser.Box pitm) {
    if (pitm.BodyLength < 6 || pitm.BodyOffset < 0
        || pitm.BodyOffset + pitm.BodyLength > data.Length)
      return 0;

    var offset = (int)pitm.BodyOffset;
    return data[offset] == 0
      ? BinaryPrimitives.ReadUInt16BigEndian(data[(offset + 4)..])
      : BinaryPrimitives.ReadUInt32BigEndian(data[(offset + 4)..]);
  }

  private static List<ItemInfo> ParseIinfLayout(ReadOnlySpan<byte> data, BoxParser.Box iinf) {
    var result = new List<ItemInfo>();
    if (iinf.BodyLength < 6 || iinf.BodyOffset < 0
        || iinf.BodyOffset + iinf.BodyLength > data.Length)
      return result;

    var offset = (int)iinf.BodyOffset;
    var end = (int)(iinf.BodyOffset + iinf.BodyLength);
    var version = data[offset];
    var position = offset + 4;

    uint count;
    if (version == 0) {
      if (position + 2 > end) return result;
      count = BinaryPrimitives.ReadUInt16BigEndian(data[position..]);
      position += 2;
    } else {
      if (position + 4 > end) return result;
      count = BinaryPrimitives.ReadUInt32BigEndian(data[position..]);
      position += 4;
    }

    for (uint index = 0; index < count && position + 8 <= end; ++index) {
      var size = (int)BinaryPrimitives.ReadUInt32BigEndian(data[position..]);
      if (size < 8 || position > end - size)
        break;

      if (data[position + 4] == 'i' && data[position + 5] == 'n'
          && data[position + 6] == 'f' && data[position + 7] == 'e') {
        var info = ParseInfeLayout(data, position + 8, position + size);
        if (info != null)
          result.Add(info);
      }

      position += size;
    }

    return result;
  }

  private static ItemInfo? ParseInfeLayout(ReadOnlySpan<byte> data, int bodyStart, int bodyEnd) {
    if (bodyStart < 0 || bodyEnd > data.Length || bodyStart + 4 > bodyEnd)
      return null;

    var version = data[bodyStart];
    var position = bodyStart + 4;
    uint id;
    string type;
    string? contentType = null;

    if (version <= 1) {
      if (position + 4 > bodyEnd) return null;
      id = BinaryPrimitives.ReadUInt16BigEndian(data[position..]);
      position += 4;
      var name = ReadCStringLayout(data, ref position, bodyEnd);
      var content = ReadCStringLayout(data, ref position, bodyEnd);
      return new ItemInfo(id, "", name, content);
    }

    if (version == 2) {
      if (position + 8 > bodyEnd) return null;
      id = BinaryPrimitives.ReadUInt16BigEndian(data[position..]);
      position += 4;
      type = Encoding.ASCII.GetString(data.Slice(position, 4));
      position += 4;
    } else {
      if (position + 10 > bodyEnd) return null;
      id = BinaryPrimitives.ReadUInt32BigEndian(data[position..]);
      position += 6;
      type = Encoding.ASCII.GetString(data.Slice(position, 4));
      position += 4;
    }

    var itemName = ReadCStringLayout(data, ref position, bodyEnd);
    if (type == "mime")
      contentType = ReadCStringLayout(data, ref position, bodyEnd);
    return new ItemInfo(id, type, itemName, contentType);
  }

  private static string? ReadCStringLayout(ReadOnlySpan<byte> data, ref int position, int end) {
    var start = position;
    while (position < end && data[position] != 0)
      ++position;
    var value = position > start
      ? Encoding.UTF8.GetString(data.Slice(start, position - start))
      : null;
    if (position < end)
      ++position;
    return value;
  }

  private static List<ItemLocation> ParseIlocLayout(ReadOnlySpan<byte> data, BoxParser.Box iloc) {
    var result = new List<ItemLocation>();
    if (iloc.BodyLength < 8 || iloc.BodyOffset < 0
        || iloc.BodyOffset + iloc.BodyLength > data.Length)
      return result;

    var offset = (int)iloc.BodyOffset;
    var end = (int)(iloc.BodyOffset + iloc.BodyLength);
    var version = data[offset];
    var position = offset + 4;
    if (position + 2 > end) return result;

    var firstSizes = data[position];
    var secondSizes = data[position + 1];
    var offsetSize = (firstSizes >> 4) & 0xF;
    var lengthSize = firstSizes & 0xF;
    var baseOffsetSize = (secondSizes >> 4) & 0xF;
    var indexSize = version >= 1 ? secondSizes & 0xF : 0;
    position += 2;

    uint itemCount;
    if (version < 2) {
      if (position + 2 > end) return result;
      itemCount = BinaryPrimitives.ReadUInt16BigEndian(data[position..]);
      position += 2;
    } else {
      if (position + 4 > end) return result;
      itemCount = BinaryPrimitives.ReadUInt32BigEndian(data[position..]);
      position += 4;
    }

    for (uint itemIndex = 0; itemIndex < itemCount && position < end; ++itemIndex) {
      uint id;
      if (version < 2) {
        if (position + 2 > end) break;
        id = BinaryPrimitives.ReadUInt16BigEndian(data[position..]);
        position += 2;
      } else {
        if (position + 4 > end) break;
        id = BinaryPrimitives.ReadUInt32BigEndian(data[position..]);
        position += 4;
      }

      uint constructionMethod = 0;
      if (version >= 1) {
        if (position + 2 > end) break;
        constructionMethod = (uint)(BinaryPrimitives.ReadUInt16BigEndian(data[position..]) & 0xF);
        position += 2;
      }

      if (position + 2 > end) break;
      position += 2;

      if (baseOffsetSize is not (0 or 1 or 2 or 4 or 8)
          || offsetSize is not (0 or 1 or 2 or 4 or 8)
          || lengthSize is not (0 or 1 or 2 or 4 or 8)
          || indexSize is not (0 or 1 or 2 or 4 or 8))
        break;

      if (position + baseOffsetSize > end) break;
      var baseOffset = ReadUIntLayout(data, position, baseOffsetSize);
      position += baseOffsetSize;

      if (position + 2 > end) break;
      var extentCount = BinaryPrimitives.ReadUInt16BigEndian(data[position..]);
      position += 2;

      var extents = new List<ItemExtent>(extentCount);
      for (var extentIndex = 0; extentIndex < extentCount && position < end; ++extentIndex) {
        if (version >= 1 && indexSize > 0) {
          if (position + indexSize > end) break;
          position += indexSize;
        }

        if (position + offsetSize + lengthSize > end)
          break;

        var extentOffset = ReadUIntLayout(data, position, offsetSize);
        position += offsetSize;
        var extentLength = ReadUIntLayout(data, position, lengthSize);
        position += lengthSize;
        extents.Add(new ItemExtent(extentOffset, extentLength));
      }

      result.Add(new ItemLocation(id, baseOffset, extents, constructionMethod));
    }

    return result;
  }

  private static long ReadUIntLayout(ReadOnlySpan<byte> data, int position, int size) => size switch {
    0 => 0,
    1 => data[position],
    2 => BinaryPrimitives.ReadUInt16BigEndian(data[position..]),
    4 => BinaryPrimitives.ReadUInt32BigEndian(data[position..]),
    8 => checked((long)BinaryPrimitives.ReadUInt64BigEndian(data[position..])),
    _ => throw new InvalidDataException($"HEIF: unsupported integer field width {size}."),
  };

  /// <summary>
  /// Initializes a new instance of <see cref="HeifReader"/>.
  /// </summary>
  public HeifReader(byte[] data) {
    this._data = data;
    var parser = new BoxParser();
    var boxes = parser.Parse(data);

    var ftyp = BoxParser.Find(boxes, "ftyp") ?? throw new InvalidDataException("HEIF: missing 'ftyp'.");
    (this.MajorBrand, this.CompatibleBrands) = ParseFtyp(data, ftyp);

    var meta = BoxParser.Find(boxes, "meta") ?? throw new InvalidDataException("HEIF: missing 'meta'.");
    // `meta` is a FullBox: 4 bytes (version+flags) then child boxes. BoxParser treats
    // meta as compound so its children include junk from the first 4 bytes — re-parse.
    var metaChildren = ParseMetaChildren(data, meta);

    var pitm = metaChildren.FirstOrDefault(b => b.Type == "pitm");
    this.PrimaryItemId = pitm != null ? ParsePitm(data, pitm) : 0u;

    var iinf = metaChildren.FirstOrDefault(b => b.Type == "iinf");
    this.Items = iinf != null ? ParseIinf(data, iinf) : Array.Empty<ItemInfo>();

    var iloc = metaChildren.FirstOrDefault(b => b.Type == "iloc");
    this.Locations = iloc != null ? ParseIloc(data, iloc) : Array.Empty<ItemLocation>();

    var iprp = metaChildren.FirstOrDefault(b => b.Type == "iprp");
    (this.ItemProperties, this.ItemPropertyAssociations) = iprp != null
      ? ParseIprp(data, iprp)
      : (new Dictionary<uint, byte[]>(), Array.Empty<(uint, IReadOnlyList<uint>)>());
  }

  /// <summary>Returns true when one of the given brands appears as major or compatible.</summary>
  public bool MatchesAnyBrand(IEnumerable<string> brands) {
    var set = new HashSet<string>(brands, StringComparer.Ordinal);
    if (this.MajorBrand != null && set.Contains(this.MajorBrand)) return true;
    return this.CompatibleBrands.Any(set.Contains);
  }

  /// <summary>Reads the raw bytes of an item by joining its extents (construction method 0 only).</summary>
  public byte[] ReadItem(uint itemId) {
    var loc = this.Locations.FirstOrDefault(l => l.Id == itemId);
    if (loc == null) return Array.Empty<byte>();
    // Construction method 1 = idat (item data), 2 = item reference; we only handle 0 (file offset).
    if (loc.ConstructionMethod != 0) return Array.Empty<byte>();
    using var ms = new MemoryStream();
    foreach (var ex in loc.Extents) {
      var start = checked((int)(loc.BaseOffset + ex.Offset));
      var len = checked((int)ex.Length);
      if (start < 0 || start + len > this._data.Length || len < 0) continue;
      ms.Write(this._data, start, len);
    }
    return ms.ToArray();
  }

  /// <summary>Returns the property boxes (e.g. <c>hvcC</c>, <c>ispe</c>, <c>av1C</c>) associated with an item.</summary>
  public IReadOnlyList<byte[]> GetPropertiesFor(uint itemId) {
    var assoc = this.ItemPropertyAssociations.FirstOrDefault(a => a.ItemId == itemId);
    if (assoc.PropertyIndexes == null) return Array.Empty<byte[]>();
    var result = new List<byte[]>();
    foreach (var idx in assoc.PropertyIndexes)
      if (this.ItemProperties.TryGetValue(idx, out var bytes))
        result.Add(bytes);
    return result;
  }

  private static (string? Major, IReadOnlyList<string> Compatible) ParseFtyp(byte[] data, BoxParser.Box ftyp) {
    if (ftyp.BodyLength < 8) return (null, Array.Empty<string>());
    var off = (int)ftyp.BodyOffset;
    var major = Encoding.ASCII.GetString(data, off, 4);
    // minor version at +4..+8, then 4-char compatible brands until end.
    var compat = new List<string>();
    for (var p = off + 8; p + 4 <= off + (int)ftyp.BodyLength; p += 4)
      compat.Add(Encoding.ASCII.GetString(data, p, 4));
    return (major, compat);
  }

  private static List<BoxParser.Box> ParseMetaChildren(byte[] data, BoxParser.Box meta) {
    // meta = FullBox: skip 1 version + 3 flags before child list.
    var start = (int)meta.BodyOffset + 4;
    var end = (int)(meta.BodyOffset + meta.BodyLength);
    var parser = new BoxParser();
    // Feed a slice as if it were the top-level stream.
    var slice = new byte[end - start];
    Buffer.BlockCopy(data, start, slice, 0, slice.Length);
    var children = parser.Parse(slice);
    // Re-base offsets onto the original file so extent reads work.
    return children.Select(c => Rebase(c, start)).ToList();
  }

  private static BoxParser.Box Rebase(BoxParser.Box box, long delta) =>
    new(box.Type, box.Offset + delta, box.Size, box.BodyOffset + delta, box.BodyLength,
        box.Children?.Select(c => Rebase(c, delta)).ToList());

  private static uint ParsePitm(byte[] data, BoxParser.Box pitm) {
    // FullBox header: 1 + 3 = 4 bytes. v0 → 2-byte item_id, v1+ → 4-byte.
    if (pitm.BodyLength < 6) return 0;
    var off = (int)pitm.BodyOffset;
    var version = data[off];
    if (version == 0)
      return BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(off + 4));
    return BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(off + 4));
  }

  private static List<ItemInfo> ParseIinf(byte[] data, BoxParser.Box iinf) {
    var result = new List<ItemInfo>();
    if (iinf.BodyLength < 6) return result;
    var off = (int)iinf.BodyOffset;
    var end = (int)(iinf.BodyOffset + iinf.BodyLength);
    var version = data[off];
    var pos = off + 4;
    uint count;
    if (version == 0) {
      count = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(pos));
      pos += 2;
    } else {
      count = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos));
      pos += 4;
    }
    // Children are 'infe' boxes.
    for (uint i = 0; i < count && pos + 8 <= end; i++) {
      var size = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos));
      var type = Encoding.ASCII.GetString(data, pos + 4, 4);
      if (size < 8 || pos + size > end) break;
      if (type == "infe") {
        var info = ParseInfe(data, pos + 8, pos + size);
        if (info != null) result.Add(info);
      }
      pos += size;
    }
    return result;
  }

  private static ItemInfo? ParseInfe(byte[] data, int bodyStart, int bodyEnd) {
    if (bodyStart + 4 > bodyEnd) return null;
    var v = data[bodyStart];
    var pos = bodyStart + 4; // past version + flags
    uint id;
    string type;
    string? contentType = null;
    if (v <= 1) {
      // v0/v1: item_ID (2), item_protection_index (2), item_name (cstr), content_type (cstr), content_encoding (cstr)
      if (pos + 4 > bodyEnd) return null;
      id = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(pos));
      pos += 4;
      var name = ReadCString(data, ref pos, bodyEnd);
      var ct = ReadCString(data, ref pos, bodyEnd);
      return new ItemInfo(id, "", name, ct);
    }
    // v2: item_ID (2 if v2, 4 if v3), item_protection_index (2), item_type (4), item_name (cstr), …
    if (v == 2) {
      if (pos + 8 > bodyEnd) return null;
      id = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(pos));
      pos += 4;
      type = Encoding.ASCII.GetString(data, pos, 4);
      pos += 4;
    } else {
      if (pos + 10 > bodyEnd) return null;
      id = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos));
      pos += 6;
      type = Encoding.ASCII.GetString(data, pos, 4);
      pos += 4;
    }
    var itemName = ReadCString(data, ref pos, bodyEnd);
    if (type == "mime")
      contentType = ReadCString(data, ref pos, bodyEnd);
    return new ItemInfo(id, type, itemName, contentType);
  }

  private static string? ReadCString(byte[] data, ref int pos, int end) {
    var start = pos;
    while (pos < end && data[pos] != 0) pos++;
    var s = pos > start ? Encoding.UTF8.GetString(data, start, pos - start) : null;
    if (pos < end) pos++;
    return s;
  }

  private static List<ItemLocation> ParseIloc(byte[] data, BoxParser.Box iloc) {
    var result = new List<ItemLocation>();
    if (iloc.BodyLength < 8) return result;
    var off = (int)iloc.BodyOffset;
    var end = (int)(iloc.BodyOffset + iloc.BodyLength);
    var version = data[off];
    var pos = off + 4; // past FullBox header
    if (pos + 2 > end) return result;
    // Byte layout: offset_size(4) | length_size(4) | base_offset_size(4) | index_size(4) [only v1+]
    var b1 = data[pos];
    var b2 = data[pos + 1];
    var offsetSize = (b1 >> 4) & 0xF;
    var lengthSize = b1 & 0xF;
    var baseOffsetSize = (b2 >> 4) & 0xF;
    var indexSize = version >= 1 ? (b2 & 0xF) : 0;
    pos += 2;
    uint itemCount;
    if (version < 2) {
      if (pos + 2 > end) return result;
      itemCount = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(pos));
      pos += 2;
    } else {
      if (pos + 4 > end) return result;
      itemCount = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos));
      pos += 4;
    }
    for (uint i = 0; i < itemCount && pos < end; i++) {
      uint id;
      if (version < 2) {
        id = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(pos));
        pos += 2;
      } else {
        id = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos));
        pos += 4;
      }
      uint constructionMethod = 0;
      if (version >= 1) {
        if (pos + 2 > end) break;
        constructionMethod = (uint)(BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(pos)) & 0xF);
        pos += 2;
      }
      // data_reference_index (2)
      if (pos + 2 > end) break;
      pos += 2;
      // base_offset (baseOffsetSize bytes)
      if (pos + baseOffsetSize > end) break;
      long baseOffset = ReadUInt(data, pos, baseOffsetSize);
      pos += baseOffsetSize;
      if (pos + 2 > end) break;
      var extentCount = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(pos));
      pos += 2;
      var extents = new List<ItemExtent>();
      for (var e = 0; e < extentCount && pos < end; e++) {
        if (version >= 1 && indexSize > 0) {
          if (pos + indexSize > end) break;
          pos += indexSize;
        }
        if (pos + offsetSize + lengthSize > end) break;
        var extOff = ReadUInt(data, pos, offsetSize);
        pos += offsetSize;
        var extLen = ReadUInt(data, pos, lengthSize);
        pos += lengthSize;
        extents.Add(new ItemExtent(extOff, extLen));
      }
      result.Add(new ItemLocation(id, baseOffset, extents, constructionMethod));
    }
    return result;
  }

  private static long ReadUInt(byte[] data, int pos, int size) => size switch {
    0 => 0,
    1 => data[pos],
    2 => BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(pos)),
    4 => BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos)),
    8 => (long)BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(pos)),
    _ => 0,
  };

  private static (IReadOnlyDictionary<uint, byte[]>, IReadOnlyList<(uint, IReadOnlyList<uint>)>) ParseIprp(byte[] data, BoxParser.Box iprp) {
    var props = new Dictionary<uint, byte[]>();
    var assoc = new List<(uint, IReadOnlyList<uint>)>();
    // iprp is compound — children are ipco (property container) and ipma (association).
    var off = (int)iprp.BodyOffset;
    var end = (int)(iprp.BodyOffset + iprp.BodyLength);
    var pos = off;
    while (pos + 8 <= end) {
      var size = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos));
      var type = Encoding.ASCII.GetString(data, pos + 4, 4);
      if (size < 8 || pos + size > end) break;
      if (type == "ipco") {
        var childEnd = pos + size;
        var cp = pos + 8;
        uint idx = 1;
        while (cp + 8 <= childEnd) {
          var cs = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(cp));
          if (cs < 8 || cp + cs > childEnd) break;
          // Store each property box (header + body) so consumers can identify them.
          var propBytes = new byte[cs];
          Buffer.BlockCopy(data, cp, propBytes, 0, cs);
          props[idx] = propBytes;
          idx++;
          cp += cs;
        }
      } else if (type == "ipma") {
        ParseIpma(data, pos + 8, pos + size, assoc);
      }
      pos += size;
    }
    return (props, assoc);
  }

  private static void ParseIpma(byte[] data, int bodyStart, int bodyEnd, List<(uint, IReadOnlyList<uint>)> assoc) {
    if (bodyStart + 4 > bodyEnd) return;
    var version = data[bodyStart];
    var flags = ((data[bodyStart + 1] << 16) | (data[bodyStart + 2] << 8) | data[bodyStart + 3]);
    var pos = bodyStart + 4;
    if (pos + 4 > bodyEnd) return;
    var entryCount = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos));
    pos += 4;
    for (uint i = 0; i < entryCount && pos < bodyEnd; i++) {
      uint itemId;
      if (version < 1) {
        if (pos + 2 > bodyEnd) break;
        itemId = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(pos));
        pos += 2;
      } else {
        if (pos + 4 > bodyEnd) break;
        itemId = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos));
        pos += 4;
      }
      if (pos + 1 > bodyEnd) break;
      var assocCount = data[pos];
      pos++;
      var indexes = new List<uint>();
      for (var a = 0; a < assocCount && pos < bodyEnd; a++) {
        uint propIdx;
        if ((flags & 1) != 0) {
          if (pos + 2 > bodyEnd) break;
          propIdx = (uint)(BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(pos)) & 0x7FFF);
          pos += 2;
        } else {
          if (pos + 1 > bodyEnd) break;
          propIdx = (uint)(data[pos] & 0x7F);
          pos++;
        }
        indexes.Add(propIdx);
      }
      assoc.Add((itemId, indexes));
    }
  }
}
