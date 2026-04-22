#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Ar;

public sealed class ArFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable {
  public string Id => "Ar";
  public string DisplayName => "AR";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".a";
  public IReadOnlyList<string> Extensions => [".a", ".ar", ".deb"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [new([(byte)'!', (byte)'<', (byte)'a', (byte)'r', (byte)'c', (byte)'h', (byte)'>', (byte)'\n'], Confidence: 0.95)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("ar", "AR")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Unix ar archive, used for static libraries (.a files)";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new ArReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(i, e.Name, e.Data.Length, e.Data.Length,
      "ar", false, false, e.ModifiedTime.DateTime)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new ArReader(stream);
    foreach (var e in r.Entries) {
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, e.Data);
    }
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var entries = FormatHelpers.FilesOnly(inputs)
      .Select(f => new ArEntry { Name = f.Name, Data = f.Data })
      .ToList();
    using var w = new ArWriter(output, leaveOpen: true);
    w.Write(entries);
  }
}
