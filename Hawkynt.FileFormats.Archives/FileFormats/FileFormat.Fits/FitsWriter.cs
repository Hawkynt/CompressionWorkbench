#pragma warning disable CS1591

using System.Text;

namespace FileFormat.Fits;

/// <summary>
/// Writes a minimal FITS file from a collection of named data blobs.
/// Each input file becomes a separate HDU: the primary HDU is the first file,
/// and any subsequent files become IMAGE extensions with NAXIS=1.
/// </summary>
internal static class FitsWriter {
  private const int CardSize = 80;
  private const int BlockSize = 2880;

  /// <summary>
  /// Creates a FITS file from a list of named data entries.
  /// </summary>
  /// <param name="output">The output stream.</param>
  /// <param name="entries">The entries to write, each as (name, data).</param>
  public static void Write(Stream output, IReadOnlyList<(string Name, byte[] Data)> entries) {
    for (var i = 0; i < entries.Count; i++) {
      var (name, data) = entries[i];
      WriteHdu(output, name, data, isPrimary: i == 0);
    }
  }

  private static void WriteHdu(Stream output, string name, byte[] data, bool isPrimary) {
    var cards = new List<string>();

    if (isPrimary) {
      cards.Add(FormatCard("SIMPLE", "                   T"));
    } else {
      cards.Add(FormatCard("XTENSION", $"'IMAGE   '{'/',20} extension"));
    }

    cards.Add(FormatCard("BITPIX", $"{8,20}"));
    cards.Add(FormatCard("NAXIS", $"{1,20}"));
    cards.Add(FormatCard("NAXIS1", $"{data.Length,20}"));

    if (!isPrimary)
      cards.Add(FormatCard("PCOUNT", $"{0,20}"));
    if (!isPrimary)
      cards.Add(FormatCard("GCOUNT", $"{1,20}"));

    // Store the filename as OBJECT
    var safeName = name.Length > 60 ? name[..60] : name;
    cards.Add(FormatCard("OBJECT", $"'{safeName,-8}' / {safeName}"));

    cards.Add("END".PadRight(CardSize));

    // Write header padded to BlockSize boundary
    var headerText = string.Concat(cards);
    var headerPad = (BlockSize - headerText.Length % BlockSize) % BlockSize;
    headerText += new string(' ', headerPad);
    var headerBytes = Encoding.ASCII.GetBytes(headerText);
    output.Write(headerBytes);

    // Write data padded to BlockSize boundary
    output.Write(data);
    var dataPad = (BlockSize - (int)(data.Length % BlockSize)) % BlockSize;
    if (dataPad > 0) {
      var padding = new byte[dataPad];
      output.Write(padding);
    }
  }

  private static string FormatCard(string keyword, string valuePart) {
    var kw = keyword.Length >= 8 ? keyword[..8] : keyword.PadRight(8);
    var full = kw + "= " + valuePart;
    if (full.Length > CardSize)
      full = full[..CardSize];
    return full.PadRight(CardSize);
  }
}
