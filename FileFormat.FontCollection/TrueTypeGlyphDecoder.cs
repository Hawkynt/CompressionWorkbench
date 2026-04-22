#pragma warning disable CS1591
using System.Buffers.Binary;

namespace FileFormat.FontCollection;

/// <summary>
/// Decodes TrueType glyph outlines ('glyf' table) into contours of (x, y, onCurve)
/// points. Simple glyphs only — composite glyphs are reported via
/// <see cref="DecodedGlyph.IsComposite"/> with their referenced sub-glyph indices,
/// allowing callers to emit a placeholder.
/// </summary>
internal static class TrueTypeGlyphDecoder {
  public sealed record DecodedGlyph(
    short XMin, short YMin, short XMax, short YMax,
    IReadOnlyList<IReadOnlyList<(int X, int Y, bool OnCurve)>> Contours,
    bool IsComposite,
    IReadOnlyList<int> ComponentGlyphIndices);

  // TrueType simple-glyph point flags (TT spec §"Glyf table").
  private const byte OnCurve = 0x01;
  private const byte XShort = 0x02;
  private const byte YShort = 0x04;
  private const byte Repeat = 0x08;
  private const byte XIsSameOrPositive = 0x10;
  private const byte YIsSameOrPositive = 0x20;

  // Composite-glyph flags (TT spec §"Composite glyphs").
  private const ushort CArgsAreWords = 0x0001;
  private const ushort CWeHaveScale = 0x0008;
  private const ushort CMoreComponents = 0x0020;
  private const ushort CWeHaveXYScale = 0x0040;
  private const ushort CWeHave2By2 = 0x0080;
  private const ushort CWeHaveInstructions = 0x0100;

  public static DecodedGlyph Decode(ReadOnlySpan<byte> glyphBytes) {
    if (glyphBytes.IsEmpty) {
      // Empty glyph record — an advance-only glyph (e.g. space).
      return new DecodedGlyph(0, 0, 0, 0, [], false, []);
    }
    var numContours = BinaryPrimitives.ReadInt16BigEndian(glyphBytes);
    var xMin = BinaryPrimitives.ReadInt16BigEndian(glyphBytes[2..]);
    var yMin = BinaryPrimitives.ReadInt16BigEndian(glyphBytes[4..]);
    var xMax = BinaryPrimitives.ReadInt16BigEndian(glyphBytes[6..]);
    var yMax = BinaryPrimitives.ReadInt16BigEndian(glyphBytes[8..]);

    if (numContours < 0)
      return DecodeComposite(glyphBytes[10..], xMin, yMin, xMax, yMax);

    // Simple glyph
    var endPts = new ushort[numContours];
    var pos = 10;
    for (var i = 0; i < numContours; ++i) {
      endPts[i] = BinaryPrimitives.ReadUInt16BigEndian(glyphBytes[pos..]);
      pos += 2;
    }
    var numPoints = numContours == 0 ? 0 : endPts[^1] + 1;

    var instrLen = BinaryPrimitives.ReadUInt16BigEndian(glyphBytes[pos..]);
    pos += 2 + instrLen;

    var flags = new byte[numPoints];
    for (var i = 0; i < numPoints;) {
      var f = glyphBytes[pos++];
      flags[i++] = f;
      if ((f & Repeat) != 0) {
        var repeats = glyphBytes[pos++];
        for (var r = 0; r < repeats && i < numPoints; ++r) flags[i++] = f;
      }
    }

    var xs = new int[numPoints];
    var x = 0;
    for (var i = 0; i < numPoints; ++i) {
      var f = flags[i];
      if ((f & XShort) != 0) {
        var dx = glyphBytes[pos++];
        x += (f & XIsSameOrPositive) != 0 ? dx : -dx;
      } else if ((f & XIsSameOrPositive) == 0) {
        x += BinaryPrimitives.ReadInt16BigEndian(glyphBytes[pos..]);
        pos += 2;
      }
      xs[i] = x;
    }
    var ys = new int[numPoints];
    var y = 0;
    for (var i = 0; i < numPoints; ++i) {
      var f = flags[i];
      if ((f & YShort) != 0) {
        var dy = glyphBytes[pos++];
        y += (f & YIsSameOrPositive) != 0 ? dy : -dy;
      } else if ((f & YIsSameOrPositive) == 0) {
        y += BinaryPrimitives.ReadInt16BigEndian(glyphBytes[pos..]);
        pos += 2;
      }
      ys[i] = y;
    }

    var contours = new List<IReadOnlyList<(int, int, bool)>>(numContours);
    var start = 0;
    for (var c = 0; c < numContours; ++c) {
      var end = endPts[c];
      var contour = new List<(int, int, bool)>(end - start + 1);
      for (var i = start; i <= end; ++i)
        contour.Add((xs[i], ys[i], (flags[i] & OnCurve) != 0));
      contours.Add(contour);
      start = end + 1;
    }

    return new DecodedGlyph(xMin, yMin, xMax, yMax, contours, false, []);
  }

  private static DecodedGlyph DecodeComposite(ReadOnlySpan<byte> data, short xMin, short yMin, short xMax, short yMax) {
    var components = new List<int>();
    var pos = 0;
    while (true) {
      if (pos + 4 > data.Length) break;
      var flags = BinaryPrimitives.ReadUInt16BigEndian(data[pos..]);
      var glyphIndex = BinaryPrimitives.ReadUInt16BigEndian(data[(pos + 2)..]);
      components.Add(glyphIndex);
      pos += 4;
      pos += (flags & CArgsAreWords) != 0 ? 4 : 2;
      if ((flags & CWeHaveScale) != 0) pos += 2;
      else if ((flags & CWeHaveXYScale) != 0) pos += 4;
      else if ((flags & CWeHave2By2) != 0) pos += 8;
      if ((flags & CMoreComponents) == 0) break;
      if ((flags & CWeHaveInstructions) != 0) break; // instructions terminate the list
    }
    return new DecodedGlyph(xMin, yMin, xMax, yMax, [], true, components);
  }
}
