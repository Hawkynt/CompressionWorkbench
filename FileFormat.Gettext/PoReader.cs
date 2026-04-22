#pragma warning disable CS1591
using System.Text;

namespace FileFormat.Gettext;

/// <summary>
/// Parses a GNU gettext .po text catalog. Supports msgctxt, msgid / msgid_plural,
/// msgstr / msgstr[n], and multi-line continuation strings. Comment lines
/// (<c>#</c>, <c>#.</c>, <c>#:</c>, <c>#,</c>) are ignored.
/// </summary>
public sealed class PoReader {
  public List<CatalogEntry> Read(ReadOnlySpan<byte> data) {
    var text = Encoding.UTF8.GetString(data);
    // Normalise line endings; keep empty lines as entry separators.
    var lines = text.Replace("\r\n", "\n").Split('\n');
    var entries = new List<CatalogEntry>();

    string? ctx = null;
    string? msgid = null;
    string? msgidPlural = null;
    var msgstr = new Dictionary<int, StringBuilder>(); // key -1 means the singular msgstr
    StringBuilder? current = null;

    void Flush() {
      if (msgid == null) return;
      string singular = msgstr.TryGetValue(-1, out var sb) ? sb.ToString() : "";
      List<string>? plurals = null;
      if (msgidPlural != null) {
        plurals = [];
        for (var i = 0; msgstr.TryGetValue(i, out var ps); ++i)
          plurals.Add(ps.ToString());
        if (plurals.Count > 0) singular = plurals[0];
      }
      entries.Add(new CatalogEntry(entries.Count, ctx, msgid, msgidPlural, singular, plurals));
      ctx = null; msgid = null; msgidPlural = null; msgstr.Clear(); current = null;
    }

    foreach (var rawLine in lines) {
      var line = rawLine.Trim();
      if (line.Length == 0) { Flush(); continue; }
      if (line[0] == '#') continue;

      if (line.StartsWith("msgctxt ", StringComparison.Ordinal)) {
        if (msgid != null) Flush();
        ctx = Unquote(line[8..]);
        current = null;
      } else if (line.StartsWith("msgid_plural ", StringComparison.Ordinal)) {
        msgidPlural = Unquote(line[13..]);
        current = null;
      } else if (line.StartsWith("msgid ", StringComparison.Ordinal)) {
        if (msgid != null) Flush();
        msgid = Unquote(line[6..]);
        current = null;
      } else if (line.StartsWith("msgstr[", StringComparison.Ordinal)) {
        var close = line.IndexOf(']');
        if (close < 0) continue;
        var n = int.Parse(line[7..close]);
        var sb = new StringBuilder(Unquote(line[(close + 2)..]));
        msgstr[n] = sb;
        current = sb;
      } else if (line.StartsWith("msgstr ", StringComparison.Ordinal)) {
        var sb = new StringBuilder(Unquote(line[7..]));
        msgstr[-1] = sb;
        current = sb;
      } else if (line[0] == '"' && current != null) {
        current.Append(Unquote(line));
      }
    }
    Flush();
    return entries;
  }

  private static string Unquote(string s) {
    s = s.Trim();
    if (s.Length < 2 || s[0] != '"' || s[^1] != '"') return s;
    var inner = s[1..^1];
    var sb = new StringBuilder(inner.Length);
    for (var i = 0; i < inner.Length; ++i) {
      var c = inner[i];
      if (c == '\\' && i + 1 < inner.Length) {
        var e = inner[++i];
        sb.Append(e switch {
          'n' => '\n',
          't' => '\t',
          'r' => '\r',
          '\\' => '\\',
          '"' => '"',
          _ => e,
        });
      } else {
        sb.Append(c);
      }
    }
    return sb.ToString();
  }
}
