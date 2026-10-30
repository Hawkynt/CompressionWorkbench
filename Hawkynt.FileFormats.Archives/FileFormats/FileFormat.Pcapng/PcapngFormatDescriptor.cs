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
///
/// <para>Create writes a little-endian pcapng with one section, one interface (from an input
/// <c>metadata.ini</c>'s <c>[interface_0]</c> keys or the <c>linktype</c> / <c>snaplen</c> /
/// <c>nanosecond</c> options) and one Enhanced Packet Block per input file, timestamped with the
/// input's write time. Edits keep every untouched block byte for byte: an input named
/// <c>packet_NNNN.bin</c> replaces that packet's data, any other input appends an EPB on the last
/// section's first interface, and removal drops the named packet blocks (a declared section
/// length is recomputed).</para>
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://github.com/pcapng/pcapng</c> — canonical pcapng specification repository (IETF draft source)</description></item>
///   <item><description>IETF draft-ietf-opsawg-pcapng — the specification text produced from that repository</description></item>
///   <item><description><c>https://wiki.wireshark.org</c> — Wireshark wiki — pcapng development notes</description></item>
/// </list>
/// </summary>
public sealed class PcapngFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveInMemoryExtract,
    IArchiveCreatable, IArchiveModifiable {
  private const int MaxPackets = 100;

  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "Pcapng";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "PCAP Next Generation";
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
  public string DefaultExtension => ".pcapng";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".pcapng", ".ntar"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    // Block type 0x0A0D0D0A at offset 0 — same in both byte orders (palindrome).
    new([0x0A, 0x0D, 0x0D, 0x0A], Confidence: 0.95),
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
    "PCAP Next Generation block-structured packet capture (multiple sections, multiple interfaces). Create options: " +
    "linktype (default 1, Ethernet), snaplen (default 0 = unlimited), nanosecond (default false).";

  // ── Create / modify ────────────────────────────────────────────────────

  /// <summary>Writes a capture holding one packet per input file.</summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(options);
    var (metadata, packets) = FileFormat.Pcap.CaptureEditing.Split(inputs, existingPackets: 0);
    var ini = metadata != null
      ? FileFormat.Pcap.CaptureEditing.ParseIni(metadata, "interface_0")
      : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    var linkType = FileFormat.Pcap.CaptureEditing.LeadingNumber(options.GetString("linktype"))
      ?? FileFormat.Pcap.CaptureEditing.LeadingNumber(ini.GetValueOrDefault("link_type")) ?? 1u;
    var snaplen = FileFormat.Pcap.CaptureEditing.LeadingNumber(options.GetString("snaplen"))
      ?? FileFormat.Pcap.CaptureEditing.LeadingNumber(ini.GetValueOrDefault("snaplen")) ?? 0u;
    var nano = options.HasOption("nanosecond")
      ? options.GetOptionBool("nanosecond", false)
      : string.Equals(ini.GetValueOrDefault("timestamp_resolution"), "nanosecond", StringComparison.OrdinalIgnoreCase);
    using var ms = new MemoryStream();
    PcapngWriter.Write(ms, new PcapngSettings(linkType, snaplen, nano), packets.Select(p => (p.Timestamp, p.Data)));
    ms.Position = 0;
    ms.CopyTo(output);
  }

  /// <summary>Replaces packets named <c>packet_NNNN.bin</c> and appends every other input.</summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    var data = FileFormat.Pcap.CaptureEditing.ReadAll(archive);
    var layout = Editable(data);
    var (metadata, packets) = FileFormat.Pcap.CaptureEditing.Split(inputs, layout.Packets.Count);
    if (metadata != null && !metadata.AsSpan().SequenceEqual(BuildMetadata(layout, layout.Packets.Count, layout.Packets.Count > MaxPackets)))
      throw new NotSupportedException("pcapng: section and interface headers cannot be changed in place; create a new capture with the new metadata.ini.");

    var replacements = new Dictionary<int, byte[]>();
    foreach (var p in packets.Where(p => p.ReplaceIndex >= 0)) {
      var old = layout.Packets[p.ReplaceIndex];
      var little = SectionIsLittle(data, layout.Sections[old.SectionIndex]);
      if (data.AsSpan(old.BlockOffset, 4).SequenceEqual(BlockType(PcapngReader.BtSimplePacket, little)))
        throw new NotSupportedException("pcapng: Simple Packet Blocks carry no timestamp or interface to rebuild them with; replacing one is refused.");
      if (old.Interface is not { } iface)
        throw new NotSupportedException("pcapng: the packet names an interface its section does not describe.");
      var block = PcapngWriter.EnhancedPacket((uint)old.InterfaceId, 1, DateTimeOffset.UnixEpoch, p.Data, little, iface.Snaplen);
      // Keep the interface id and both timestamp words exactly as they were.
      data.AsSpan(old.BlockOffset + 8, 12).CopyTo(block.AsSpan(8));
      replacements[p.ReplaceIndex] = block;
    }

    var appended = new List<byte[]>();
    var appends = packets.Where(p => p.ReplaceIndex < 0).ToList();
    if (appends.Count > 0) {
      var section = layout.Sections[^1];
      if (section.Interfaces.Count == 0)
        throw new NotSupportedException("pcapng: the last section describes no interface to append packets on.");
      var iface = section.Interfaces[0];
      if (iface.UnitsPerSecond == 0)
        throw new NotSupportedException("pcapng: the interface's if_tsresol is unusable, so no timestamp can be written for it.");
      var little = SectionIsLittle(data, section);
      foreach (var p in appends) {
        var ts = p.Timestamp.AddSeconds(-iface.OffsetSeconds);
        appended.Add(PcapngWriter.EnhancedPacket(0, iface.UnitsPerSecond, ts, p.Data, little, iface.Snaplen));
      }
    }
    FileFormat.Pcap.CaptureEditing.Overwrite(archive, Rebuild(data, layout, i => true, replacements, appended));
  }

  /// <summary>Drops the named packet blocks; every other block stays byte-identical.</summary>
  public void Remove(Stream archive, string[] entryNames) {
    var drop = FileFormat.Pcap.CaptureEditing.IndicesToRemove(entryNames, "pcapng");
    var data = FileFormat.Pcap.CaptureEditing.ReadAll(archive);
    var layout = Editable(data);
    FileFormat.Pcap.CaptureEditing.Overwrite(archive, Rebuild(data, layout, i => !drop.Contains(i), [], []));
  }

  /// <summary>Drops every packet block, leaving sections, interfaces and every other block.</summary>
  public void Purge(Stream archive) {
    var data = FileFormat.Pcap.CaptureEditing.ReadAll(archive);
    var layout = Editable(data);
    FileFormat.Pcap.CaptureEditing.Overwrite(archive, Rebuild(data, layout, _ => false, [], []));
  }

  /// <summary>
  /// Copies the block stream, dropping/replacing packet blocks and appending new ones to the last
  /// section; a section whose SHB declares a length gets it recomputed.
  /// </summary>
  private static byte[] Rebuild(byte[] data, PcapngReader.CaptureLayout layout, Func<int, bool> keep,
      Dictionary<int, byte[]> replacements, List<byte[]> appended) {
    var byOffset = new Dictionary<int, int>();
    for (var i = 0; i < layout.Packets.Count; ++i) byOffset[layout.Packets[i].BlockOffset] = i;

    using var ms = new MemoryStream();
    var sectionStarts = new List<(int OutOffset, int SectionIndex)>();
    var sectionIndex = -1;
    var little = true;
    var pos = 0;
    while (pos < data.Length) {
      if (sectionIndex + 1 < layout.Sections.Count && layout.Sections[sectionIndex + 1].Offset == pos) {
        ++sectionIndex;
        little = SectionIsLittle(data, layout.Sections[sectionIndex]);
        sectionStarts.Add(((int)ms.Length, sectionIndex));
      }
      var length = (int)PcapngReader.ReadU32(little, data.AsSpan(pos + 4));
      if (byOffset.TryGetValue(pos, out var packet)) {
        if (replacements.TryGetValue(packet, out var block)) ms.Write(block);
        else if (keep(packet)) ms.Write(data, pos, length);
      } else
        ms.Write(data, pos, length);
      pos += length;
    }
    foreach (var block in appended) ms.Write(block);

    var image = ms.ToArray();
    for (var s = 0; s < sectionStarts.Count; ++s) {
      var (start, index) = sectionStarts[s];
      var section = layout.Sections[index];
      if (section.DeclaredLength == -1) continue;
      var end = s + 1 < sectionStarts.Count ? sectionStarts[s + 1].OutOffset : image.Length;
      var bodyLength = (ulong)(end - start - section.HeaderLength);
      var at = image.AsSpan(start + 16, 8);
      if (SectionIsLittle(data, section)) System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(at, bodyLength);
      else System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(at, bodyLength);
    }
    return image;
  }

  private static PcapngReader.CaptureLayout Editable(byte[] data) {
    var layout = PcapngReader.ReadLayout(data);
    if (layout.EndOffset != data.Length)
      throw new NotSupportedException(
        $"pcapng: {data.Length - layout.EndOffset} byte(s) after the last well-formed block (a truncated capture); editing would lose them.");
    return layout;
  }

  private static bool SectionIsLittle(byte[] data, PcapngReader.SectionLayout section)
    => System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(section.Offset + 8)) == PcapngReader.ByteOrderMagic;

  private static byte[] BlockType(uint type, bool little) {
    var b = new byte[4];
    if (little) System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(b, type);
    else System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(b, type);
    return b;
  }

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
    foreach (var e in BuildEntries(input))
      if (e.Name.Equals(entryName, StringComparison.OrdinalIgnoreCase)) {
        output.Write(e.Data);
        return;
      }
    throw new FileNotFoundException($"Entry not found: {entryName}");
  }

  List<ArchiveEntryInfo> IArchiveFormatOperations.ListSpan(ReadOnlySpan<byte> archive, string? password) {
    var capture = PcapngReader.ReadLayout(archive);
    var total = capture.Packets.Count;
    var exposed = Math.Min(total, MaxPackets);
    var metadata = BuildMetadata(capture, total, total > MaxPackets);
    var result = new List<ArchiveEntryInfo>(exposed + 1) {
      new(0, "metadata.ini", metadata.LongLength, metadata.LongLength,
        "stored", false, false, null, "Tag"),
    };

    for (var i = 0; i < exposed; ++i) {
      var packet = capture.Packets[i];
      var timestamp = packet.Timestamp;
      result.Add(new ArchiveEntryInfo(
        result.Count, $"packet_{i:D4}.bin", packet.DataLength, packet.DataLength,
        "stored", false, false, timestamp, "Payload"));
    }

    return result;
  }

  void IArchiveFormatOperations.ExtractSpan(
      ReadOnlySpan<byte> archive, string outputDir, string? password, string[]? files) {
    var capture = PcapngReader.ReadLayout(archive);
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

  private static IReadOnlyList<(string Name, string Kind, DateTime? Timestamp, byte[] Data)> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var data = ms.GetBuffer().AsSpan(0, (int)ms.Length);
    var capture = PcapngReader.ReadLayout(data);

    var total = capture.Packets.Count;
    var exposed = Math.Min(total, MaxPackets);

    var result = new List<(string, string, DateTime?, byte[])> {
      ("metadata.ini", "Tag", null, BuildMetadata(capture, total, total > MaxPackets)),
    };

    for (var i = 0; i < exposed; i++) {
      var p = capture.Packets[i];
      result.Add(($"packet_{i:D4}.bin", "Payload", p.Timestamp, data.Slice(p.DataOffset, p.DataLength).ToArray()));
    }
    return result;
  }

  private static byte[] BuildMetadata(PcapngReader.CaptureLayout c, int totalPackets, bool truncated) {
    var sb = new StringBuilder();
    void Line(string key, object value) => sb.Append(CultureInfo.InvariantCulture, $"{key} = {value}\n");
    sb.Append("[pcapng]\n");
    Line("version", $"{c.VersionMajor}.{c.VersionMinor}");
    Line("endian", c.LittleEndian ? "little" : "big");
    if (c.Sections.Count > 1) Line("section_count", c.Sections.Count);
    Line("interface_count", c.Interfaces.Count);
    Line("total_packet_count", totalPackets);
    if (truncated) {
      Line("exposed_packets", MaxPackets);
      Line("note", "capture truncated for listing; remaining packets omitted");
    }
    for (var i = 0; i < c.Interfaces.Count; i++) {
      var iface = c.Interfaces[i];
      sb.Append(CultureInfo.InvariantCulture, $"\n[interface_{i}]\n");
      Line("link_type", $"{iface.LinkType} {LinkTypeName(iface.LinkType)}".TrimEnd());
      Line("snaplen", iface.Snaplen);
      Line("timestamp_resolution", iface.UnitsPerSecond switch {
        1_000_000UL => "microsecond",
        1_000_000_000UL => "nanosecond",
        0UL => "invalid",
        var u => $"1/{u} s",
      });
      if (iface.OffsetSeconds != 0) Line("timestamp_offset", iface.OffsetSeconds);
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
