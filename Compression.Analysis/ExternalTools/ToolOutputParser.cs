#pragma warning disable CS1591

using System.Globalization;
using System.Text.RegularExpressions;

namespace Compression.Analysis.ExternalTools;

/// <summary>
/// An entry parsed from 7z archive listing output.
/// </summary>
public sealed class SevenZipListEntry {
  /// <summary>Path/name of the entry within the archive.</summary>
  public required string Path { get; init; }

  /// <summary>Uncompressed size in bytes (-1 if unknown).</summary>
  public long Size { get; init; } = -1;

  /// <summary>Compressed size in bytes (-1 if unknown).</summary>
  public long PackedSize { get; init; } = -1;

  /// <summary>Compression method (e.g. "LZMA2", "Deflate"), or null if not reported.</summary>
  public string? Method { get; init; }

  /// <summary>CRC32 checksum as reported by 7z, or null.</summary>
  public string? Crc { get; init; }

  /// <summary>Whether this entry is a directory.</summary>
  public bool IsDirectory { get; init; }
}

/// <summary>
/// Result from parsing the Unix <c>file</c> command output.
/// </summary>
public sealed class FileCommandResult {
  /// <summary>MIME type (e.g. "application/gzip").</summary>
  public required string MimeType { get; init; }

  /// <summary>Human-readable description (e.g. "gzip compressed data").</summary>
  public required string Description { get; init; }
}

/// <summary>
/// An entry parsed from binwalk scan output.
/// </summary>
public sealed class BinwalkEntry {
  /// <summary>Offset in the file where the signature was found.</summary>
  public long Offset { get; init; }

  /// <summary>Description of what was found at this offset.</summary>
  public required string Description { get; init; }
}

/// <summary>
/// Parses structured output from well-known external tools.
/// </summary>
public static partial class ToolOutputParser {

  // ── 7z list output (7z l -slt) ────────────────────────────────────

  /// <summary>
  /// Parses output from <c>7z l -slt &lt;archive&gt;</c> (technical listing format).
  /// Each entry is separated by blank lines and has key = value lines.
  /// </summary>
  public static List<SevenZipListEntry> Parse7zList(string output) {
    var entries = new List<SevenZipListEntry>();
    if (string.IsNullOrWhiteSpace(output))
      return entries;

    // Split into blocks separated by blank lines.
    var blocks = output.Split(["\r\n\r\n", "\n\n"], StringSplitOptions.RemoveEmptyEntries);

    foreach (var block in blocks) {
      var props = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
      foreach (var line in block.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)) {
        var eqIdx = line.IndexOf('=');
        if (eqIdx <= 0)
          continue;

        var key = line[..eqIdx].Trim();
        var value = line[(eqIdx + 1)..].Trim();
        props[key] = value;
      }

      // Only include blocks that have a "Path" property (skip header/footer blocks).
      if (!props.TryGetValue("Path", out var path) || string.IsNullOrEmpty(path))
        continue;

      // Skip the archive header entry (has "Type" but no "Size" or "Folder").
      if (props.ContainsKey("Type") && !props.ContainsKey("Folder") && !props.ContainsKey("Size"))
        continue;

      var entry = new SevenZipListEntry {
        Path = path,
        Size = props.TryGetValue("Size", out var sizeStr) && long.TryParse(sizeStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var size) ? size : -1,
        PackedSize = props.TryGetValue("Packed Size", out var packedStr) && long.TryParse(packedStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var packed) ? packed : -1,
        Method = props.GetValueOrDefault("Method"),
        Crc = props.GetValueOrDefault("CRC"),
        IsDirectory = props.TryGetValue("Folder", out var folder) && folder == "+"
      };

      entries.Add(entry);
    }

