#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Pcapng;

/// <summary>
/// Pseudo-archive descriptor for the modern pcapng (PCAP Next Generation) capture
/// format. Each Enhanced/Simple Packet Block is exposed as its own entry. The first
/// 100 packets are emitted verbatim; larger captures are tail-truncated and a note
/// is left in <c>metadata.ini</c>.
/// </summary>
public sealed class PcapngFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveInMemoryExtract {
  private const int MaxPackets = 100;

  public string Id => "Pcapng";
  public string DisplayName => "PCAP Next Generation";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".pcapng";
  public IReadOnlyList<string> Extensions => [".pcapng", ".ntar"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    // Block type 0x0A0D0D0A at offset 0 — same in both byte orders (palindrome).
    new([0x0A, 0x0D, 0x0D, 0x0A], Confidence: 0.95),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "PCAP Next Generation block-structured packet capture (multiple sections, multiple interfaces).";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) =>
    BuildEntries(stream).Select((e, i) => new ArchiveEntryInfo(
      Index: i, Name: e.Name,
      OriginalSize: e.Data.Length, CompressedSize: e.Data.Length,
      Method: "stored", IsDirectory: false, IsEncrypted: false,
      LastModified: e.Timestamp, Kind: e.Kind)).ToList();

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    foreach (var e in BuildEntries(stream)) {
      if (files != null && files.Length > 0 && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, e.Data);
    }
  }

  public void ExtractEntry(Stream input, string entryName, Stream output, string? password) {
    foreach (var e in BuildEntries(input))
      if (e.Name.Equals(entryName, StringComparison.OrdinalIgnoreCase)) {
        output.Write(e.Data);
        return;
      }
    throw new FileNotFoundException($"Entry not found: {entryName}");
  }

  private static IReadOnlyList<(string Name, string Kind, DateTime? Timestamp, byte[] Data)> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var capture = PcapngReader.Read(ms.GetBuffer().AsSpan(0, (int)ms.Length));

    var total = capture.Packets.Count;
    var exposed = Math.Min(total, MaxPackets);
    var truncated = total > MaxPackets;

    var result = new List<(string, string, DateTime?, byte[])> {
      ("metadata.ini", "Tag", null, BuildMetadata(capture, total, truncated)),
    };

    for (var i = 0; i < exposed; i++) {
      var p = capture.Packets[i];
      var ts = p.TimestampRaw == 0 ? (DateTime?)null : p.ToDateTime();
      result.Add(($"packet_{i:D4}.bin", "Payload", ts, p.Data));
    }
    return result;
  }

  private static byte[] BuildMetadata(PcapngReader.Capture c, int totalPackets, bool truncated) {
    var sb = new StringBuilder();
    sb.AppendLine("[pcapng]");
    sb.Append("version = ").Append(c.VersionMajor).Append('.').Append(c.VersionMinor).AppendLine();
    sb.Append("endian = ").AppendLine(c.LittleEndian ? "little" : "big");
    sb.Append("interface_count = ").Append(c.Interfaces.Count).Append(CultureInfo.InvariantCulture, $"\n");
    sb.Append("total_packet_count = ").Append(totalPackets).Append(CultureInfo.InvariantCulture, $"\n");
    if (truncated) {
      sb.Append("exposed_packets = ").Append(MaxPackets).Append(CultureInfo.InvariantCulture, $"\n");
      sb.AppendLine("note = capture truncated for listing; remaining packets omitted");
    }
    for (var i = 0; i < c.Interfaces.Count; i++) {
      var iface = c.Interfaces[i];
      sb.Append(CultureInfo.InvariantCulture, $"\n[interface_{i}]\n");
      sb.Append("link_type = ").Append(iface.LinkType).Append(' ').AppendLine(LinkTypeName(iface.LinkType));
      sb.Append("snaplen = ").Append(iface.Snaplen).Append(CultureInfo.InvariantCulture, $"\n");
    }
    return Encoding.UTF8.GetBytes(sb.ToString());
  }

  private static string LinkTypeName(uint lt) => lt switch {
    1 => "(Ethernet)",
    101 => "(raw IP)",
    105 => "(IEEE 802.11)",
    113 => "(Linux cooked)",
    127 => "(IEEE 802.11 radiotap)",
    228 => "(IPv4)",
    229 => "(IPv6)",
    _ => string.Empty,
  };
}
