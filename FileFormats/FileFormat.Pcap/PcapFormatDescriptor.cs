#pragma warning disable CS1591
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Pcap;

/// <summary>
/// Descriptor for libpcap capture files.  Surfaces each raw link-layer frame as
/// a separate archive entry.  To keep listings manageable the first 100 packets
/// are exposed verbatim; larger captures are tail-truncated and a note is left
/// in <c>metadata.ini</c>.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://www.tcpdump.org</c> — tcpdump/libpcap project — the pcap-savefile(5) man page documents the capture file layout</description></item>
///   <item><description><c>https://github.com/the-tcpdump-group/libpcap</c> — canonical implementation</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/Pcap</c> — Wikipedia</description></item>
/// </list>
/// </summary>
public sealed class PcapFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveInMemoryExtract {
  private const int MaxPackets = 100;

    /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "Pcap";
    /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "PCAP (libpcap capture)";
    /// <summary>
  /// Gets the category.
  /// </summary>
public FormatCategory Category => FormatCategory.Archive;
    /// <summary>
  /// Gets the capabilities.
  /// </summary>
public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
    /// <summary>
  /// Gets the default extension.
  /// </summary>
public string DefaultExtension => ".pcap";
    /// <summary>
  /// Gets the extensions.
  /// </summary>
public IReadOnlyList<string> Extensions => [".pcap", ".cap"];
    /// <summary>
  /// Gets the compound extensions.
  /// </summary>
public IReadOnlyList<string> CompoundExtensions => [];
    /// <summary>
  /// Gets the magic signatures.
  /// </summary>
public IReadOnlyList<MagicSignature> MagicSignatures => [
    // Raw leading-byte sequences for the four recognised libpcap global-header magic
    // constants.  The reader distinguishes byte order and timestamp resolution
    // by which of these patterns appears at offset 0.
    new([0xA1, 0xB2, 0xC3, 0xD4], Confidence: 0.95), // little-endian, microsecond
    new([0xD4, 0xC3, 0xB2, 0xA1], Confidence: 0.95), // big-endian,    microsecond
    new([0xA1, 0xB2, 0x3C, 0x4D], Confidence: 0.95), // little-endian, nanosecond
    new([0x4D, 0x3C, 0xB2, 0xA1], Confidence: 0.95), // big-endian,    nanosecond
  ];
    /// <summary>
  /// Gets the methods.
  /// </summary>
public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
    /// <summary>
  /// Gets the tar compression format id.
  /// </summary>
public string? TarCompressionFormatId => null;
    /// <summary>
  /// Gets the family.
  /// </summary>
public AlgorithmFamily Family => AlgorithmFamily.Archive;
    /// <summary>
  /// Gets the description.
  /// </summary>
public string Description => "Classic libpcap packet capture: global header + per-packet link-layer frames.";

    /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
public List<ArchiveEntryInfo> List(Stream stream, string? password) =>
    BuildEntries(stream).Select((e, i) => new ArchiveEntryInfo(
      Index: i, Name: e.Name,
      OriginalSize: e.Data.Length, CompressedSize: e.Data.Length,
      Method: "stored", IsDirectory: false, IsEncrypted: false,
      LastModified: e.Timestamp, Kind: e.Kind)).ToList();

  /// <summary>
  /// Opens a single entry as a bounded read-only stream. Each entry's
  /// decoded byte buffer is produced by <see cref="BuildEntries"/> and
  /// wrapped in a
  /// <see cref="Compression.Registry.Streaming.BoundedEntryStream"/> sized
  /// to its logical length.
  /// </summary>
  public Stream OpenEntry(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryName);
    if (archive.CanSeek) archive.Position = 0;
    foreach (var e in BuildEntries(archive)) {
      if (!string.Equals(e.Name, entryName, StringComparison.OrdinalIgnoreCase)) continue;
      return new Compression.Registry.Streaming.BoundedEntryStream(
        new MemoryStream(e.Data, writable: false), e.Data.Length, leaveOpen: false);
    }
    return new Compression.Registry.Streaming.BoundedEntryStream(
      new MemoryStream(System.Array.Empty<byte>(), writable: false), 0, leaveOpen: false);
  }

  /// <summary>Native in-memory single-entry extraction routed through the bounded <see cref="OpenEntry"/>.</summary>
  public byte[] ExtractEntryToMemory(Stream archive, string entryName, string? password) {
    using var s = this.OpenEntry(archive, entryName, password);
    using var memoryStream = new MemoryStream();
    s.CopyTo(memoryStream);
    return memoryStream.ToArray();
  }

    /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    foreach (var e in BuildEntries(stream)) {
      if (files != null && files.Length > 0 && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, e.Data);
    }
  }

    /// <summary>
  /// Performs the extract entry operation.
  /// </summary>
public void ExtractEntry(Stream input, string entryName, Stream output, string? password) {
    foreach (var e in BuildEntries(input)) {
      if (e.Name.Equals(entryName, StringComparison.OrdinalIgnoreCase)) {
        output.Write(e.Data);
        return;
      }
    }
    throw new FileNotFoundException($"Entry not found: {entryName}");
  }

  // ── Builder ─────────────────────────────────────────────────────────────

  private static IReadOnlyList<(string Name, string Kind, DateTime? Timestamp, byte[] Data)>
      BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var capture = PcapReader.Read(ms.GetBuffer().AsSpan(0, (int)ms.Length));

    var total = capture.Packets.Count;
    var exposed = Math.Min(total, MaxPackets);
    var truncated = total > MaxPackets;

    var result = new List<(string, string, DateTime?, byte[])> {
      ("metadata.ini", "Tag", null, BuildMetadata(capture, total, truncated)),
    };

    for (var i = 0; i < exposed; i++) {
      var p = capture.Packets[i];
      var ts = DateTime.UnixEpoch.AddSeconds(p.TimestampSeconds)
        .AddTicks(capture.Nanosecond
          ? p.TimestampFraction / 100                       // ns → 100ns ticks
          : p.TimestampFraction * 10);                      // µs → 100ns ticks
      result.Add(($"packet_{i:D4}.bin", "Payload", ts, p.Data));
    }
    return result;
  }

  private static byte[] BuildMetadata(PcapReader.Capture c, int totalPackets, bool truncated) {
    var sb = new StringBuilder();
    sb.AppendLine("[pcap]");
    sb.Append("version = ").Append(c.VersionMajor).Append('.').Append(c.VersionMinor).AppendLine();
    sb.Append("link_type = ").Append(c.LinkType).Append(' ').AppendLine(LinkTypeName(c.LinkType));
    sb.Append("snaplen = ").Append(c.Snaplen).AppendLine();
    sb.Append("endian = ").AppendLine(c.LittleEndian ? "little" : "big");
    sb.Append("timestamp_resolution = ").AppendLine(c.Nanosecond ? "nanosecond" : "microsecond");
    sb.Append("total_packet_count = ").Append(totalPackets).AppendLine();
    if (truncated) {
      sb.Append("exposed_packets = ").Append(MaxPackets).AppendLine();
      sb.AppendLine("note = capture truncated for listing; remaining packets omitted");
    }
    return Encoding.UTF8.GetBytes(sb.ToString());
  }

  private static string LinkTypeName(uint lt) => lt switch {
    1 => "(Ethernet)",
    101 => "(raw IP)",
    105 => "(IEEE 802.11)",
    113 => "(Linux cooked)",
    127 => "(IEEE 802.11 radiotap)",
    _ => string.Empty,
  };
}
