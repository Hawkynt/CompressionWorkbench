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
/// <para>Create writes a little-endian format-2.4 capture, one packet per input file, with the
/// header taken from an input <c>metadata.ini</c> (the keys this reader emits) or the
/// <c>linktype</c> / <c>snaplen</c> / <c>nanosecond</c> options; a packet's timestamp is its
/// input's write time. Edits keep the global header and every untouched record byte for byte:
/// an input named <c>packet_NNNN.bin</c> replaces that packet's data (timestamp kept), any other
/// input appends a packet, and removal drops the named packets.</para>
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://www.tcpdump.org</c> — tcpdump/libpcap project — the pcap-savefile(5) man page documents the capture file layout</description></item>
///   <item><description><c>https://github.com/the-tcpdump-group/libpcap</c> — canonical implementation</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/Pcap</c> — Wikipedia</description></item>
/// </list>
/// </summary>
public sealed class PcapFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveInMemoryExtract,
    IArchiveCreatable, IArchiveModifiable {
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
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify | FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
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
    // The magic number 0xA1B2C3D4 (0xA1B23C4D for nanosecond timestamps) in the writing
    // host's byte order: x86 captures start D4 C3 B2 A1.
    new([0xD4, 0xC3, 0xB2, 0xA1], Confidence: 0.95), // little-endian, microsecond
    new([0xA1, 0xB2, 0xC3, 0xD4], Confidence: 0.95), // big-endian,    microsecond
    new([0x4D, 0x3C, 0xB2, 0xA1], Confidence: 0.95), // little-endian, nanosecond
    new([0xA1, 0xB2, 0x3C, 0x4D], Confidence: 0.95), // big-endian,    nanosecond
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
  public string Description =>
    "Classic libpcap packet capture: global header + per-packet link-layer frames. Create options: " +
    "linktype (default 1, Ethernet), snaplen (default 262144), nanosecond (default false).";

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

  List<ArchiveEntryInfo> IArchiveFormatOperations.ListSpan(ReadOnlySpan<byte> archive, string? password) {
    var capture = PcapReader.ReadLayout(archive);
    var total = capture.Packets.Count;
    var exposed = Math.Min(total, MaxPackets);
    var metadata = BuildMetadata(capture, total, total > MaxPackets);
    var result = new List<ArchiveEntryInfo>(exposed + 1) {
      new(0, "metadata.ini", metadata.LongLength, metadata.LongLength,
        "stored", false, false, null, "Tag"),
    };

    for (var i = 0; i < exposed; ++i) {
      var packet = capture.Packets[i];
      result.Add(new ArchiveEntryInfo(
        result.Count, $"packet_{i:D4}.bin", packet.DataLength, packet.DataLength,
        "stored", false, false, PacketTimestamp(capture.Nanosecond, packet.TimestampSeconds, packet.TimestampFraction),
        "Payload"));
    }
    return result;
  }

  void IArchiveFormatOperations.ExtractSpan(
      ReadOnlySpan<byte> archive, string outputDir, string? password, string[]? files) {
    var capture = PcapReader.ReadLayout(archive);
    var total = capture.Packets.Count;
    var exposed = Math.Min(total, MaxPackets);

    if (files is null || files.Length == 0 || MatchesFilter("metadata.ini", files))
      WriteFile(outputDir, "metadata.ini", BuildMetadata(capture, total, total > MaxPackets));

    for (var i = 0; i < exposed; ++i) {
      var name = $"packet_{i:D4}.bin";
      if (files is { Length: > 0 } && !MatchesFilter(name, files))
        continue;

      var packet = capture.Packets[i];
      using var target = CreateEntryFile(outputDir, name);
      target.Write(archive.Slice(packet.DataOffset, packet.DataLength));
    }
  }

  // ── Create / modify ────────────────────────────────────────────────────

  /// <summary>Writes a capture holding one packet per input file.</summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(options);
    var (metadata, packets) = CaptureEditing.Split(inputs, existingPackets: 0);
    var settings = SettingsFrom(metadata, options);
    using var ms = new MemoryStream();
    PcapWriter.Write(ms, settings, packets.Select(p => (p.Timestamp, p.Data)));
    ms.Position = 0;
    ms.CopyTo(output);
  }

  /// <summary>Replaces packets named <c>packet_NNNN.bin</c> and appends every other input.</summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    var data = CaptureEditing.ReadAll(archive);
    var layout = Editable(data);
    var (metadata, packets) = CaptureEditing.Split(inputs, layout.Packets.Count);
    if (metadata != null && !metadata.AsSpan().SequenceEqual(BuildMetadata(layout, layout.Packets.Count, layout.Packets.Count > MaxPackets)))
      throw new NotSupportedException("pcap: the global header cannot be changed in place; create a new capture with the new metadata.ini.");
    var settings = SettingsOf(layout);
    var replacements = new Dictionary<int, byte[]>();
    foreach (var p in packets.Where(p => p.ReplaceIndex >= 0)) replacements[p.ReplaceIndex] = p.Data;

    using var ms = new MemoryStream();
    ms.Write(data.AsSpan(0, PcapReader.FileHeaderSize));
    for (var i = 0; i < layout.Packets.Count; ++i) {
      var record = layout.Packets[i];
      if (replacements.TryGetValue(i, out var replacement)) {
        // The packet keeps its own timestamp words exactly as they were.
        var rebuilt = PcapWriter.Record(settings, DateTimeOffset.UnixEpoch, replacement);
        data.AsSpan(record.RecordOffset, 8).CopyTo(rebuilt);
        ms.Write(rebuilt);
      } else
        ms.Write(data.AsSpan(record.RecordOffset, record.RecordLength));
    }
    foreach (var p in packets.Where(p => p.ReplaceIndex < 0))
      ms.Write(PcapWriter.Record(settings, p.Timestamp, p.Data));
    CaptureEditing.Overwrite(archive, ms.ToArray());
  }

  /// <summary>Drops the named packets; every other record and the header stay byte-identical.</summary>
  public void Remove(Stream archive, string[] entryNames) {
    var drop = CaptureEditing.IndicesToRemove(entryNames, "pcap");
    Rebuild(archive, i => !drop.Contains(i));
  }

  /// <summary>Drops every packet, leaving the global header: an empty capture is a valid pcap file.</summary>
  public void Purge(Stream archive) => Rebuild(archive, _ => false);

  private static void Rebuild(Stream archive, Func<int, bool> keep) {
    var data = CaptureEditing.ReadAll(archive);
    var layout = Editable(data);
    using var ms = new MemoryStream();
    ms.Write(data.AsSpan(0, PcapReader.FileHeaderSize));
    for (var i = 0; i < layout.Packets.Count; ++i)
      if (keep(i))
        ms.Write(data.AsSpan(layout.Packets[i].RecordOffset, layout.Packets[i].RecordLength));
    CaptureEditing.Overwrite(archive, ms.ToArray());
  }

  private static PcapReader.CaptureLayout Editable(byte[] data) {
    var layout = PcapReader.ReadLayout(data);
    if (layout.TrailingBytes != 0)
      throw new NotSupportedException(
        $"pcap: {layout.TrailingBytes} byte(s) after the last complete packet (a truncated capture); editing would lose them.");
    return layout;
  }

  private static PcapSettings SettingsOf(PcapReader.CaptureLayout c)
    => new(c.LinkType, c.Snaplen, c.Nanosecond, c.LittleEndian);

  internal static PcapSettings SettingsFrom(byte[]? metadata, FormatCreateOptions options) {
    var ini = metadata != null
      ? CaptureEditing.ParseIni(metadata, "pcap")
      : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    var linkType = CaptureEditing.LeadingNumber(options.GetString("linktype"))
      ?? CaptureEditing.LeadingNumber(ini.GetValueOrDefault("link_type")) ?? 1u;
    var snaplen = CaptureEditing.LeadingNumber(options.GetString("snaplen"))
      ?? CaptureEditing.LeadingNumber(ini.GetValueOrDefault("snaplen")) ?? PcapSettings.DefaultSnaplen;
    var nano = options.HasOption("nanosecond")
      ? options.GetOptionBool("nanosecond", false)
      : string.Equals(ini.GetValueOrDefault("timestamp_resolution"), "nanosecond", StringComparison.OrdinalIgnoreCase);
    var little = !string.Equals(ini.GetValueOrDefault("endian"), "big", StringComparison.OrdinalIgnoreCase);
    if (snaplen == 0)
      throw new ArgumentException("pcap: snaplen must be positive.");
    return new PcapSettings(linkType, snaplen, nano, little);
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
      var ts = PacketTimestamp(capture.Nanosecond, p.TimestampSeconds, p.TimestampFraction);
      result.Add(($"packet_{i:D4}.bin", "Payload", ts, p.Data));
    }
    return result;
  }

  private static byte[] BuildMetadata(PcapReader.CaptureLayout c, int totalPackets, bool truncated) {
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

  private static DateTime PacketTimestamp(bool nanosecond, uint seconds, uint fraction)
    => DateTime.UnixEpoch.AddSeconds(seconds)
      .AddTicks(nanosecond ? fraction / 100 : fraction * 10L);

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
