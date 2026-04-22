#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.AppleDos;

public sealed class AppleDosFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveWriteConstraints {

  public long? MaxTotalArchiveSize => AppleDosReader.StandardSize;
  public string AcceptedInputsDescription =>
    "Apple DOS 3.3 disk (35 tracks x 16 sectors x 256 bytes = 143 360 bytes).";
  public bool CanAccept(ArchiveInputInfo input, out string? reason) { reason = null; return true; }

  /// <summary>The Apple DOS 3.3 format has exactly one canonical image size.</summary>
  public IReadOnlyList<long> CanonicalSizes => [AppleDosReader.StandardSize];

  public string Id => "AppleDos";
  public string DisplayName => "Apple DOS 3.3";
  public FormatCategory Category => FormatCategory.Archive;

  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;

  public string DefaultExtension => ".dsk";
  public IReadOnlyList<string> Extensions => [".dsk", ".do"];
  public IReadOnlyList<string> CompoundExtensions => [];

  // DOS 3.3 has no magic bytes — detection is extension + VTOC sanity (handled
  // by attempting a parse). We keep the magic list empty and let FormatDetector
  // fall back to extension matching.
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Apple II DOS 3.3 floppy disk image";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    using var r = new AppleDosReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored", false, false, null
    )).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    using var r = new AppleDosReader(stream);
    foreach (var e in r.Entries) {
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var total = 0L;
    foreach (var i in inputs) if (!i.IsDirectory) total += new FileInfo(i.FullPath).Length;
    if (this.MaxTotalArchiveSize is long cap && total > cap)
      throw new InvalidOperationException(
        $"AppleDOS: combined input size {total} bytes exceeds disk capacity ({cap} bytes).");

    var w = new AppleDosWriter();
    foreach (var (name, data) in FlatFiles(inputs))
      w.AddFile(name, data);
    output.Write(w.Build());
  }
}
