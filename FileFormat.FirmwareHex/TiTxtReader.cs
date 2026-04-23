#pragma warning disable CS1591
using System.Globalization;

namespace FileFormat.FirmwareHex;

/// <summary>
/// Reader for TI-TXT MSP430 text firmware files. Addresses are introduced by
/// <c>@HHHH</c> lines; data lines follow as space-separated hex bytes (typically
/// 16 per line); a single <c>q</c> token terminates the file.
/// </summary>
public sealed class TiTxtReader {

  /// <summary>Parses a TI-TXT document into a <see cref="FirmwareImage"/>.</summary>
  public static FirmwareImage Read(string text) {
    var segments = new SortedDictionary<uint, List<byte>>();
    uint? currentAddr = null;
    var sawTerminator = false;
    var dataLines = 0;

    using var reader = new StringReader(text);
    for (var line = reader.ReadLine(); line != null; line = reader.ReadLine()) {
      line = line.Trim();
      if (line.Length == 0) continue;

      if (line.StartsWith('q') || line.StartsWith('Q')) {
        sawTerminator = true;
        break;
      }
      if (line.StartsWith('@')) {
        if (!uint.TryParse(line.AsSpan(1).Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var addr))
          throw new InvalidDataException($"TiTxt: invalid address record '{line}'.");
        currentAddr = addr;
        if (!segments.ContainsKey(addr)) segments[addr] = [];
        continue;
      }
      if (currentAddr is null)
        throw new InvalidDataException($"TiTxt: data before first '@address' line ('{line}').");

      dataLines++;
      foreach (var token in line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries)) {
        if (!byte.TryParse(token, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
          throw new InvalidDataException($"TiTxt: invalid hex byte '{token}'.");
        segments[currentAddr.Value].Add(b);
      }
    }

    if (!sawTerminator)
      throw new InvalidDataException("TiTxt: missing 'q' termination line.");

    var dict = new SortedDictionary<uint, byte[]>();
    foreach (var (k, v) in segments) dict[k] = v.ToArray();
    var (merged, gapCount, totalBytes) = IntelHexReader.MergeSegments(dict);
    return new FirmwareImage(merged, StartAddress: null, RecordCount: dataLines,
      GapCount: gapCount, TotalDataBytes: totalBytes, SourceFormat: "TiTxt");
  }
}
