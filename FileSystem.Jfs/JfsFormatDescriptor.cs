#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Jfs;

public sealed class JfsFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations,
                                          IArchiveCreatable, IArchiveWriteConstraints {
  // WORM write constraints.
  public long? MaxTotalArchiveSize => null;
  public long? MinTotalArchiveSize => 16L * 1024 * 1024;
  public string AcceptedInputsDescription =>
    "JFS1 filesystem image; single allocation group, inline-dtree root, up to 8 files.";
  public bool CanAccept(ArchiveInputInfo input, out string? reason) {
    if (!input.IsDirectory) {
      var leaf = Path.GetFileName(input.ArchiveName);
      if (leaf.Length > 11) {
        reason = "JFS writer supports inline-dtree slots only; file names must be ≤ 11 chars.";
        return false;
      }
    }
    reason = null;
    return true;
  }

  public string Id => "Jfs";
  public string DisplayName => "JFS";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries |
    FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".jfs";
  public IReadOnlyList<string> Extensions => [".jfs"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures =>
    [new("JFS1"u8.ToArray(), Offset: 32768, Confidence: 0.90)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "IBM Journaled File System image";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new JfsReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored", e.IsDirectory, false, e.LastModified
    )).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new JfsReader(stream);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var w = new JfsWriter();
    foreach (var i in inputs) {
      if (i.IsDirectory) continue;
      w.AddFile(i.ArchiveName, File.ReadAllBytes(i.FullPath));
    }
    w.WriteTo(output);
  }
}
