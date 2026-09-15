#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace FileSystem.Gpfs;

/// <summary>
/// Parses the lab capture's concatenated mmfileid transcript. mmfileid itself is
/// invoked for one GPFS disk/range at a time and its ordinary output does not
/// repeat the disk id, so capture.sh prefixes each invocation with an explicit
/// disk:sector heading.
/// </summary>
internal static class GpfsMmfileidAggregateParser {
  private static readonly Regex Heading = new(
    @"^===== (?<disk>[0-9]+):(?<sector>[0-9]+) =====$",
    RegexOptions.CultureInvariant);

  internal static IReadOnlyList<GpfsMmfileidQueryOracle> Parse(string text) {
    ArgumentNullException.ThrowIfNull(text);

    var result = new List<GpfsMmfileidQueryOracle>();
    var body = new StringBuilder();
    GpfsDiskAddress? query = null;

    foreach (var raw in text.ReplaceLineEndings("\n").Split('\n')) {
      var line = raw.Trim();
      var heading = Heading.Match(line);
      if (heading.Success) {
        Flush();
        query = new GpfsDiskAddress(
          int.Parse(heading.Groups["disk"].Value, NumberStyles.None, CultureInfo.InvariantCulture),
          long.Parse(heading.Groups["sector"].Value, NumberStyles.None, CultureInfo.InvariantCulture));
        continue;
      }

      if (query is not null)
        body.AppendLine(raw);
      else if (line.Length != 0)
        throw new InvalidDataException("mmfileid aggregate output contains data before its first disk:sector heading.");
    }

    Flush();
    return result;

    void Flush() {
      if (query is null)
        return;
      var owners = GpfsOracleParser.ParseMmfileid(body.ToString(), query.Value.DiskId);
      result.Add(new GpfsMmfileidQueryOracle(query.Value, owners));
      body.Clear();
      query = null;
    }
  }
}

internal sealed record GpfsMmfileidQueryOracle(
  GpfsDiskAddress QueryAddress,
  IReadOnlyList<GpfsSectorOwnerOracle> Owners);
