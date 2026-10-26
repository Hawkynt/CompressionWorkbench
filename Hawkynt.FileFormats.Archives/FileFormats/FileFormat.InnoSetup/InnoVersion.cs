using System.Text;

namespace FileFormat.InnoSetup;

/// <summary>
/// The setup-data version an Inno Setup installer was written with, packed as
/// <c>(a &lt;&lt; 24) | (b &lt;&lt; 16) | (c &lt;&lt; 8) | d</c>.
/// </summary>
/// <remarks>
/// The 64-byte version tag that opens setup-0 ("Inno Setup Setup Data (5.5.7) (u)") names the
/// layout of everything after it. Some releases changed the layout without changing the tag; for
/// those the parser retries with the next known version (<see cref="Next"/>), as innoextract does.
/// The table of known tags is innoextract's (src/setup/version.cpp, zlib licence).
/// </remarks>
public readonly record struct InnoVersion(uint Value, bool IsUnicode, bool IsIsx, bool IsKnown) {

  /// <summary>Packs a version number.</summary>
  public static uint V(int a, int b, int c, int d = 0) => (uint)((a << 24) | (b << 16) | (c << 8) | d);

  /// <summary>Gets the major version.</summary>
  public int Major => (int)(this.Value >> 24);
  /// <summary>Gets the minor version.</summary>
  public int Minor => (int)((this.Value >> 16) & 0xFF);
  /// <summary>Gets the patch version.</summary>
  public int Patch => (int)((this.Value >> 8) & 0xFF);
  /// <summary>Gets the fourth version component (variant or build).</summary>
  public int Build => (int)(this.Value & 0xFF);

  /// <summary>True when this version is at least a.b.c.d.</summary>
  public bool AtLeast(int a, int b, int c, int d = 0) => this.Value >= V(a, b, c, d);

  /// <summary>True when this version is below a.b.c.d.</summary>
  public bool Below(int a, int b, int c, int d = 0) => this.Value < V(a, b, c, d);

  /// <inheritdoc/>
  public override string ToString() {
    var s = $"{this.Major}.{this.Minor}.{this.Patch}" + (this.Build != 0 ? $".{this.Build}" : "");
    if (this.IsUnicode) s += " (unicode)";
    if (this.IsIsx) s += " (isx)";
    return s;
  }

  private sealed record Known(string Name, uint Value, bool Unicode, bool Isx);

  private static readonly Known[] KnownVersions = [
    new("Inno Setup Setup Data (1.3.3)", V(1, 3, 3, 0), false, false),
    new("Inno Setup Setup Data (1.3.9)", V(1, 3, 9, 0), false, false),
    new("Inno Setup Setup Data (1.3.10)", V(1, 3, 10, 0), false, false),
    new("Inno Setup Setup Data (1.3.10) with ISX (1.3.10)", V(1, 3, 10, 0), false, true),
    new("Inno Setup Setup Data (1.3.12) with ISX (1.3.12.1)", V(1, 3, 12, 1), false, true),
    new("Inno Setup Setup Data (1.3.21)", V(1, 3, 21, 0), false, false),
    new("Inno Setup Setup Data (1.3.21) with ISX (1.3.17)", V(1, 3, 21, 0), false, true),
    new("Inno Setup Setup Data (1.3.24)", V(1, 3, 24, 0), false, false),
    new("Inno Setup Setup Data (1.3.21) with ISX (1.3.24)", V(1, 3, 24, 0), false, true),
    new("Inno Setup Setup Data (1.3.25)", V(1, 3, 25, 0), false, false),
    new("Inno Setup Setup Data (1.3.25) with ISX (1.3.25)", V(1, 3, 25, 0), false, true),
    new("Inno Setup Setup Data (2.0.0)", V(2, 0, 0, 0), false, false),
    new("Inno Setup Setup Data (2.0.1)", V(2, 0, 1, 0), false, false),
    new("Inno Setup Setup Data (2.0.2)", V(2, 0, 2, 0), false, false),
    new("Inno Setup Setup Data (2.0.5)", V(2, 0, 5, 0), false, false),
    new("Inno Setup Setup Data (2.0.6a)", V(2, 0, 6, 0), false, false),
    new("Inno Setup Setup Data (2.0.6a) with ISX (2.0.3)", V(2, 0, 6, 0), false, true),
    new("Inno Setup Setup Data (2.0.7)", V(2, 0, 7, 0), false, false),
    new("Inno Setup Setup Data (2.0.8)", V(2, 0, 8, 0), false, false),
    new("Inno Setup Setup Data (2.0.8) with ISX (2.0.3)", V(2, 0, 8, 0), false, true),
    new("Inno Setup Setup Data (2.0.8) with ISX (2.0.10)", V(2, 0, 10, 0), false, true),
    new("Inno Setup Setup Data (2.0.11)", V(2, 0, 11, 0), false, false),
    new("Inno Setup Setup Data (2.0.11) with ISX (2.0.11)", V(2, 0, 11, 0), false, true),
    new("Inno Setup Setup Data (2.0.17)", V(2, 0, 17, 0), false, false),
    new("Inno Setup Setup Data (2.0.17) with ISX (2.0.11)", V(2, 0, 17, 0), false, true),
    new("Inno Setup Setup Data (2.0.18)", V(2, 0, 18, 0), false, false),
    new("Inno Setup Setup Data (2.0.18) with ISX (2.0.11)", V(2, 0, 18, 0), false, true),
    new("Inno Setup Setup Data (3.0.0a)", V(3, 0, 0, 0), false, false),
    new("Inno Setup Setup Data (3.0.1)", V(3, 0, 1, 0), false, false),
    new("Inno Setup Setup Data (3.0.1) with ISX (3.0.0)", V(3, 0, 1, 0), false, true),
    new("Inno Setup Setup Data (3.0.3)", V(3, 0, 3, 0), false, false),
    new("Inno Setup Setup Data (3.0.3) with ISX (3.0.3)", V(3, 0, 3, 0), false, true),
    new("Inno Setup Setup Data (3.0.4)", V(3, 0, 4, 0), false, false),
    new("My Inno Setup Extensions Setup Data (3.0.4)", V(3, 0, 4, 0), false, true),
    new("Inno Setup Setup Data (3.0.5)", V(3, 0, 5, 0), false, false),
    new("My Inno Setup Extensions Setup Data (3.0.6.1)", V(3, 0, 6, 1), false, true),
    new("Inno Setup Setup Data (4.0.0a)", V(4, 0, 0, 0), false, false),
    new("Inno Setup Setup Data (4.0.1)", V(4, 0, 1, 0), false, false),
    new("Inno Setup Setup Data (4.0.3)", V(4, 0, 3, 0), false, false),
    new("Inno Setup Setup Data (4.0.5)", V(4, 0, 5, 0), false, false),
    new("Inno Setup Setup Data (4.0.9)", V(4, 0, 9, 0), false, false),
    new("Inno Setup Setup Data (4.0.10)", V(4, 0, 10, 0), false, false),
    new("Inno Setup Setup Data (4.0.11)", V(4, 0, 11, 0), false, false),
    new("Inno Setup Setup Data (4.1.0)", V(4, 1, 0, 0), false, false),
    new("Inno Setup Setup Data (4.1.2)", V(4, 1, 2, 0), false, false),
    new("Inno Setup Setup Data (4.1.3)", V(4, 1, 3, 0), false, false),
    new("Inno Setup Setup Data (4.1.4)", V(4, 1, 4, 0), false, false),
    new("Inno Setup Setup Data (4.1.5)", V(4, 1, 5, 0), false, false),
    new("Inno Setup Setup Data (4.1.6)", V(4, 1, 6, 0), false, false),
    new("Inno Setup Setup Data (4.1.8)", V(4, 1, 8, 0), false, false),
    new("Inno Setup Setup Data (4.2.0)", V(4, 2, 0, 0), false, false),
    new("Inno Setup Setup Data (4.2.1)", V(4, 2, 1, 0), false, false),
    new("Inno Setup Setup Data (4.2.2)", V(4, 2, 2, 0), false, false),
    new("Inno Setup Setup Data (4.2.3)", V(4, 2, 3, 0), false, false),
    new("Inno Setup Setup Data (4.2.4)", V(4, 2, 4, 0), false, false),
    new("Inno Setup Setup Data (4.2.5)", V(4, 2, 5, 0), false, false),
    new("Inno Setup Setup Data (4.2.6)", V(4, 2, 6, 0), false, false),
    new("Inno Setup Setup Data (5.0.0)", V(5, 0, 0, 0), false, false),
    new("Inno Setup Setup Data (5.0.1)", V(5, 0, 1, 0), false, false),
    new("Inno Setup Setup Data (5.0.3)", V(5, 0, 3, 0), false, false),
    new("Inno Setup Setup Data (5.0.4)", V(5, 0, 4, 0), false, false),
    new("Inno Setup Setup Data (5.1.0)", V(5, 1, 0, 0), false, false),
    new("Inno Setup Setup Data (5.1.2)", V(5, 1, 2, 0), false, false),
    new("Inno Setup Setup Data (5.1.7)", V(5, 1, 7, 0), false, false),
    new("Inno Setup Setup Data (5.1.10)", V(5, 1, 10, 0), false, false),
    new("Inno Setup Setup Data (5.1.13)", V(5, 1, 13, 0), false, false),
    new("Inno Setup Setup Data (5.2.0)", V(5, 2, 0, 0), false, false),
    new("Inno Setup Setup Data (5.2.1)", V(5, 2, 1, 0), false, false),
    new("Inno Setup Setup Data (5.2.3)", V(5, 2, 3, 0), false, false),
    new("Inno Setup Setup Data (5.2.5)", V(5, 2, 5, 0), false, false),
    new("Inno Setup Setup Data (5.2.5) (u)", V(5, 2, 5, 0), true, false),
    new("Inno Setup Setup Data (5.3.0)", V(5, 3, 0, 0), false, false),
    new("Inno Setup Setup Data (5.3.0) (u)", V(5, 3, 0, 0), true, false),
    new("Inno Setup Setup Data (5.3.3)", V(5, 3, 3, 0), false, false),
    new("Inno Setup Setup Data (5.3.3) (u)", V(5, 3, 3, 0), true, false),
    new("Inno Setup Setup Data (5.3.5)", V(5, 3, 5, 0), false, false),
    new("Inno Setup Setup Data (5.3.5) (u)", V(5, 3, 5, 0), true, false),
    new("Inno Setup Setup Data (5.3.6)", V(5, 3, 6, 0), false, false),
    new("Inno Setup Setup Data (5.3.6) (u)", V(5, 3, 6, 0), true, false),
    new("Inno Setup Setup Data (5.3.7)", V(5, 3, 7, 0), false, false),
    new("Inno Setup Setup Data (5.3.7) (u)", V(5, 3, 7, 0), true, false),
    new("Inno Setup Setup Data (5.3.8)", V(5, 3, 8, 0), false, false),
    new("Inno Setup Setup Data (5.3.8) (u)", V(5, 3, 8, 0), true, false),
    new("Inno Setup Setup Data (5.3.9)", V(5, 3, 9, 0), false, false),
    new("Inno Setup Setup Data (5.3.9) (u)", V(5, 3, 9, 0), true, false),
    new("Inno Setup Setup Data (5.3.10)", V(5, 3, 10, 0), false, false),
    new("Inno Setup Setup Data (5.3.10) (u)", V(5, 3, 10, 0), true, false),
    new("", V(5, 3, 10, 1), false, false),
    new("", V(5, 3, 10, 1), true, false),
    new("Inno Setup Setup Data (5.4.2)", V(5, 4, 2, 0), false, false),
    new("Inno Setup Setup Data (5.4.2) (u)", V(5, 4, 2, 0), true, false),
    new("", V(5, 4, 2, 1), false, false),
    new("", V(5, 4, 2, 1), true, false),
    new("Inno Setup Setup Data (5.5.0)", V(5, 5, 0, 0), false, false),
    new("Inno Setup Setup Data (5.5.0) (u)", V(5, 5, 0, 0), true, false),
    new("", V(5, 5, 0, 1), false, false),
    new("", V(5, 5, 0, 1), true, false),
    new("Inno Setup Setup Data (5.5.6)", V(5, 5, 6, 0), false, false),
    new("Inno Setup Setup Data (5.5.6) (u)", V(5, 5, 6, 0), true, false),
    new("Inno Setup Setup Data (5.5.7)", V(5, 5, 7, 0), false, false),
    new("Inno Setup Setup Data (5.5.7) (u)", V(5, 5, 7, 0), true, false),
    new("Inno Setup Setup Data (5.5.7) (U)", V(5, 5, 7, 0), true, false),
    new("Inno Setup Setup Data (5.5.8) (u)", V(5, 5, 7, 0), true, false),
    new("", V(5, 5, 7, 1), false, false),
    new("", V(5, 5, 7, 1), true, false),
    new("Inno Setup Setup Data (5.6.0)", V(5, 6, 0, 0), false, false),
    new("Inno Setup Setup Data (5.6.0) (u)", V(5, 6, 0, 0), true, false),
    new("Inno Setup Setup Data (5.6.2)", V(5, 6, 2, 0), false, false),
    new("Inno Setup Setup Data (5.6.2) (u)", V(5, 6, 2, 0), true, false),
    new("Inno Setup Setup Data (6.0.0) (u)", V(6, 0, 0, 0), true, false),
    new("Inno Setup Setup Data (6.1.0) (u)", V(6, 1, 0, 0), true, false),
    new("Inno Setup Setup Data (6.3.0)", V(6, 3, 0, 0), true, false),
    new("Inno Setup Setup Data (6.4.0)", V(6, 4, 0, 0), true, false),
    new("Inno Setup Setup Data (6.4.0.1)", V(6, 4, 0, 1), true, false),
    // Later tags, from Inno Setup's own Shared.Struct.pas (issrc), beyond innoextract's table:
    new("Inno Setup Setup Data (6.4.2)", V(6, 4, 2, 0), true, false),
    new("Inno Setup Setup Data (6.4.3)", V(6, 4, 3, 0), true, false),
    new("Inno Setup Setup Data (6.5.0)", V(6, 5, 0, 0), true, false),
    new("Inno Setup Setup Data (6.5.2)", V(6, 5, 2, 0), true, false),
    new("Inno Setup Setup Data (6.6.0)", V(6, 6, 0, 0), true, false),
    new("Inno Setup Setup Data (6.6.1)", V(6, 6, 1, 0), true, false),
    new("Inno Setup Setup Data (6.7.0)", V(6, 7, 0, 0), true, false),
    new("Inno Setup Setup Data (7.0.0.3)", V(7, 0, 0, 3), true, false),
  ];

  /// <summary>
  /// Parses the 64-byte version tag at the start of setup-0.
  /// </summary>
  /// <exception cref="InvalidDataException">Not an Inno Setup tag, or the 16-bit 1.2.x format.</exception>
  public static InnoVersion Parse(ReadOnlySpan<byte> tag64) {
    if (tag64.Length < 64)
      throw new InvalidDataException("Inno Setup version tag is truncated.");
    if (tag64[0] == 'i' && tag64[11] == 0x1A)
      throw new NotSupportedException("Inno Setup 1.2.x setup data (\"i1.2.10--\" tags) is not supported.");

    var end = tag64[..64].IndexOf((byte)0);
    var text = Encoding.ASCII.GetString(tag64[..(end < 0 ? 64 : end)]);
    foreach (var k in KnownVersions)
      if (k.Name.Length > 0 && k.Name == text)
        return new(k.Value, k.Unicode, k.Isx, true);

    if (!text.Contains("Inno Setup", StringComparison.Ordinal))
      throw new InvalidDataException("Not an Inno Setup version tag.");

    // Unknown tag: take the highest a.b.c[.d] that appears in parentheses.
    uint value = 0;
    for (var bracket = text.IndexOf('('); bracket >= 0; bracket = text.IndexOf('(', bracket + 1)) {
      var parsed = ParseNumber(text, bracket + 1);
      if (parsed > value)
        value = parsed;
    }
    if (value == 0)
      throw new InvalidDataException($"Unrecognised Inno Setup version tag \"{text}\".");

    var unicode = value >= V(6, 3, 0) || text.Contains("(u)", StringComparison.Ordinal) || text.Contains("(U)", StringComparison.Ordinal);
    var isx = text.Contains("My Inno Setup Extensions", StringComparison.Ordinal) || text.Contains("with ISX", StringComparison.Ordinal);
    return new(value, unicode, isx, false);
  }

  private static uint ParseNumber(string s, int at) {
    var parts = new int[4];
    var count = 0;
    var i = at;
    while (count < 4) {
      var start = i;
      while (i < s.Length && char.IsAsciiDigit(s[i]))
        ++i;
      if (i == start || i - start > 3)
        break;
      parts[count++] = int.Parse(s.AsSpan(start, i - start), System.Globalization.CultureInfo.InvariantCulture);
      if (i < s.Length && s[i] == 'a' && count == 3)
        ++i;
      if (i >= s.Length || s[i] != '.')
        break;
      ++i;
    }
    return count >= 3 ? V(parts[0], parts[1], parts[2], parts[3]) : 0;
  }

  /// <summary>
  /// True for the tags that more than one layout used (the compiler was changed without the tag).
  /// </summary>
  public bool IsAmbiguous => this.Value == V(1, 3, 21) || this.Value == V(2, 0, 1) || this.Value == V(3, 0, 3)
    || this.Value == V(4, 2, 3) || this.Value == V(5, 3, 10) || this.Value == V(5, 4, 2) || this.Value == V(5, 5, 0)
    || this.Value == V(5, 5, 7) || this.Value == V(5, 5, 7, 1);

  /// <summary>The next known version of the same flavour (ANSI/Unicode, ISX), or null.</summary>
  public InnoVersion? Next() {
    foreach (var k in KnownVersions)
      if (k.Value > this.Value && k.Unicode == this.IsUnicode && k.Isx == this.IsIsx)
        return new(k.Value, k.Unicode, k.Isx, true);
    return null;
  }
}
