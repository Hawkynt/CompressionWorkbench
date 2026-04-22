#pragma warning disable CS1591
using System.Globalization;
using System.Text;

namespace FileFormat.Fits;

/// <summary>
/// One Header-Data Unit extracted from a FITS file.
/// </summary>
public sealed class FitsHdu {
  public List<string> Cards { get; } = new();
  public string? Xtension { get; set; }
  public int Bitpix { get; set; }
  public int Naxis { get; set; }
  public List<long> AxisSizes { get; } = new();
  public long DataOffset { get; set; }
  public long DataLength { get; set; }
  public string? Object { get; set; }
  public string? Telescope { get; set; }
}

/// <summary>
/// Minimal structural FITS parser: walks the 80-byte card / 2880-byte block structure
/// and reports each HDU's header cards and data slice. Does not decode pixel values.
/// </summary>
internal static class FitsParser {
  private const int CardSize = 80;
  private const int BlockSize = 2880;

  public static List<FitsHdu> ParseAll(byte[] data) {
    var result = new List<FitsHdu>();
    long pos = 0;
    var first = true;
    while (pos < data.LongLength) {
      var before = pos;
      var hdu = ParseOneHdu(data, ref pos, first);
      if (hdu == null) break;
      result.Add(hdu);
      first = false;
      // Guard against parsers that fail to advance (truncated/corrupt input).
      if (pos <= before) break;
    }
    return result;
  }

  /// <summary>
  /// Stream-based HDU enumeration. Only reads headers (bounded, typically &lt; a few KB each)
  /// and skips past data regions via <see cref="Stream.Seek"/>. Never materializes HDU payloads.
  /// </summary>
  public static List<FitsHdu> ParseAll(Stream stream) {
    var result = new List<FitsHdu>();
    var streamLen = stream.Length;
    stream.Seek(0, SeekOrigin.Begin);
    long pos = 0;
    while (pos < streamLen) {
      var before = pos;
      var hdu = ParseOneHduFromStream(stream, ref pos, streamLen);
      if (hdu == null) break;
      result.Add(hdu);
      if (pos <= before) break;
    }
    return result;
  }

  private static FitsHdu? ParseOneHduFromStream(Stream stream, ref long pos, long streamLen) {
    if (pos >= streamLen) return null;

    var hdu = new FitsHdu();
    var headerStart = pos;

    stream.Seek(pos, SeekOrigin.Begin);
    var cardBuf = new byte[CardSize];
    var endSeen = false;
    while (pos + CardSize <= streamLen) {
      var read = 0;
      while (read < CardSize) {
        var n = stream.Read(cardBuf, read, CardSize - read);
        if (n <= 0) break;
        read += n;
      }
      if (read < CardSize) break;
      pos += CardSize;

      var card = Encoding.ASCII.GetString(cardBuf);
      hdu.Cards.Add(card);

      var keyword = card.Length >= 8 ? card[..8].TrimEnd() : card.TrimEnd();
      if (keyword == "END") {
        endSeen = true;
        break;
      }

      switch (keyword) {
        case "SIMPLE":
          break;
        case "XTENSION":
          hdu.Xtension = ExtractStringValue(card);
          break;
        case "BITPIX":
          hdu.Bitpix = (int)(ExtractIntegerValue(card) ?? 0);
          break;
        case "NAXIS":
          hdu.Naxis = (int)(ExtractIntegerValue(card) ?? 0);
          break;
        case "OBJECT":
          hdu.Object = ExtractStringValue(card);
          break;
        case "TELESCOP":
          hdu.Telescope = ExtractStringValue(card);
          break;
        default:
          if (keyword.StartsWith("NAXIS", StringComparison.Ordinal) &&
              keyword.Length > 5 &&
              int.TryParse(keyword.AsSpan(5), NumberStyles.Integer, CultureInfo.InvariantCulture, out var axisIdx) &&
              axisIdx >= 1) {
            var val = ExtractIntegerValue(card) ?? 0;
            while (hdu.AxisSizes.Count < axisIdx)
              hdu.AxisSizes.Add(0);
            hdu.AxisSizes[axisIdx - 1] = val;
          }
          break;
      }
    }

    if (!endSeen) {
      hdu.DataOffset = pos;
      hdu.DataLength = 0;
      return hdu;
    }

    var headerBytes = pos - headerStart;
    var headerPadding = (BlockSize - headerBytes % BlockSize) % BlockSize;
    pos += headerPadding;

    hdu.DataOffset = pos;
    long dataBytes = 0;
    if (hdu.Naxis > 0 && hdu.Bitpix != 0) {
      dataBytes = Math.Abs(hdu.Bitpix) / 8;
      for (var i = 0; i < hdu.Naxis && i < hdu.AxisSizes.Count; i++) {
        var sz = hdu.AxisSizes[i];
        if (sz <= 0) { dataBytes = 0; break; }
        if (dataBytes > 0 && sz > long.MaxValue / dataBytes) { dataBytes = 0; break; }
        dataBytes *= sz;
      }
    }
    hdu.DataLength = dataBytes;

    if (dataBytes > 0) {
      if (pos + dataBytes > streamLen) {
        hdu.DataLength = Math.Max(0, streamLen - pos);
        pos = streamLen;
      } else {
        pos += dataBytes;
        var dataPadding = (BlockSize - dataBytes % BlockSize) % BlockSize;
        pos += dataPadding;
        if (pos > streamLen) pos = streamLen;
      }
    }

    return hdu;
  }

