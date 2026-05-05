#pragma warning disable CS1591
using System.Globalization;

namespace FileFormat.FirmwareHex;

/// <summary>
/// Reader for Intel HEX records (<c>:LLAAAATT[DD…]CC</c>), the long-standing
/// flash-programmer text format. Supports record types 00 (data), 01 (EOF),
/// 02 (extended-segment address), 03 (start-segment address), 04 (extended-linear
/// address) and 05 (start-linear address). Maps all 16-bit addresses through the
/// active extended base into a flat 32-bit address space.
/// </summary>
public sealed class IntelHexReader {

  /// <summary>Parses an Intel HEX text document into a <see cref="FirmwareImage"/>.</summary>
  public static FirmwareImage Read(string text) {
    var segments = new SortedDictionary<uint, byte[]>();
    uint extendedBase = 0;
    uint? startAddr = null;
    var recordCount = 0;
    var sawEof = false;

    using var reader = new StringReader(text);
    for (var line = reader.ReadLine(); line != null; line = reader.ReadLine()) {
      line = line.Trim();
      if (line.Length == 0 || line[0] != ':') continue;
      if (line.Length < 11) throw new InvalidDataException($"IntelHex: short record '{line}'.");
      recordCount++;

      var bytes = ParseHexBytes(line.AsSpan(1));
      if (bytes.Length < 5)
        throw new InvalidDataException($"IntelHex: record too short ({bytes.Length} bytes) in '{line}'.");
      var len = bytes[0];
      var addr = (ushort)((bytes[1] << 8) | bytes[2]);
      var type = bytes[3];
      if (bytes.Length != 5 + len)
        throw new InvalidDataException($"IntelHex: length {len} mismatches payload in '{line}'.");

      // Checksum = two's complement of the byte sum of len+addr+type+data (everything up to the checksum byte).
      byte sum = 0;
      for (var i = 0; i < bytes.Length - 1; i++) sum += bytes[i];
      var expected = (byte)((~sum + 1) & 0xFF);
      if (expected != bytes[^1])
        throw new InvalidDataException(
          $"IntelHex: checksum mismatch (computed 0x{expected:X2}, stored 0x{bytes[^1]:X2}) on '{line}'.");

      var payload = bytes.AsSpan(4, len).ToArray();
      switch (type) {
        case 0x00: // data
          AddSegment(segments, extendedBase + addr, payload);
          break;
        case 0x01: // EOF
          sawEof = true;
          break;
        case 0x02: // extended segment address (16-bit segment * 16)
          extendedBase = (uint)(((payload[0] << 8) | payload[1]) << 4);
          break;
        case 0x03: // start segment address (CS:IP)
          startAddr = (uint)(((payload[0] << 24) | (payload[1] << 16)) + ((payload[2] << 8) | payload[3]));
          break;
        case 0x04: // extended linear address (high 16 bits)
          extendedBase = (uint)((payload[0] << 24) | (payload[1] << 16));
          break;
        case 0x05: // start linear address (EIP)
          startAddr = (uint)((payload[0] << 24) | (payload[1] << 16) | (payload[2] << 8) | payload[3]);
          break;
        default:
          // Unknown record types are ignored (some vendors extend the format).
          break;
      }
    }

    if (!sawEof)
      throw new InvalidDataException("IntelHex: missing ':00000001FF' end-of-file record.");

    var (merged, gapCount, totalBytes) = MergeSegments(segments);
    return new FirmwareImage(merged, startAddr, recordCount, gapCount, totalBytes, "IntelHex");
  }

  private static byte[] ParseHexBytes(ReadOnlySpan<char> hex) {
    if ((hex.Length & 1) != 0)
      throw new InvalidDataException("IntelHex: odd-length hex payload.");
    var bytes = new byte[hex.Length / 2];
    for (var i = 0; i < bytes.Length; i++) {
      if (!byte.TryParse(hex.Slice(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
        throw new InvalidDataException($"IntelHex: invalid hex byte '{hex.Slice(i * 2, 2).ToString()}'.");
      bytes[i] = b;
    }
    return bytes;
  }

  private static void AddSegment(SortedDictionary<uint, byte[]> segments, uint addr, byte[] data) {
    // Merge with an immediately-preceding segment so consecutive 16-byte records fold into one.
    foreach (var k in segments.Keys) {
      var existing = segments[k];
      if (k + (uint)existing.Length == addr) {
        var merged = new byte[existing.Length + data.Length];
        Buffer.BlockCopy(existing, 0, merged, 0, existing.Length);
        Buffer.BlockCopy(data, 0, merged, existing.Length, data.Length);
        segments[k] = merged;
        return;
      }
    }
    segments[addr] = data;
  }

  internal static (List<(uint Address, byte[] Data)> Segments, int GapCount, int TotalBytes)
      MergeSegments(SortedDictionary<uint, byte[]> segments) {
    var list = new List<(uint, byte[])>();
    uint? prevEnd = null;
    var gaps = 0;
    var total = 0;
    foreach (var (addr, data) in segments) {
      if (prevEnd.HasValue && addr > prevEnd.Value) gaps++;
      list.Add((addr, data));
      prevEnd = addr + (uint)data.Length;
      total += data.Length;
    }
    return (list, gaps, total);
  }
}
