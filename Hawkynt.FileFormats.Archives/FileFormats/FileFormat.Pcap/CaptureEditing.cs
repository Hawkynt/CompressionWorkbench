using System.Globalization;
using System.Text;
using Compression.Registry;

namespace FileFormat.Pcap;

/// <summary>
/// What pcap and pcapng share as pseudo-archives: entries named <c>packet_NNNN.bin</c> after their
/// position in the capture, a <c>metadata.ini</c> that describes the capture header, and the rules
/// for turning a create/add input list back into packets.
/// </summary>
internal static class CaptureEditing {

  public const string MetadataName = "metadata.ini";

  /// <summary>Entry name of the packet at <paramref name="index"/>.</summary>
  public static string PacketName(int index) => $"packet_{index:D4}.bin";

  /// <summary>The packet index a <c>packet_NNNN.bin</c> name refers to, or -1.</summary>
  public static int PacketIndex(string name) {
    var n = Path.GetFileName(name.Replace('\\', '/'));
    if (!n.StartsWith("packet_", StringComparison.OrdinalIgnoreCase) || !n.EndsWith(".bin", StringComparison.OrdinalIgnoreCase))
      return -1;
    var digits = n["packet_".Length..^".bin".Length];
    return digits.Length >= 4 && digits.All(char.IsAsciiDigit)
      && int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var i) ? i : -1;
  }

  public static bool IsMetadata(string name)
    => string.Equals(Path.GetFileName(name.Replace('\\', '/')), MetadataName, StringComparison.OrdinalIgnoreCase);

  /// <summary>Reads <c>key = value</c> lines of one INI section (or of every section when null).</summary>
  public static Dictionary<string, string> ParseIni(byte[] ini, string? section) {
    var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    string? current = null;
    foreach (var raw in Encoding.UTF8.GetString(ini).Split('\n')) {
      var line = raw.Trim();
      if (line.Length == 0 || line[0] is ';' or '#') continue;
      if (line[0] == '[' && line[^1] == ']') { current = line[1..^1].Trim(); continue; }
      if (section != null && !string.Equals(current, section, StringComparison.OrdinalIgnoreCase)) continue;
      var eq = line.IndexOf('=');
      if (eq <= 0) continue;
      result[line[..eq].Trim()] = line[(eq + 1)..].Trim();
    }
    return result;
  }

  /// <summary>The leading unsigned number of a value such as <c>1 (Ethernet)</c>.</summary>
  public static uint? LeadingNumber(string? value) {
    if (string.IsNullOrWhiteSpace(value)) return null;
    var digits = new string(value.TrimStart().TakeWhile(char.IsAsciiDigit).ToArray());
    return uint.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var v) ? v : null;
  }

  /// <summary>Timestamp a packet gets from its input: the input's write time, else the Unix epoch.</summary>
  public static DateTimeOffset TimestampOf(ArchiveInputInfo input)
    => input.Metadata?.LastWriteTimeUtc ?? DateTimeOffset.UnixEpoch;

  /// <summary>A validated packet input: the slot it replaces (-1 = append), its bytes and timestamp.</summary>
  public readonly record struct PacketInput(int ReplaceIndex, byte[] Data, DateTimeOffset Timestamp);

  /// <summary>
  /// Splits <paramref name="inputs"/> into the optional metadata.ini and the packets, in input order.
  /// Directories carry nothing a capture can hold and are skipped.
  /// </summary>
  public static (byte[]? Metadata, List<PacketInput> Packets) Split(IReadOnlyList<ArchiveInputInfo> inputs, int existingPackets) {
    ArgumentNullException.ThrowIfNull(inputs);
    byte[]? metadata = null;
    var packets = new List<PacketInput>();
    foreach (var input in inputs) {
      if (input.IsDirectory) continue;
      if (IsMetadata(input.ArchiveName)) {
        metadata = input.ReadContent();
        continue;
      }
      var index = PacketIndex(input.ArchiveName);
      packets.Add(new PacketInput(index >= 0 && index < existingPackets ? index : -1, input.ReadContent(), TimestampOf(input)));
    }
    return (metadata, packets);
  }

  /// <summary>Replaces <paramref name="archive"/>'s content with <paramref name="image"/>.</summary>
  public static void Overwrite(Stream archive, byte[] image) {
    archive.Position = 0;
    archive.SetLength(0);
    archive.Write(image);
    archive.Flush();
  }

  public static byte[] ReadAll(Stream archive) {
    ArgumentNullException.ThrowIfNull(archive);
    archive.Position = 0;
    using var ms = new MemoryStream();
    archive.CopyTo(ms);
    return ms.ToArray();
  }

  /// <summary>
  /// The set of packet indices <paramref name="entryNames"/> names; metadata.ini is refused, since
  /// the capture header is not an entry that can go away.
  /// </summary>
  public static HashSet<int> IndicesToRemove(string[] entryNames, string format) {
    ArgumentNullException.ThrowIfNull(entryNames);
    var drop = new HashSet<int>();
    foreach (var name in entryNames) {
      if (IsMetadata(name))
        throw new NotSupportedException($"{format}: metadata.ini describes the capture header, which cannot be removed.");
      var i = PacketIndex(name);
      if (i >= 0) drop.Add(i);
    }
    return drop;
  }
}
