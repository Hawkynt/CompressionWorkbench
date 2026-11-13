using System.Buffers.Binary;

namespace FileFormat.StuffIt;

/// <summary>
/// The Macintosh resource fork layout, as Inside Macintosh: More Macintosh Toolbox (1993), chapter 1
/// "Resource Manager", describes it, for the one thing classic StuffIt needs: finding a resource
/// and writing a fork that holds a single resource.
/// </summary>
/// <remarks>
/// The fork starts with a 16-byte header: data offset, map offset, data length and map length.
/// Each resource in the data area is a 32-bit length followed by its bytes. The map repeats the
/// header, then holds 6 reserved bytes, the fork attributes, and the offsets of the type list
/// and the name list from the map's start. The type list is a count less one, then 8-byte
/// entries (type, count less one, offset of that type's reference list from the type list).
/// Each 12-byte reference holds the id, a name offset (−1 = none), the attributes, a 24-bit
/// data offset and a reserved handle.
/// </remarks>
internal static class MacResourceFork {

  /// <summary>The data of resource (<paramref name="type"/>, <paramref name="id"/>), or <see langword="null"/>.</summary>
  public static byte[]? Find(ReadOnlySpan<byte> fork, uint type, short id) {
    if (fork.Length < 16) return null;
    var dataOffset = (int)BinaryPrimitives.ReadUInt32BigEndian(fork);
    var mapOffset = (int)BinaryPrimitives.ReadUInt32BigEndian(fork[4..]);
    if (mapOffset < 0 || mapOffset + 30 > fork.Length || dataOffset < 0 || dataOffset > fork.Length) return null;
    var map = fork[mapOffset..];
    var typeList = BinaryPrimitives.ReadUInt16BigEndian(map[24..]);
    if (typeList + 2 > map.Length) return null;
    var types = map[typeList..];
    var typeCount = BinaryPrimitives.ReadUInt16BigEndian(types) + 1;
    for (var t = 0; t < typeCount; ++t) {
      var entry = 2 + 8 * t;
      if (entry + 8 > types.Length) return null;
      if (BinaryPrimitives.ReadUInt32BigEndian(types[entry..]) != type) continue;
      var count = BinaryPrimitives.ReadUInt16BigEndian(types[(entry + 4)..]) + 1;
      var refs = BinaryPrimitives.ReadUInt16BigEndian(types[(entry + 6)..]);
      for (var k = 0; k < count; ++k) {
        var r = refs + 12 * k;
        if (r + 12 > types.Length) return null;
        if (BinaryPrimitives.ReadInt16BigEndian(types[r..]) != id) continue;
        var at = dataOffset + (int)(BinaryPrimitives.ReadUInt32BigEndian(types[(r + 4)..]) & 0xFFFFFF);
        if (at + 4 > fork.Length) return null;
        var length = (int)BinaryPrimitives.ReadUInt32BigEndian(fork[at..]);
        return length < 0 || at + 4 + length > fork.Length ? null : fork.Slice(at + 4, length).ToArray();
      }
    }
    return null;
  }

  /// <summary>A resource fork holding the single resource (<paramref name="type"/>, <paramref name="id"/>).</summary>
  public static byte[] Single(uint type, short id, ReadOnlySpan<byte> data) {
    const int DataOffset = 256; // the Resource Manager leaves the first 256 bytes after the header for application use
    var dataLength = 4 + data.Length;
    var mapOffset = DataOffset + dataLength;
    const int MapLength = 28 + 2 + 8 + 12; // header copy, reserved, attributes, list offsets; type list; one reference
    var fork = new byte[mapOffset + MapLength];
    void Header(Span<byte> h) {
      BinaryPrimitives.WriteUInt32BigEndian(h, DataOffset);
      BinaryPrimitives.WriteUInt32BigEndian(h[4..], (uint)mapOffset);
      BinaryPrimitives.WriteUInt32BigEndian(h[8..], (uint)dataLength);
      BinaryPrimitives.WriteUInt32BigEndian(h[12..], MapLength);
    }
    Header(fork);
    BinaryPrimitives.WriteUInt32BigEndian(fork.AsSpan(DataOffset), (uint)data.Length);
    data.CopyTo(fork.AsSpan(DataOffset + 4));

    var map = fork.AsSpan(mapOffset);
    Header(map);
    BinaryPrimitives.WriteUInt16BigEndian(map[24..], 28); // type list right after the map header
    BinaryPrimitives.WriteUInt16BigEndian(map[26..], MapLength); // empty name list at the end
    var types = map[28..];
    BinaryPrimitives.WriteUInt16BigEndian(types, 0); // one type
    BinaryPrimitives.WriteUInt32BigEndian(types[2..], type);
    BinaryPrimitives.WriteUInt16BigEndian(types[6..], 0); // one resource
    BinaryPrimitives.WriteUInt16BigEndian(types[8..], 10); // its reference follows the type list
    BinaryPrimitives.WriteInt16BigEndian(types[10..], id);
    BinaryPrimitives.WriteInt16BigEndian(types[12..], -1); // no name
    // attributes 0, data offset 0, handle 0
    return fork;
  }
}
