#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Compression.Registry;
using FileFormat.Tar;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Ova;

/// <summary>
/// OVA (Open Virtual Appliance) — an uncompressed TAR archive carrying an OVF
/// (Open Virtualization Format) XML descriptor, one or more virtual disk images
/// (typically <c>.vmdk</c>), and an optional <c>.mf</c> manifest of checksums.
///
/// <para>List/Extract are delegated to the TAR reader: every TAR member is
/// surfaced verbatim alongside a <c>FULL.ova</c> copy of the whole container and
/// a <c>metadata.ini</c> distilled from the OVF XML (VM name, disk count, guest
/// OS type). The OVF is parsed with lightweight pattern matching — no schema
/// validation — so any well-formed appliance round-trips, and malformed input
/// degrades to <c>parse_status=partial</c> rather than throwing.</para>
/// </summary>
public sealed class OvaFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  public string Id => "Ova";
  public string DisplayName => "OVA/OVF Virtual Appliance";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".ova";
  public IReadOnlyList<string> Extensions => [".ova"];
  public IReadOnlyList<string> CompoundExtensions => [];
  // OVA is a plain TAR; rely on the .ova extension plus the presence of an .ovf
  // member (verified during List). No distinguishing leading magic exists.
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored (TAR)")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "OVA Open Virtual Appliance: uncompressed TAR with an .ovf descriptor, .vmdk disks and an optional .mf manifest.";

  private sealed record OvaMember(string Name, byte[] Data);

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var fullSize = SafeLength(stream);
    var entries = new List<ArchiveEntryInfo> {
      new(0, "FULL.ova", fullSize, fullSize, "Stored", false, false, null),
      new(1, "metadata.ini", 0, 0, "Stored", false, false, null),
    };

    var members = TryReadMembers(stream, out var partial);
    var idx = 2;
    foreach (var m in members)
      entries.Add(new ArchiveEntryInfo(idx++, m.Name, m.Data.Length, m.Data.Length, "Stored", false, false, null));
    _ = partial;
    return entries;
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    if (Wants(files, "FULL.ova")) {
      var full = ReadAll(stream);
      WriteFile(outputDir, "FULL.ova", full);
    }

    var members = TryReadMembers(stream, out var partial);
    if (Wants(files, "metadata.ini"))
      WriteFile(outputDir, "metadata.ini", Encoding.UTF8.GetBytes(BuildMetadataIni(members, partial)));

    foreach (var m in members) {
      if (!Wants(files, m.Name)) continue;
      WriteFile(outputDir, m.Name, m.Data);
    }
  }

  private static bool Wants(string[]? files, string name)
    => files == null || files.Length == 0 || MatchesFilter(name, files);

  private static List<OvaMember> TryReadMembers(Stream stream, out bool partial) {
    partial = false;
    var members = new List<OvaMember>();
    try {
      if (stream.CanSeek) stream.Position = 0;
      using var r = new TarReader(stream, leaveOpen: true);
      while (r.GetNextEntry() is { } entry) {
        if (entry.IsDirectory) continue;
        using var data = r.GetEntryStream();
        using var ms = new MemoryStream();
        data.CopyTo(ms);
        members.Add(new OvaMember(entry.Name, ms.ToArray()));
      }
    } catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException or IOException or FormatException or ArgumentException) {
      partial = true;
    }
    return members;
  }

  private static string BuildMetadataIni(List<OvaMember> members, bool partial) {
    var ovf = members.FirstOrDefault(m => m.Name.EndsWith(".ovf", StringComparison.OrdinalIgnoreCase));
    var diskCount = members.Count(m =>
      m.Name.EndsWith(".vmdk", StringComparison.OrdinalIgnoreCase) ||
      m.Name.EndsWith(".vhd", StringComparison.OrdinalIgnoreCase) ||
      m.Name.EndsWith(".img", StringComparison.OrdinalIgnoreCase));
    var hasManifest = members.Any(m => m.Name.EndsWith(".mf", StringComparison.OrdinalIgnoreCase));

    var sb = new StringBuilder();
    sb.Append("[Ova]\n");
    sb.Append(CultureInfo.InvariantCulture, $"member_count={members.Count}\n");
    sb.Append(CultureInfo.InvariantCulture, $"disk_count={diskCount}\n");
    sb.Append(CultureInfo.InvariantCulture, $"has_manifest={(hasManifest ? 1 : 0)}\n");
    sb.Append(CultureInfo.InvariantCulture, $"ovf_member={(ovf?.Name ?? string.Empty)}\n");
    sb.Append(CultureInfo.InvariantCulture, $"parse_status={(partial ? "partial" : "ok")}\n");

    if (ovf != null) {
      var xml = Encoding.UTF8.GetString(ovf.Data);
      var vmName = ExtractAttr(xml, @"<VirtualSystem[^>]*ovf:id\s*=\s*""([^""]*)""")
                   ?? ExtractElement(xml, "Name")
                   ?? ExtractElement(xml, "VirtualSystemIdentifier");
      var osType = ExtractAttr(xml, @"<OperatingSystemSection[^>]*ovf:id\s*=\s*""([^""]*)""");
      var osDesc = ExtractElement(xml, "Description");
      var ovfDiskCount = Regex.Matches(xml, @"<Disk\b", RegexOptions.IgnoreCase).Count;
      sb.Append("\n[Ovf]\n");
      if (vmName != null) sb.Append(CultureInfo.InvariantCulture, $"vm_name={Sanitize(vmName)}\n");
      if (osType != null) sb.Append(CultureInfo.InvariantCulture, $"os_id={Sanitize(osType)}\n");
      if (osDesc != null) sb.Append(CultureInfo.InvariantCulture, $"os_description={Sanitize(osDesc)}\n");
      sb.Append(CultureInfo.InvariantCulture, $"ovf_disk_count={ovfDiskCount}\n");
    }
    return sb.ToString();
  }

  private static string? ExtractAttr(string xml, string pattern) {
    var m = Regex.Match(xml, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);
    return m.Success ? m.Groups[1].Value : null;
  }

  private static string? ExtractElement(string xml, string localName) {
    var m = Regex.Match(xml, $@"<(?:\w+:)?{Regex.Escape(localName)}\b[^>]*>(.*?)</(?:\w+:)?{Regex.Escape(localName)}>",
      RegexOptions.IgnoreCase | RegexOptions.Singleline);
    return m.Success ? m.Groups[1].Value.Trim() : null;
  }

  private static string Sanitize(string value)
    => value.Replace('\r', ' ').Replace('\n', ' ').Trim();

  private static long SafeLength(Stream s) => s.CanSeek ? s.Length : 0;

  private static byte[] ReadAll(Stream stream) {
    if (stream.CanSeek) stream.Position = 0;
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return ms.ToArray();
  }
}