  private static FitsHdu? ParseOneHdu(byte[] data, ref long pos, bool isPrimary) {
    if (pos >= data.LongLength) return null;

    var hdu = new FitsHdu();
    var headerStart = pos;

    // Walk the header: read cards until we see END, then round up to the
    // nearest 2880-byte boundary.
    var endSeen = false;
    while (pos + CardSize <= data.LongLength) {
      var card = Encoding.ASCII.GetString(data, (int)pos, CardSize);
      pos += CardSize;
      hdu.Cards.Add(card);

      var keyword = card.Length >= 8 ? card[..8].TrimEnd() : card.TrimEnd();
      if (keyword == "END") {
        endSeen = true;
        break;
      }

      // Parse well-known keywords.
      switch (keyword) {
        case "SIMPLE":
          // primary header marker; value should be T
          break;
        case "XTENSION":
          hdu.Xtension = ExtractStringValue(card);
          break;
        case "BITPIX":
          hdu.Bitpix = (int)(ExtractIntegerValue(card) ?? 0);
          break;
        case "NAXIS":
          hdu.Naxis = (int)(ExtractIntegerValue(card) ?? 0);
          break;
        case "OBJECT":
          hdu.Object = ExtractStringValue(card);
          break;
        case "TELESCOP":
          hdu.Telescope = ExtractStringValue(card);
          break;
        default:
          if (keyword.StartsWith("NAXIS", StringComparison.Ordinal) &&
              keyword.Length > 5 &&
              int.TryParse(keyword.AsSpan(5), NumberStyles.Integer, CultureInfo.InvariantCulture, out var axisIdx) &&
              axisIdx >= 1) {
            var val = ExtractIntegerValue(card) ?? 0;
            while (hdu.AxisSizes.Count < axisIdx)
              hdu.AxisSizes.Add(0);
            hdu.AxisSizes[axisIdx - 1] = val;
          }
          break;
      }
    }

    if (!endSeen) {
      // Truncated header — salvage partial HDU and stop.
      hdu.DataOffset = pos;
      hdu.DataLength = 0;
      return hdu;
    }

    // Round up to next 2880-byte boundary.
    var headerBytes = pos - headerStart;
    var headerPadding = (BlockSize - headerBytes % BlockSize) % BlockSize;
    pos += headerPadding;

    // Compute data size.
    hdu.DataOffset = pos;
    long dataBytes = 0;
    if (hdu.Naxis > 0 && hdu.Bitpix != 0) {
      dataBytes = Math.Abs(hdu.Bitpix) / 8;
      for (var i = 0; i < hdu.Naxis && i < hdu.AxisSizes.Count; i++) {
        var sz = hdu.AxisSizes[i];
        if (sz <= 0) { dataBytes = 0; break; }
        // guard against absurd sizes overflowing
        if (dataBytes > 0 && sz > long.MaxValue / dataBytes) { dataBytes = 0; break; }
        dataBytes *= sz;
      }
    }
    hdu.DataLength = dataBytes;

    // Advance past data + its padding.
    if (dataBytes > 0) {
      if (pos + dataBytes > data.LongLength) {
        // truncated data region — clamp
        hdu.DataLength = Math.Max(0, data.LongLength - pos);
        pos = data.LongLength;
      } else {
        pos += dataBytes;
        var dataPadding = (BlockSize - dataBytes % BlockSize) % BlockSize;
        pos += dataPadding;
        if (pos > data.LongLength) pos = data.LongLength;
      }
    }

    _ = isPrimary;
    return hdu;
  }

  private static string? ExtractStringValue(string card) {
    // Format: KEYWORD = 'value    ' / comment
    var eq = card.IndexOf('=');
    if (eq < 0) return null;
    var rest = card[(eq + 1)..];
    // Look for single-quoted string value
    var open = rest.IndexOf('\'');
    if (open >= 0) {
      var close = rest.IndexOf('\'', open + 1);
      if (close > open)
        return rest.Substring(open + 1, close - open - 1).TrimEnd();
    }
    // Otherwise grab up to "/" comment
    var slash = rest.IndexOf('/');
    var val = slash >= 0 ? rest[..slash] : rest;
    return val.Trim();
  }

  private static long? ExtractIntegerValue(string card) {
    var eq = card.IndexOf('=');
    if (eq < 0) return null;
    var rest = card[(eq + 1)..];
    var slash = rest.IndexOf('/');
    var val = (slash >= 0 ? rest[..slash] : rest).Trim();
    if (long.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
      return n;
    return null;
  }
}
