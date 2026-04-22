#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Adf;

public sealed class AdfFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveWriteConstraints, IArchiveShrinkable {
  public long? MaxTotalArchiveSize => 901120;  // standard DD (880 KB) — 11 sectors × 2 sides × 80 tracks × 512
  public string AcceptedInputsDescription =>
    "Amiga DD ADF disk; any file up to 901 120 bytes total.";
  public bool CanAccept(ArchiveInputInfo input, out string? reason) { reason = null; return true; }

  public IReadOnlyList<long> CanonicalSizes => [901120];
  public void Shrink(Stream input, Stream output) =>
    Compression.Registry.ArchiveShrinker.ShrinkViaRebuild(input, output, this, this, this.CanonicalSizes);

  public string Id => "Adf";
  public string DisplayName => "ADF";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries |
    FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".adf";
  public IReadOnlyList<string> Extensions => [".adf"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures =>
    [new("DOS\0"u8.ToArray(), Confidence: 0.60)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("adf", "ADF")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Amiga Disk File";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new AdfReader(stream, leaveOpen: true);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(i, e.FullPath, e.Size,
      e.Size, "Stored", e.IsDirectory, false, null)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new AdfReader(stream, leaveOpen: true);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.FullPath, files)) continue;
      WriteFile(outputDir, e.FullPath, r.Extract(e));
    }
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var w = new AdfWriter();
    foreach (var (name, data) in FormatHelpers.FilesOnly(inputs))
      w.AddFile(name, data);
    output.Write(w.Build());
  }
}
