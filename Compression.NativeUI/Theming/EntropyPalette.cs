using System.Drawing;

namespace Compression.NativeUI.Theming;

/// <summary>
/// Maps an entropy value (0 to 8 bits per byte) onto the shell's blue-to-red ramp: blue for
/// plaintext and repeating data, cyan and green for structure, yellow and orange for dictionary
/// compression, red for random or encrypted bytes.
/// </summary>
internal static class EntropyPalette {
  public static Color ToColor(double entropy) {
    var t = Math.Clamp(entropy / 8.0, 0, 1);

    byte r, g, b;
    if (t < 0.25) {
      // Blue to cyan.
      var f = t / 0.25;
      r = (byte)(40 * f);
      g = (byte)(120 + 100 * f);
      b = (byte)(220 - 40 * f);
    } else if (t < 0.5) {
      // Cyan to green.
      var f = (t - 0.25) / 0.25;
      r = (byte)(40 + 60 * f);
      g = (byte)(220 - 30 * f);
      b = (byte)(180 - 120 * f);
    } else if (t < 0.75) {
      // Green to yellow and orange.
      var f = (t - 0.5) / 0.25;
      r = (byte)(100 + 140 * f);
      g = (byte)(190 - 40 * f);
      b = (byte)(60 - 30 * f);
    } else {
      // Orange to red.
      var f = (t - 0.75) / 0.25;
      r = 240;
      g = (byte)(150 - 110 * f);
      b = (byte)(30 + 10 * f);
    }

    return Color.FromArgb(r, g, b);
  }

  /// <summary>The same colour as a light wash, for tinting a whole row behind its text.</summary>
  public static Color ToRowTint(double entropy) {
    var c = ToColor(entropy);
    return Color.FromArgb(50, c.R, c.G, c.B);
  }
}
