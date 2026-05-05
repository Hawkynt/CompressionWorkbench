#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Ufs;

public sealed class UfsFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations,
                                          IArchiveCreatable, IArchiveWriteConstraints {
  public long? MaxTotalArchiveSize => null;
  public long? MinTotalArchiveSize => 16L * 1024 * 1024;
  public string AcceptedInputsDescription =>
    "UFS1 filesystem image; single cylinder group, flat root, direct-block files (< 96 KB).";
  public bool CanAccept(ArchiveInputInfo input, out string? reason) {
    reason = null;
    return true;
  }

  public string Id => "Ufs";
  public string DisplayName => "UFS";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".ufs";
  public IReadOnlyList<string> Extensions => [".ufs"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures =>
    [new([0x54, 0x19, 0x01, 0x00], Offset: 8192 + 1372, Confidence: 0.90)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Unix File System (UFS1) image";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new UfsReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored", e.IsDirectory, false, e.LastModified
    )).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new UfsReader(stream);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var w = new UfsWriter();
    foreach (var i in inputs) {
      if (i.IsDirectory) continue;
      w.AddFile(i.ArchiveName, File.ReadAllBytes(i.FullPath));
    }
    w.WriteTo(output);
  }
}
