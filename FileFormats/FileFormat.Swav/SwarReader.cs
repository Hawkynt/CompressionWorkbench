#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileFormat.Swav;

/// <summary>
/// Parses a Nintendo DS wave archive (<c>SWAR</c>): an NDS header identical in shape to a SWAV's
/// (magic, BOM, version, fileSize, headerSize, numBlocks) followed by a <c>DATA</c> block that
/// holds a record count and an offset table. Each table offset points at a SWAVINFO record
/// (12-byte info header + sample data) which <see cref="SwavReader.ReadRecord"/> decodes. The last
/// record runs to the start of the next record (or to end-of-archive for the final entry).
/// </summary>
public sealed class SwarReader {

  /// <summary>Decodes every wave contained in a SWAR archive.</summary>
  public IReadOnlyList<SwavReader.ParsedSwav> Read(ReadOnlySpan<byte> data) {
    if (data.Length < 0x3C)
      throw new InvalidDataException("SWAR too short.");
    if (data[0] != 'S' || data[1] != 'W' || data[2] != 'A' || data[3] != 'R')
      throw new InvalidDataException("Missing SWAR magic.");

    // NDS header(0x10) then "DATA" block: marker(4) blockSize(4) reserved(0x20) count(4) then offsets.
    if (data[0x10] != 'D' || data[0x11] != 'A' || data[0x12] != 'T' || data[0x13] != 'A')
      throw new InvalidDataException("SWAR missing DATA block.");

    // After the 8-byte DATA marker+size there are 0x20 reserved bytes, then u32 sample count.
    var countOffset = 0x10 + 8 + 0x20;
    if (countOffset + 4 > data.Length)
      throw new InvalidDataException("SWAR record count out of range.");
    var count = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[countOffset..]);
    if (count is < 0 or > 100_000)
      throw new InvalidDataException($"Implausible SWAR record count {count}.");

    var tableBase = countOffset + 4;
    var offsets = new int[count];
    for (var i = 0; i < count; ++i) {
      var o = tableBase + i * 4;
      if (o + 4 > data.Length)
        throw new InvalidDataException("SWAR offset table out of range.");
      offsets[i] = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[o..]);
    }

    var reader = new SwavReader();
    var waves = new List<SwavReader.ParsedSwav>(count);
    for (var i = 0; i < count; ++i) {
      var infoOff = offsets[i];
      if (infoOff <= 0 || infoOff + 12 > data.Length)
        continue;
      var end = i + 1 < count && offsets[i + 1] > infoOff ? offsets[i + 1] : data.Length;
      if (end > data.Length) end = data.Length;
      waves.Add(reader.ReadRecord(data, infoOff, end));
    }

    return waves;
  }
}
