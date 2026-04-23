#pragma warning disable CS1591
using System.Globalization;
using System.Text;

namespace FileFormat.VobSub;

/// <summary>
/// Reader for VobSub DVD subtitle pairs (<c>.idx</c> + <c>.sub</c>).
/// </summary>
/// <remarks>
/// The <c>.idx</c> file is UTF-8 text describing palette, frame size, language streams,
/// and per-frame <c>timestamp: HH:MM:SS:mmm, filepos: HEX</c> entries. The sibling
/// <c>.sub</c> is a stream of MPEG-2 Private Stream 1 PES packets, each containing
/// one subtitle bitmap. Reference: MPlayer/libvobsub source.
/// </remarks>
public sealed class VobSubReader {

  /// <summary>One subtitle frame entry from the .idx file.</summary>
  public sealed record IndexEntry(TimeSpan Timestamp, long FilePos);

  /// <summary>Parsed contents of an .idx file.</summary>
  public sealed record Index(
    int Width,
    int Height,
    IReadOnlyList<uint> Palette,
    string? Language,
    IReadOnlyList<IndexEntry> Entries);

  /// <summary>Header/Body bundle of a parsed VobSub pair.</summary>
  public sealed record Pair(Index Index, IReadOnlyList<byte[]> Frames);

  /// <summary>Parses an .idx text file. Unknown directives are ignored.</summary>
  public static Index ReadIndex(string text) {
    var palette = new List<uint>();
    var entries = new List<IndexEntry>();
    int width = 0, height = 0;
    string? lang = null;

    var lines = text.Split('\n');
    if (lines.Length == 0 || !lines[0].StartsWith("# VobSub index file", StringComparison.Ordinal))
      throw new InvalidDataException("VobSub: missing '# VobSub index file' header.");

    foreach (var raw in lines) {
      var line = raw.TrimEnd('\r').Trim();
      if (line.Length == 0 || line[0] == '#') continue;

      if (line.StartsWith("size:", StringComparison.OrdinalIgnoreCase)) {
        // size: 720x480
        var v = line[5..].Trim();
        var x = v.IndexOf('x');
        if (x > 0
            && int.TryParse(v[..x], NumberStyles.Integer, CultureInfo.InvariantCulture, out var w)
            && int.TryParse(v[(x + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var h)) {
          width = w; height = h;
        }
      } else if (line.StartsWith("palette:", StringComparison.OrdinalIgnoreCase)) {
        // palette: 000000, ffffff, ... (16 hex RGB triples)
        foreach (var tok in line[8..].Split(',')) {
          var t = tok.Trim();
          if (t.Length > 0 && uint.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var c))
            palette.Add(c);
        }
      } else if (line.StartsWith("id:", StringComparison.OrdinalIgnoreCase)) {
        // id: en, index: 0
        var rest = line[3..].Trim();
        var comma = rest.IndexOf(',');
        lang = comma >= 0 ? rest[..comma].Trim() : rest;
      } else if (line.StartsWith("timestamp:", StringComparison.OrdinalIgnoreCase)) {
        // timestamp: 00:00:01:234, filepos: 0000000000
        var rest = line[10..].Trim();
        var comma = rest.IndexOf(',');
        if (comma < 0) continue;
        var tsStr = rest[..comma].Trim();
        var fp = rest[(comma + 1)..].Trim();
        var fpIdx = fp.IndexOf(':');
        if (fpIdx < 0) continue;
        var fpHex = fp[(fpIdx + 1)..].Trim();
        if (TryParseTimestamp(tsStr, out var ts)
            && long.TryParse(fpHex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var pos))
          entries.Add(new IndexEntry(ts, pos));
      }
    }

    return new Index(width, height, palette, lang, entries);
  }

  private static bool TryParseTimestamp(string s, out TimeSpan ts) {
    // VobSub format: HH:MM:SS:mmm — note ':' separator before milliseconds, not '.'.
    ts = default;
    var parts = s.Split(':');
    if (parts.Length != 4) return false;
    if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var hh)) return false;
    if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var mm)) return false;
    if (!int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ss)) return false;
    if (!int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms)) return false;
    ts = new TimeSpan(0, hh, mm, ss, ms);
    return true;
  }

  /// <summary>
  /// Slices the .sub byte stream into per-subtitle byte arrays using the index entries'
  /// <c>filepos</c> values as boundaries. The bytes between consecutive <c>filepos</c>
  /// values form one frame; the final frame extends to end-of-file.
  /// </summary>
  public static IReadOnlyList<byte[]> SliceFrames(Index index, ReadOnlySpan<byte> sub) {
    var frames = new List<byte[]>(index.Entries.Count);
    for (var i = 0; i < index.Entries.Count; i++) {
      var start = index.Entries[i].FilePos;
      var end = i + 1 < index.Entries.Count ? index.Entries[i + 1].FilePos : sub.Length;
      // Clamp (rather than drop) out-of-range offsets so a malformed index still
      // produces a 1:1 frame list — callers can detect truncation via zero-length frames.
      if (start < 0) start = 0;
      if (start > sub.Length) start = sub.Length;
      if (end > sub.Length) end = sub.Length;
      if (end <= start) { frames.Add([]); continue; }
      frames.Add(sub.Slice((int)start, (int)(end - start)).ToArray());
    }
    return frames;
  }

  /// <summary>
  /// Convenience overload: reads the .idx text from <paramref name="idxText"/> and slices
  /// the supplied .sub byte stream into per-frame byte arrays.
  /// </summary>
  public static Pair Read(string idxText, ReadOnlySpan<byte> subBytes) {
    var idx = ReadIndex(idxText);
    var frames = SliceFrames(idx, subBytes);
    return new Pair(idx, frames);
  }

  /// <summary>UTF-8 conveniance wrapper for tests / disk-backed scenarios.</summary>
  public static Pair Read(byte[] idxBytes, ReadOnlySpan<byte> subBytes)
    => Read(Encoding.UTF8.GetString(idxBytes), subBytes);
}
