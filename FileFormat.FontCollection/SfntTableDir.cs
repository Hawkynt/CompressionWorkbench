#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileFormat.FontCollection;

/// <summary>
/// Parsed SFNT offset subtable + table records for a standalone .ttf/.otf.
/// </summary>
internal sealed class SfntTableDir {
  public uint SfntVersion { get; }
  public IReadOnlyDictionary<string, (uint Offset, uint Length)> Tables { get; }

  private SfntTableDir(uint version, Dictionary<string, (uint, uint)> tables) {
    this.SfntVersion = version;
    this.Tables = tables;
  }

  public static SfntTableDir Parse(ReadOnlySpan<byte> data) {
    if (data.Length < 12) throw new InvalidDataException("SFNT truncated header.");
    var version = BinaryPrimitives.ReadUInt32BigEndian(data);
    var numTables = BinaryPrimitives.ReadUInt16BigEndian(data[4..]);
    if (data.Length < 12 + 16 * numTables) throw new InvalidDataException("SFNT truncated records.");

    var tables = new Dictionary<string, (uint, uint)>(numTables);
    for (var i = 0; i < numTables; ++i) {
      var rec = data[(12 + 16 * i)..];
      var tag = TagToString(rec[..4]);
      var offset = BinaryPrimitives.ReadUInt32BigEndian(rec[8..]);
      var length = BinaryPrimitives.ReadUInt32BigEndian(rec[12..]);
      tables[tag] = (offset, length);
    }
    return new SfntTableDir(version, tables);
  }

  private static string TagToString(ReadOnlySpan<byte> bytes) {
    Span<char> c = stackalloc char[4];
    c[0] = (char)bytes[0]; c[1] = (char)bytes[1]; c[2] = (char)bytes[2]; c[3] = (char)bytes[3];
    return new string(c);
  }
}
