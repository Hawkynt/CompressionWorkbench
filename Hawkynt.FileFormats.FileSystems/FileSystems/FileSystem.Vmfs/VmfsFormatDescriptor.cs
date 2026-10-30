#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Vmfs;

/// <summary>
/// VMware VMFS3 datastore volumes, read-only.
/// <para>
/// The layout follows <c>docs/VMFS-ON-DISK.md</c>, a specification derived from
/// vmfs-tools (GPL-2.0, used as specification and oracle only) and checked against a
/// volume VMware ESX wrote. Single-extent VMFS3 is read, unpartitioned or inside an MBR
/// (type 0xFB) or GPT VMFS partition. VMFS5/6 and spanned volumes are refused: no public
/// volume exists to prove a reader against.
/// </para>
/// <para>References:</para>
/// <list type="bullet">
///   <item><description><c>https://github.com/glandium/vmfs-tools</c> — the oracle and specification source (GPL-2.0)</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/VMware_VMFS</c></description></item>
/// </list>
/// </summary>
public sealed class VmfsFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {

  public string Id => "Vmfs";
  public string DisplayName => "VMware VMFS";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".vmfs";
  public IReadOnlyList<string> Extensions => [".vmfs"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures =>
    [new([0x0D, 0xD0, 0x01, 0xC0], Offset: (int)VmfsReader.VolumeInfoOffset, Confidence: 0.85)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "VMware VMFS3 datastore volume (read-only)";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    using var r = new VmfsReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(i, e.Name, e.Size, e.Size, "Stored", e.IsDirectory, false, e.LastModified,
      Kind: null, IsSymlink: e.IsSymlink)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    using var r = new VmfsReader(stream);
    foreach (var e in r.Entries) {
      if (files != null && files.Length > 0 && !MatchesFilter(e.Name, files)) continue;
      if (e.IsDirectory) { Directory.CreateDirectory(Path.Combine(outputDir, e.Name)); continue; }
      var path = Path.Combine(outputDir, e.Name.Replace('/', Path.DirectorySeparatorChar));
      Directory.CreateDirectory(Path.GetDirectoryName(path)!);
      using var output = File.Create(path);
      r.ExtractTo(e, output);
    }
  }
}
