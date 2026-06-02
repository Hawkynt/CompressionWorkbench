#pragma warning disable CS1591
using Compression.Registry;
using Compression.Registry.Streaming;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Wafl;

/// <summary>
/// Stage 0 detection-only descriptor for NetApp WAFL (Write-Anywhere File
/// Layout) volume images. Surfaces only a synthetic <c>metadata.ini</c>
/// and the raw image bytes; no real file-walk is attempted.
///
/// <para>
/// <b>Stage-0 confirmed.</b> An R/O promotion attempt was investigated
/// against the publicly available material (Hitz 1994 TR3002, NetApp
/// patents WO1994029807 / US6289356, archived ONTAP whitepapers) and
/// declined. The high-level tree-of-blocks design (root inode → inode
/// file → metadata files + user files; 4 KB blocks; FSinfo block at a
/// fixed location anchoring two redundant copies) is published, but the
/// exact byte-level on-disk encoding used by current ONTAP releases is
/// not — neither the inode record layout, the FBN → VBN → PVBN
/// translation tables, the FlexVol container-file mapping, nor the
/// RAID-DP parity scheme used for block addressing have a public spec
/// adequate to extract files from a single-image dump. WAFL is heavily
/// patented and proprietary; no open-source reader exists. The full
/// investigation record is captured in this XML doc, the metadata.ini
/// surface, and the README stub-tier table.
/// </para>
/// </summary>
public sealed class WaflFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {

  public string Id => "Wafl";
  public string DisplayName => "NetApp WAFL";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest;
  public string DefaultExtension => ".wafl";
  public IReadOnlyList<string> Extensions => [".wafl"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    // "wafd" (0x77 0x61 0x66 0x64) — WAFL FSinfo block tag at offset 0.
    new("wafd"u8.ToArray(), Offset: 0, Confidence: 0.90),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "NetApp WAFL — detection-only (Stage-0 confirmed) — proprietary ONTAP filesystem; " +
    "on-disk tree-of-blocks is partially reverse-engineered from Hitz 1994 + NetApp patents " +
    "but FBN/VBN/PVBN translation, FlexVol container mapping, RAID-DP block placement, and " +
    "NVRAM consistency-point gap make a safe single-image R/O reader infeasible from public spec. " +
    "Magic 'wafd' at offset 0 of FSinfo block.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new WaflReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored", e.IsDirectory, false, null)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new WaflReader(stream);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }

  Stream IArchiveFormatOperations.OpenEntry(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryName);
    var r = new WaflReader(archive);
    var entry = r.Entries.FirstOrDefault(e => e.Name == entryName)
      ?? throw new FileNotFoundException($"WAFL entry not found: {entryName}");
    var data = r.Extract(entry);
    return new BoundedEntryStream(new MemoryStream(data, writable: false), data.Length, leaveOpen: false);
  }
}
