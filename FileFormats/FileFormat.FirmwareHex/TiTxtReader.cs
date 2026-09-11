#pragma warning disable CS1591
using System.Globalization;

namespace FileFormat.FirmwareHex;

/// <summary>
/// Parser for Texas Instruments TI-TXT firmware images. Address records introduce
/// sparse byte runs and a line containing only <c>q</c> terminates the document.
/// </summary>
public sealed class TiTxtReader {

  /// <summary>Parses a TI-TXT document into its sparse firmware image.</summary>
  public static FirmwareImage Read(string text) {
    ArgumentNullException.ThrowIfNull(text);

    var sections = new List<(uint Address, List<byte> Data)>();
    List<byte>? currentData = null;
    var sawTerminator = false;
    var dataLines = 0;

    using var reader = new StringReader(text);
    for (var raw = reader.ReadLine(); raw != null; raw = reader.ReadLine()) {
      var line = raw.Trim();
      if (line.Length == 0) continue;

      if (sawTerminator)
        throw new InvalidDataException($"TiTxt: content after the 'q' termination line ('{line}').");

      if (line.Equals("q", StringComparison.OrdinalIgnoreCase)) {
        sawTerminator = true;
        currentData = null;
        continue;
      }

      if (line.StartsWith('@')) {
        var address = line.AsSpan(1).Trim();
        if (address.IsEmpty || !uint.TryParse(address, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var parsed))
          throw new InvalidDataException($"TiTxt: invalid address record '{line}'.");
        currentData = [];
        sections.Add((parsed, currentData));
        continue;
      }

      if (currentData is null)
        throw new InvalidDataException($"TiTxt: data before first '@address' line ('{line}').");

      dataLines++;
      foreach (var token in line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries)) {
        if (token.Length != 2 || !byte.TryParse(token, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value))
          throw new InvalidDataException($"TiTxt: invalid hex byte '{token}'.");
        currentData.Add(value);
      }
    }

    if (!sawTerminator)
      throw new InvalidDataException("TiTxt: missing 'q' termination line.");

    var merged = NormalizeSections(sections, out var gapCount, out var totalBytes);
    return new FirmwareImage(merged, StartAddress: null, RecordCount: dataLines,
      GapCount: gapCount, TotalDataBytes: totalBytes, SourceFormat: "TiTxt");
  }

  private static List<(uint Address, byte[] Data)> NormalizeSections(
      List<(uint Address, List<byte> Data)> sections, out int gapCount, out int totalBytes) {
    var result = new List<(uint Address, byte[] Data)>();
    gapCount = 0;
    totalBytes = 0;
    ulong previousEnd = 0;
    var havePrevious = false;

    foreach (var (address, bytes) in sections.Where(section => section.Data.Count != 0).OrderBy(section => section.Address)) {
      var end = (ulong)address + (ulong)bytes.Count;
      if (end > (ulong)uint.MaxValue + 1)
        throw new InvalidDataException($"TiTxt: section at 0x{address:X} extends beyond the 32-bit address space.");
      if (havePrevious && address < previousEnd)
        throw new InvalidDataException($"TiTxt: section at 0x{address:X} overlaps a preceding section ending at 0x{previousEnd - 1:X}.");

      if (havePrevious && address == previousEnd) {
        var previous = result[^1];
        var joined = new byte[previous.Data.Length + bytes.Count];
        previous.Data.CopyTo(joined, 0);
        bytes.CopyTo(joined, previous.Data.Length);
        result[^1] = (previous.Address, joined);
      } else {
        if (havePrevious) gapCount++;
        result.Add((address, bytes.ToArray()));
      }

      previousEnd = end;
      havePrevious = true;
      checked { totalBytes += bytes.Count; }
    }

    return result;
  }
}
