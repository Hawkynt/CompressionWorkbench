#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.TrDos;

public sealed class TrDosFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveModifiable {
  public string Id => "TrDos";
  public string DisplayName => "TR-DOS";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate | FormatCapabilities.CanModify |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".trd";
  public IReadOnlyList<string> Extensions => [".trd"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "ZX Spectrum TR-DOS disk image";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new TrDosReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored", false, false, null
    )).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new TrDosReader(stream);
    foreach (var e in r.Entries) {
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var w = new TrDosWriter();
    foreach (var (name, data) in FormatHelpers.FilesOnly(inputs))
      w.AddFile(name.Length > 8 ? name[..8] : name, 'C', data);
    output.Write(w.Build());
  }

  /// <summary>
  /// Adds (or replaces by name) files inside an existing TR-DOS image.
  /// Uses <c>TrDosModifier</c> for true O(touched bytes) random-access I/O —
  /// only the directory sectors, the disk-info sector, and the file's
  /// contiguous data run are touched.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    foreach (var (name, data) in FilesOnly(inputs)) {
      TrDosModifier.RemoveFile(archive, name, wipeData: true);
      TrDosModifier.AddFile(archive, name, (byte)'C', data);
    }
  }

  /// <summary>
  /// Removes the named entries from an existing TR-DOS image. Uses
  /// <c>TrDosModifier</c> for O(touched bytes) random-access I/O.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames) {
    foreach (var name in entryNames)
      TrDosModifier.RemoveFile(archive, name, wipeData: true);
  }
}
