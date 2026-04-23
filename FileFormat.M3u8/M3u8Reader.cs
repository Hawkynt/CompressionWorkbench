#pragma warning disable CS1591
using System.Globalization;

namespace FileFormat.M3u8;

/// <summary>
/// Reader for HLS M3U8 playlists per RFC 8216.
/// </summary>
/// <remarks>
/// Handles both master playlists (containing <c>#EXT-X-STREAM-INF</c> variant entries
/// with sibling URIs) and media playlists (containing <c>#EXTINF</c> per-segment entries
/// with sibling URIs). The <c>IsMaster</c> flag is true when at least one
/// <c>#EXT-X-STREAM-INF</c> tag is present.
/// </remarks>
public sealed class M3u8Reader {

  /// <summary>
  /// One variant entry from a master playlist (the <c>#EXT-X-STREAM-INF</c> attribute
  /// pairs paired with the URI on the next non-tag line).
  /// </summary>
  public sealed record VariantStream(IReadOnlyDictionary<string, string> Attributes, string Uri);

  /// <summary>One segment entry from a media playlist (<c>#EXTINF</c> + URI).</summary>
  public sealed record Segment(double DurationSeconds, string? Title, string Uri);

  /// <summary>Parsed playlist contents.</summary>
  public sealed record Playlist(
    bool IsMaster,
    int? Version,
    int? TargetDurationSeconds,
    int? MediaSequence,
    string? PlaylistType,
    bool EndList,
    IReadOnlyList<VariantStream> Variants,
    IReadOnlyList<Segment> Segments,
    string RawText);

  /// <summary>Parses a UTF-8 M3U8 text body. Throws if the leading <c>#EXTM3U</c> tag is missing.</summary>
  public static Playlist Read(string text) {
    var lines = text.Split('\n');
    if (lines.Length == 0 || !lines[0].TrimEnd('\r').StartsWith("#EXTM3U", StringComparison.Ordinal))
      throw new InvalidDataException("M3U8: missing '#EXTM3U' header on first line.");

    int? version = null;
    int? target = null;
    int? mediaSeq = null;
    string? playlistType = null;
    var endList = false;
    var variants = new List<VariantStream>();
    var segments = new List<Segment>();

    // State for the two-line tag-then-URI pattern used by both master EXT-X-STREAM-INF
    // and media EXTINF entries.
    IReadOnlyDictionary<string, string>? pendingVariantAttrs = null;
    double? pendingExtInfDur = null;
    string? pendingExtInfTitle = null;

    foreach (var raw in lines) {
      var line = raw.TrimEnd('\r').Trim();
      if (line.Length == 0) continue;

      if (line.StartsWith('#')) {
        if (line.StartsWith("#EXT-X-VERSION:", StringComparison.Ordinal)) {
          if (int.TryParse(line[15..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
            version = v;
        } else if (line.StartsWith("#EXT-X-TARGETDURATION:", StringComparison.Ordinal)) {
          if (int.TryParse(line[22..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var t))
            target = t;
        } else if (line.StartsWith("#EXT-X-MEDIA-SEQUENCE:", StringComparison.Ordinal)) {
          if (int.TryParse(line[22..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var m))
            mediaSeq = m;
        } else if (line.StartsWith("#EXT-X-PLAYLIST-TYPE:", StringComparison.Ordinal)) {
          playlistType = line[21..].Trim();
        } else if (line == "#EXT-X-ENDLIST") {
          endList = true;
        } else if (line.StartsWith("#EXT-X-STREAM-INF:", StringComparison.Ordinal)) {
          pendingVariantAttrs = ParseAttributes(line[18..]);
        } else if (line.StartsWith("#EXTINF:", StringComparison.Ordinal)) {
          // #EXTINF:<duration>,<title?>
          var rest = line[8..];
          var comma = rest.IndexOf(',');
          var durStr = comma >= 0 ? rest[..comma] : rest;
          var title = comma >= 0 ? rest[(comma + 1)..] : null;
          double.TryParse(durStr.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var dur);
          pendingExtInfDur = dur;
          pendingExtInfTitle = string.IsNullOrEmpty(title) ? null : title;
        }
        // Other tags (#EXT-X-KEY, #EXT-X-MEDIA, #EXT-X-DISCONTINUITY, #EXT-X-PROGRAM-DATE-TIME,
        // #EXT-X-MAP, #EXT-X-BYTERANGE, etc.) are intentionally not surfaced — segment
        // attribution to keys/maps would require a much larger state machine. We preserve
        // the raw text so consumers can re-parse if needed.
        continue;
      }

      // URI line — pair with any pending tag.
      if (pendingVariantAttrs != null) {
        variants.Add(new VariantStream(pendingVariantAttrs, line));
        pendingVariantAttrs = null;
      } else if (pendingExtInfDur.HasValue) {
        segments.Add(new Segment(pendingExtInfDur.Value, pendingExtInfTitle, line));
        pendingExtInfDur = null;
        pendingExtInfTitle = null;
      }
      // URI lines without a preceding tag are ignored (non-conforming playlists).
    }

    var isMaster = variants.Count > 0;
    return new Playlist(
      IsMaster: isMaster,
      Version: version,
      TargetDurationSeconds: target,
      MediaSequence: mediaSeq,
      PlaylistType: playlistType,
      EndList: endList,
      Variants: variants,
      Segments: segments,
      RawText: text);
  }

  /// <summary>
  /// Parses an HLS attribute list — comma-separated <c>KEY=VALUE</c> pairs where values
  /// may be quoted (containing commas) or bare. Hex values prefixed with <c>0x</c> and
  /// resolution tokens like <c>1920x1080</c> stay as-is.
  /// </summary>
  internal static IReadOnlyDictionary<string, string> ParseAttributes(string attrs) {
    var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    var i = 0;
    while (i < attrs.Length) {
      // Skip whitespace + leading separator.
      while (i < attrs.Length && (attrs[i] == ' ' || attrs[i] == ',')) i++;
      if (i >= attrs.Length) break;

      // Key = up to '=' or end.
      var keyStart = i;
      while (i < attrs.Length && attrs[i] != '=' && attrs[i] != ',') i++;
      var key = attrs[keyStart..i].Trim();
      if (i >= attrs.Length || attrs[i] != '=') {
        // Bare token — store with empty value.
        if (key.Length > 0) result[key] = "";
        continue;
      }
      i++; // skip '='

      string value;
      if (i < attrs.Length && attrs[i] == '"') {
        // Quoted value — read until closing quote.
        i++;
        var valStart = i;
        while (i < attrs.Length && attrs[i] != '"') i++;
        value = attrs[valStart..i];
        if (i < attrs.Length) i++; // skip closing quote
      } else {
        var valStart = i;
        while (i < attrs.Length && attrs[i] != ',') i++;
        value = attrs[valStart..i].Trim();
      }
      if (key.Length > 0) result[key] = value;
    }
    return result;
  }
}
