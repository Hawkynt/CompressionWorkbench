#pragma warning disable CS1591
using System.Xml.Linq;

namespace FileFormat.AppleSparse;

/// <summary>
/// Minimal Apple XML-plist parser scoped to the keys a sparsebundle
/// <c>Info.plist</c> carries (band-size, size, sectors, etc.). Apple plists are
/// strict XML; we use <see cref="XDocument"/> with DTD validation disabled to
/// pull out the <c>&lt;key&gt;name&lt;/key&gt;&lt;integer|string&gt;value&lt;/&gt;</c>
/// pairs we care about.
/// </summary>
internal static class InfoPlistParser {

  /// <summary>
  /// Parses a flat top-level <c>&lt;dict&gt;</c> in an Apple XML plist into a
  /// case-sensitive key→value string map. Nested dicts and arrays are skipped.
  /// Returns an empty dictionary on any parse error so callers can fall back
  /// to detection-only handling without throwing.
  /// </summary>
  public static Dictionary<string, string> ParseTopLevelDict(byte[] xml) {
    ArgumentNullException.ThrowIfNull(xml);
    var result = new Dictionary<string, string>(StringComparer.Ordinal);
    try {
      var settings = new System.Xml.XmlReaderSettings {
        DtdProcessing = System.Xml.DtdProcessing.Ignore,
        XmlResolver = null,
      };
      using var ms = new MemoryStream(xml);
      using var xr = System.Xml.XmlReader.Create(ms, settings);
      var doc = XDocument.Load(xr);
      var dict = doc.Root?.Element("dict");
      if (dict == null) return result;

      var elements = dict.Elements().ToList();
      for (var i = 0; i < elements.Count; i++) {
        if (elements[i].Name.LocalName != "key") continue;
        if (i + 1 >= elements.Count) break;
        var key = elements[i].Value;
        var valueElem = elements[i + 1];
        var localName = valueElem.Name.LocalName;
        if (localName is "integer" or "string" or "real")
          result[key] = valueElem.Value.Trim();
        else if (localName is "true")
          result[key] = "true";
        else if (localName is "false")
          result[key] = "false";
        // dict/array/data/date — skipped
        i++; // consume the value element
      }
    } catch {
      // best-effort — return whatever we parsed before failure
    }
    return result;
  }

  /// <summary>Reads an integer key; returns <paramref name="defaultValue"/> if missing/unparseable.</summary>
  public static long GetInt64(Dictionary<string, string> dict, string key, long defaultValue = 0) {
    ArgumentNullException.ThrowIfNull(dict);
    if (!dict.TryGetValue(key, out var s)) return defaultValue;
    return long.TryParse(s, System.Globalization.NumberStyles.Integer,
      System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : defaultValue;
  }
}
