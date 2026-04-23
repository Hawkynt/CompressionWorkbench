#pragma warning disable CS1591
using System.Globalization;

namespace FileFormat.FirmwareHex;

/// <summary>
/// Reader for Motorola S-Record files (<c>Stnn[aaaa|aaaaaa|aaaaaaaa]dd…cc</c>).
/// Recognised types: S0 header, S1/S2/S3 data (16/24/32-bit address), S5/S6
/// record counts (informational), S7/S8/S9 termination (32/24/16-bit start addr).
/// </summary>
public sealed class SRecordReader {

  /// <summary>Parses an S-Record text document into a <see cref="FirmwareImage"/>.</summary>
  public static FirmwareImage Read(string text) {
    var segments = new SortedDictionary<uint, byte[]>();
    uint? startAddr = null;
    var recordCount = 0;
    var sawTermination = false;

    using var reader = new StringReader(text);
    for (var line = reader.ReadLine(); line != null; line = reader.ReadLine()) {
      line = line.Trim();
      if (line.Length < 4 || line[0] != 'S') continue;
      recordCount++;

      var type = line[1];
      var countHex = line.AsSpan(2, 2);
      if (!byte.TryParse(countHex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var count))
        throw new InvalidDataException($"SRecord: invalid count field in '{line}'.");
      // The record hex payload is count+1 bytes: the count byte itself + the
      // count bytes it declares (address + data + checksum).
      if (line.Length < 4 + count * 2)
        throw new InvalidDataException($"SRecord: declared byte count {count} exceeds payload in '{line}'.");

      // Address width is baked into the S-type.
      var addrBytes = type switch {
        '0' or '1' or '5' or '9' => 2,
        '2' or '6' or '8' => 3,
        '3' or '7' => 4,
        _ => -1,
      };
      if (addrBytes < 0) continue; // S4 is reserved/unused
      var bytes = ParseHexBytes(line.AsSpan(2, (count + 1) * 2));

      // S-Record checksum = ones' complement of sum of all bytes after the type,
      // covering count + address + data but NOT the checksum byte itself.
      byte sum = 0;
      for (var i = 0; i < bytes.Length - 1; i++) sum += bytes[i];
      var expected = (byte)(~sum & 0xFF);
      if (expected != bytes[^1])
        throw new InvalidDataException(
          $"SRecord: checksum mismatch (computed 0x{expected:X2}, stored 0x{bytes[^1]:X2}) on '{line}'.");

      var addr = 0u;
      for (var i = 0; i < addrBytes; i++)
        addr = (addr << 8) | bytes[1 + i];
      var dataLen = bytes.Length - 2 - addrBytes;
      var data = dataLen > 0 ? bytes.AsSpan(1 + addrBytes, dataLen).ToArray() : [];

      switch (type) {
        case '0': break; // header record, contains ASCII module name
        case '1' or '2' or '3':
          AddSegment(segments, addr, data);
          break;
        case '5' or '6': break; // record count, informational
        case '7' or '8' or '9':
          startAddr = addr;
          sawTermination = true;
          break;
      }
    }

    if (!sawTermination)
      throw new InvalidDataException("SRecord: missing S7/S8/S9 termination record.");

    var (merged, gapCount, totalBytes) = IntelHexReader.MergeSegments(segments);
    return new FirmwareImage(merged, startAddr, recordCount, gapCount, totalBytes, "SRecord");
  }

  private static byte[] ParseHexBytes(ReadOnlySpan<char> hex) {
    if ((hex.Length & 1) != 0)
      throw new InvalidDataException("SRecord: odd-length hex payload.");
    var bytes = new byte[hex.Length / 2];
    for (var i = 0; i < bytes.Length; i++) {
      if (!byte.TryParse(hex.Slice(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
        throw new InvalidDataException($"SRecord: invalid hex byte '{hex.Slice(i * 2, 2).ToString()}'.");
      bytes[i] = b;
    }
    return bytes;
  }

  private static void AddSegment(SortedDictionary<uint, byte[]> segments, uint addr, byte[] data) {
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
}
