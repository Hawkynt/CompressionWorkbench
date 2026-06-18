#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Ova;

/// <summary>
/// OVA (Open Virtual Appliance) — an uncompressed TAR carrying an OVF
/// (Open Virtualization Format) XML descriptor, one or more virtual disk images
/// (typically <c>.vmdk</c>), and an optional <c>.mf</c> manifest of checksums.
///
/// <para>List/Extract delegate to <see cref="OvaReader"/>: every TAR member is
/// surfaced verbatim alongside a <c>FULL.ova</c> copy of the whole container and
/// a <c>metadata.ini</c> distilled from the OVF XML (VM name, disk count, guest
/// OS type). The OVF is parsed with lightweight pattern matching — no schema
/// validation — so any well-formed appliance round-trips, and malformed input
/// degrades to <c>parse_status=partial</c> rather than throwing.</para>
///
/// <para>Create/Add build a spec-correct ustar TAR via <see cref="OvaWriter"/>:
/// the OVF first, then disks, then a freshly generated <c>.mf</c> with correct
/// SHA-256 lines. When no OVF input is supplied a minimal valid envelope is
/// synthesised that references each disk's <c>ovf:href</c>.</para>
/// </summary>
public sealed class OvaFormatDescriptor
    : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveModifiable {
  public string Id => "Ova";
  public string DisplayName => "OVA/OVF Virtual Appliance";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.CanCreate | FormatCapabilities.CanModify |
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

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var fullSize = SafeLength(stream);
    var entries = new List<ArchiveEntryInfo> {
      new(0, "FULL.ova", fullSize, fullSize, "Stored", false, false, null),
      new(1, "metadata.ini", 0, 0, "Stored", false, false, null),
    };

    var reader = OvaReader.Read(stream);
    var idx = 2;
    foreach (var m in reader.Members)
      entries.Add(new ArchiveEntryInfo(idx++, m.Name, m.Data.Length, m.Data.Length, "Stored", false, false, null));
    return entries;
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    if (Wants(files, "FULL.ova")) {
      var full = ReadAll(stream);
      WriteFile(outputDir, "FULL.ova", full);
    }

    var reader = OvaReader.Read(stream);
    if (Wants(files, "metadata.ini"))
      WriteFile(outputDir, "metadata.ini", Encoding.UTF8.GetBytes(BuildMetadataIni(reader)));

    foreach (var m in reader.Members) {
      if (!Wants(files, m.Name)) continue;
      WriteFile(outputDir, m.Name, m.Data);
    }
  }

  // ── IArchiveCreatable ────────────────────────────────────────────

  /// <summary>
  /// Builds a spec-correct OVA from <paramref name="inputs"/>: the OVF is
  /// placed first, disks next, then a generated <c>.mf</c> with correct
  /// SHA-256 lines. A minimal OVF envelope is synthesised when none is
  /// supplied so the appliance is still well-formed.
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    var writer = new OvaWriter();
    foreach (var i in inputs) {
      if (i.IsDirectory) continue;
      writer.Add(LeafName(i.ArchiveName), i.ReadContent());
    }
    writer.Write(output);
  }

  // ── IArchiveModifiable ───────────────────────────────────────────

  /// <summary>
  /// Adds (or replaces by name) members and rebuilds the appliance so the
  /// regenerated <c>.mf</c> stays consistent with the new member set. TAR has
  /// no central directory, so this is a full rewrite-in-place.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(inputs);
    var members = ReadCurrentMembers(archive);
    foreach (var i in inputs) {
      if (i.IsDirectory) continue;
      var name = LeafName(i.ArchiveName);
      members[name] = i.ReadContent();
    }
    Rebuild(archive, members);
  }

  /// <summary>
  /// Removes named members and rebuilds the appliance with a regenerated
  /// manifest reflecting the survivors.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryNames);
    var members = ReadCurrentMembers(archive);
    foreach (var n in entryNames)
      members.Remove(LeafName(n));
    Rebuild(archive, members);
  }

  /// <summary>
  /// Reads the current members (manifest excluded — it is regenerated on
  /// rebuild) into an order-preserving map keyed by name.
  /// </summary>
  private static Dictionary<string, byte[]> ReadCurrentMembers(Stream archive) {
    var reader = OvaReader.Read(archive);
    var map = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
    foreach (var m in reader.Members) {
      if (m.Name.EndsWith(".mf", StringComparison.OrdinalIgnoreCase)) continue;
      map[m.Name] = m.Data;
    }
    return map;
  }

  /// <summary>Rewrites <paramref name="archive"/> in place from <paramref name="members"/>.</summary>
  private static void Rebuild(Stream archive, Dictionary<string, byte[]> members) {
    var writer = new OvaWriter();
    foreach (var (name, data) in members)
      writer.Add(name, data);
    var bytes = writer.ToArray();
    archive.Position = 0;
    archive.SetLength(0);
    archive.Write(bytes, 0, bytes.Length);
    archive.Flush();
  }

  private static string LeafName(string archiveName) {
    var normalized = archiveName.Replace('\\', '/');
    var slash = normalized.LastIndexOf('/');
    return slash >= 0 ? normalized[(slash + 1)..] : normalized;
  }

  // ── Metadata.ini ─────────────────────────────────────────────────

  private static bool Wants(string[]? files, string name)
    => files == null || files.Length == 0 || MatchesFilter(name, files);

  private static string BuildMetadataIni(OvaReader reader) {
    var ovf = reader.Ovf;
    var diskCount = reader.Disks.Count();
    var hasManifest = reader.Manifest != null;

    var sb = new StringBuilder();
    sb.Append("[Ova]\n");
    sb.Append(CultureInfo.InvariantCulture, $"member_count={reader.Members.Count}\n");
    sb.Append(CultureInfo.InvariantCulture, $"disk_count={diskCount}\n");
    sb.Append(CultureInfo.InvariantCulture, $"has_manifest={(hasManifest ? 1 : 0)}\n");
    sb.Append(CultureInfo.InvariantCulture, $"ovf_member={(ovf?.Name ?? string.Empty)}\n");
    sb.Append(CultureInfo.InvariantCulture, $"parse_status={(reader.Partial ? "partial" : "ok")}\n");

    if (hasManifest) {
      var checks = reader.VerifyManifest();
      var verified = checks.Count > 0 && checks.All(c => c.Matches);
      sb.Append(CultureInfo.InvariantCulture, $"manifest_verified={(verified ? 1 : 0)}\n");
    }

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
