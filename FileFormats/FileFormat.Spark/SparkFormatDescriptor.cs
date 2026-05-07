#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Spark;

public sealed class SparkFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveModifiable {
  public string Id => "Spark";
  public string DisplayName => "Spark";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify | FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;

  /// <summary>
  /// Adds (or replaces by name) files inside an existing Spark archive.
  /// Uses <see cref="SparkModifier"/> — Add appends Stored before the EOA
  /// marker; Remove walks the top-level entry chain and shifts trailing
  /// bytes (no central directory).
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    foreach (var (name, data) in FilesOnly(inputs)) {
      var flat = Path.GetFileName(name);
      SparkModifier.RemoveFile(archive, flat, wipeData: true);
      SparkModifier.AddFile(archive, flat, data);
    }
  }

  /// <summary>Removes named entries; uses <see cref="SparkModifier"/>.</summary>
  public void Remove(Stream archive, string[] entryNames) {
    foreach (var name in entryNames)
      SparkModifier.RemoveFile(archive, Path.GetFileName(name), wipeData: true);
  }

  public string DefaultExtension => ".spk";
  public IReadOnlyList<string> Extensions => [".spk", ".spark"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("spark", "Spark")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "RISC OS Spark archive (Acorn/ARM)";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new SparkReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(i, e.FileName, e.OriginalSize,
      e.CompressedSize, $"Method {e.Method:X2}", e.IsDirectory, false, e.LastModified)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new SparkReader(stream);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.FileName, files)) continue;
      WriteFile(outputDir, e.FileName, r.Extract(e));
    }
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    using var w = new SparkWriter(output, leaveOpen: true);
    foreach (var (name, data) in FormatHelpers.FlatFiles(inputs))
      w.AddFile(name, data, DateTime.Now);
  }
}
