#pragma warning disable CS1591
using System.Globalization;
using System.Text.RegularExpressions;

namespace FileSystem.Gpfs;

/// <summary>
/// Parses only the semantic disk-pointer list printed by IBM's tsdbfs inode
/// diagnostic. The resulting disk/sector pairs are behavioral-oracle facts;
/// this type makes no claim about how those pairs are packed in raw inode bytes.
/// </summary>
internal static class GpfsTsdbfsPointerParser {
  private static readonly Regex Header = new(
    @"^Disk pointers \[(?<count>[0-9]+)\]:$",
    RegexOptions.CultureInvariant);

  // Slot markers have whitespace after their colon ("12:  7:1234"). A disk
  // address has a digit immediately after its colon ("7:1234"), so the two
  // syntaxes remain distinguishable without knowing the pointer width.
  private static readonly Regex SlotMarker = new(
    @"(?:^|\s)(?<slot>[0-9]+):\s+",
    RegexOptions.CultureInvariant);

  private static readonly Regex Address = new(
    @"(?<![0-9])(?<disk>[0-9]+):(?<sector>[0-9]+)(?![0-9])",
    RegexOptions.CultureInvariant);

  internal static GpfsDiskPointerSet Parse(string text) {
    ArgumentNullException.ThrowIfNull(text);
    var lines = text.ReplaceLineEndings("\n").Split('\n');
    var declaredSlotCount = -1;
    var pointers = new List<GpfsDiskPointerOracle>();
    var inPointers = false;

    foreach (var raw in lines) {
      var line = raw.Trim();
      if (!inPointers) {
        var header = Header.Match(line);
        if (!header.Success)
          continue;
        declaredSlotCount = int.Parse(header.Groups["count"].Value, NumberStyles.None, CultureInfo.InvariantCulture);
        inPointers = true;
        continue;
      }

      if (line.StartsWith("trailer:", StringComparison.OrdinalIgnoreCase))
        break;
      if (line.Length == 0)
        continue;

      var slots = SlotMarker.Matches(line);
      if (slots.Count == 0)
        continue;

      for (var i = 0; i < slots.Count; ++i) {
        var marker = slots[i];
        var slot = int.Parse(marker.Groups["slot"].Value, NumberStyles.None, CultureInfo.InvariantCulture);
        var start = marker.Index + marker.Length;
        var end = i + 1 < slots.Count ? slots[i + 1].Index : line.Length;
        var segment = line[start..end];
        var replicas = Address.Matches(segment)
          .Select(static match => new GpfsDiskAddress(
            int.Parse(match.Groups["disk"].Value, NumberStyles.None, CultureInfo.InvariantCulture),
            long.Parse(match.Groups["sector"].Value, NumberStyles.None, CultureInfo.InvariantCulture)))
          .ToArray();
        pointers.Add(new GpfsDiskPointerOracle(slot, replicas));
      }
    }

    if (declaredSlotCount < 0)
      throw new InvalidDataException("tsdbfs inode output contains no Disk pointers section.");
    if (declaredSlotCount == 0)
      return new GpfsDiskPointerSet(0, []);

    var duplicate = pointers.GroupBy(static x => x.SlotIndex).FirstOrDefault(static group => group.Count() > 1);
    if (duplicate is not null)
      throw new InvalidDataException($"tsdbfs disk-pointer output repeats slot {duplicate.Key}.");
    if (pointers.Any(x => x.SlotIndex < 0 || x.SlotIndex >= declaredSlotCount))
      throw new InvalidDataException("tsdbfs disk-pointer output contains a slot outside the declared range.");

    return new GpfsDiskPointerSet(
      declaredSlotCount,
      pointers.OrderBy(static x => x.SlotIndex).ToArray());
  }
}

internal sealed record GpfsDiskPointerOracle(int SlotIndex, IReadOnlyList<GpfsDiskAddress> Replicas);
internal sealed record GpfsDiskPointerSet(int DeclaredSlotCount, IReadOnlyList<GpfsDiskPointerOracle> Pointers);