    return entries;
  }

  // ── 7z simple list output (7z l) ──────────────────────────────────

  /// <summary>
  /// Parses output from <c>7z l &lt;archive&gt;</c> (table-format listing).
  /// Returns a simpler list with path, size, and method.
  /// </summary>
  public static List<SevenZipListEntry> Parse7zSimpleList(string output) {
    var entries = new List<SevenZipListEntry>();
    if (string.IsNullOrWhiteSpace(output))
      return entries;

    var lines = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
    var inTable = false;
    var headerPassed = 0;

    foreach (var line in lines) {
      // Table starts after the "---" separator line.
      if (line.StartsWith("---", StringComparison.Ordinal)) {
        headerPassed++;
        if (headerPassed == 1)
          inTable = true;
        else
          inTable = false; // Footer separator.
        continue;
      }

      if (!inTable || line.Length < 25)
        continue;

      // Table format: "yyyy-MM-dd HH:mm:ss ..... SIZEFIELD  NAMEPART"
      // The size and name are the key parts we can reliably extract.
      var match = SevenZipSimpleLineRegex().Match(line);
      if (!match.Success)
        continue;

      var sizeStr = match.Groups["size"].Value.Trim();
      var name = match.Groups["name"].Value.Trim();
      var attr = match.Groups["attr"].Value.Trim();

      entries.Add(new SevenZipListEntry {
        Path = name,
        Size = long.TryParse(sizeStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var size) ? size : -1,
        IsDirectory = attr.Length > 0 && attr[0] == 'D'
      });
    }

    return entries;
  }

  // ── file command output ───────────────────────────────────────────

  /// <summary>
  /// Parses output from <c>file --mime &lt;path&gt;</c>.
  /// Expected format: <c>/path/to/file: mime/type; charset=...</c>
  /// </summary>
  public static FileCommandResult? ParseFileOutput(string output) {
    if (string.IsNullOrWhiteSpace(output))
      return null;

    // Handle both "file --mime" and "file" (without --mime).
    var line = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
    if (line == null)
      return null;

    // Format: "filename: description" or "filename: mime/type; charset=..."
    var colonIdx = line.IndexOf(':');
    if (colonIdx < 0)
      return null;

    var rest = line[(colonIdx + 1)..].Trim();

    // Check if it's MIME output (contains a slash for mime type).
    var semiIdx = rest.IndexOf(';');
    var mimeCandidate = semiIdx >= 0 ? rest[..semiIdx].Trim() : rest.Trim();

    if (mimeCandidate.Contains('/')) {
      return new FileCommandResult {
        MimeType = mimeCandidate,
        Description = rest
      };
    }

    // Plain file output (no --mime): description only.
    return new FileCommandResult {
      MimeType = string.Empty,
      Description = rest
    };
  }

  /// <summary>
  /// Parses output from <c>file -b --mime-type &lt;path&gt;</c>.
  /// Expected format: just the mime type (e.g. <c>application/gzip</c>).
  /// </summary>
  public static string? ParseFileMimeType(string output) {
    if (string.IsNullOrWhiteSpace(output))
      return null;

    var trimmed = output.Trim();
    return trimmed.Contains('/') ? trimmed.Split(['\r', '\n'])[0].Trim() : null;
  }

  // ── binwalk output ────────────────────────────────────────────────

  /// <summary>
  /// Parses output from <c>binwalk &lt;file&gt;</c>.
  /// Expected format:
  /// <code>
  /// DECIMAL       HEXADECIMAL     DESCRIPTION
  /// -----------------------------------------------
  /// 0             0x0             Zip archive data, ...
  /// 1234          0x4D2           gzip compressed data, ...
  /// </code>
  /// </summary>
  public static List<BinwalkEntry> ParseBinwalkOutput(string output) {
    var entries = new List<BinwalkEntry>();
    if (string.IsNullOrWhiteSpace(output))
      return entries;

    var lines = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
    var pastHeader = false;

    foreach (var line in lines) {
      if (line.StartsWith("---", StringComparison.Ordinal)) {
        pastHeader = true;
        continue;
      }

      if (!pastHeader)
        continue;

      var match = BinwalkLineRegex().Match(line);
      if (!match.Success)
        continue;

      if (!long.TryParse(match.Groups["offset"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var offset))
        continue;

      entries.Add(new BinwalkEntry {
        Offset = offset,
        Description = match.Groups["desc"].Value.Trim()
      });
    }

    return entries;
  }

  // ── TrID output ───────────────────────────────────────────────────

  /// <summary>
  /// Parses output from <c>trid &lt;file&gt;</c>.
  /// Returns list of (confidence percentage, description).
  /// </summary>
  public static List<(double Confidence, string Description)> ParseTridOutput(string output) {
    var results = new List<(double, string)>();
    if (string.IsNullOrWhiteSpace(output))
      return results;

    var lines = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
    foreach (var line in lines) {
      var match = TridLineRegex().Match(line);
      if (!match.Success)
        continue;

      if (!double.TryParse(match.Groups["pct"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var confidence))
        continue;

      results.Add((confidence, match.Groups["desc"].Value.Trim()));
    }

    return results;
  }

  // ── Compiled regexes ──────────────────────────────────────────────

  [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}\s+\d{2}:\d{2}:\d{2}\s+(?<attr>\S+)\s+(?<size>\d+)\s+\d*\s+(?<name>.+)$")]
  private static partial Regex SevenZipSimpleLineRegex();

  [GeneratedRegex(@"^(?<offset>\d+)\s+0x[0-9A-Fa-f]+\s+(?<desc>.+)$")]
  private static partial Regex BinwalkLineRegex();

  [GeneratedRegex(@"^\s*(?<pct>[\d.]+)%\s+\(\.[\w/]+\)\s+(?<desc>.+)$")]
  private static partial Regex TridLineRegex();
}
