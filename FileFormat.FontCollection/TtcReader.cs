#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileFormat.FontCollection;

/// <summary>
/// Slices a TrueType / OpenType collection (.ttc / .otc) into per-member standalone
/// SFNT fonts. Each member font's tables — some of which may be shared across members
/// in the source collection — are copied verbatim into the output; this trades
/// compactness for an output that any font consumer reads without TTC support.
/// </summary>
public sealed class TtcReader {
  /// <summary>One sliced member font.</summary>
  public sealed record Member(int Index, string Extension, byte[] Data);

  public List<Member> Read(ReadOnlySpan<byte> data) {
    if (data.Length < 12)
      throw new InvalidDataException("TTC too short for header.");

    if (!(data[0] == (byte)'t' && data[1] == (byte)'t' && data[2] == (byte)'c' && data[3] == (byte)'f'))
      throw new InvalidDataException("Missing TTC 'ttcf' magic.");

    var numFonts = BinaryPrimitives.ReadUInt32BigEndian(data[8..]);
    if (numFonts == 0 || numFonts > 0x10000)
      throw new InvalidDataException($"Unreasonable TTC font count {numFonts}.");

    if (data.Length < 12 + 4 * numFonts)
      throw new InvalidDataException("Truncated TTC offset table.");

    var result = new List<Member>((int)numFonts);
    for (var i = 0; i < numFonts; ++i) {
      var offset = BinaryPrimitives.ReadUInt32BigEndian(data[(12 + 4 * i)..]);
      if (offset + 12 > data.Length)
        throw new InvalidDataException($"TTC font {i} offset {offset} out of range.");
      var (ext, blob) = SliceMember(data, (int)offset);
      result.Add(new Member(i, ext, blob));
    }
    return result;
  }

  // Reads the member font's offset subtable + table records at `memberOffset`, then
  // builds a standalone SFNT containing exactly those tables repacked contiguously.
  private static (string Extension, byte[] Data) SliceMember(ReadOnlySpan<byte> data, int memberOffset) {
    var sfntVersion = BinaryPrimitives.ReadUInt32BigEndian(data[memberOffset..]);
    var numTables = BinaryPrimitives.ReadUInt16BigEndian(data[(memberOffset + 4)..]);
    var recordsStart = memberOffset + 12;
    if (recordsStart + 16 * numTables > data.Length)
      throw new InvalidDataException("Truncated member font table records.");

    // Read original records and compute total payload size (4-byte aligned per table).
    var records = new (uint Tag, uint Checksum, uint Offset, uint Length)[numTables];
    for (var i = 0; i < numTables; ++i) {
      var rec = data[(recordsStart + 16 * i)..];
      records[i] = (
        BinaryPrimitives.ReadUInt32BigEndian(rec),
        BinaryPrimitives.ReadUInt32BigEndian(rec[4..]),
        BinaryPrimitives.ReadUInt32BigEndian(rec[8..]),
        BinaryPrimitives.ReadUInt32BigEndian(rec[12..])
      );
    }

    var tableArea = 0;
    foreach (var r in records) {
      if (r.Offset + r.Length > data.Length)
        throw new InvalidDataException($"Table '{FormatTag(r.Tag)}' out of range.");
      tableArea += ((int)r.Length + 3) & ~3;
    }

    var headerSize = 12 + 16 * numTables;
    var output = new byte[headerSize + tableArea];

    // Offset subtable: copy sfntVersion + numTables + searchRange/entrySelector/rangeShift
    // verbatim from source (they describe the table-record layout which is unchanged).
    data.Slice(memberOffset, 12).CopyTo(output);

    var writePos = headerSize;
    for (var i = 0; i < numTables; ++i) {
      var r = records[i];
      var recOut = output.AsSpan(12 + 16 * i);
      BinaryPrimitives.WriteUInt32BigEndian(recOut, r.Tag);
      BinaryPrimitives.WriteUInt32BigEndian(recOut[4..], r.Checksum);
      BinaryPrimitives.WriteUInt32BigEndian(recOut[8..], (uint)writePos);
      BinaryPrimitives.WriteUInt32BigEndian(recOut[12..], r.Length);
      data.Slice((int)r.Offset, (int)r.Length).CopyTo(output.AsSpan(writePos));
      writePos += ((int)r.Length + 3) & ~3;
    }

    return (ExtensionForSfnt(sfntVersion), output);
  }

  private static string ExtensionForSfnt(uint version) => version switch {
    0x00010000u => ".ttf",            // TrueType outlines
    0x4F54544Fu => ".otf",            // 'OTTO' — CFF outlines
    0x74727565u => ".ttf",            // 'true' — legacy Mac TrueType
    0x74797031u => ".otf",            // 'typ1' — PostScript Type 1 in SFNT
    _ => ".ttf",
  };

  private static string FormatTag(uint tag) {
    Span<char> c = stackalloc char[4];
    c[0] = (char)((tag >> 24) & 0xFF);
    c[1] = (char)((tag >> 16) & 0xFF);
    c[2] = (char)((tag >> 8) & 0xFF);
    c[3] = (char)(tag & 0xFF);
    return new string(c);
  }
}
