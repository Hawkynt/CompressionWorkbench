#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Asar;

/// <summary>
/// Descriptor for Electron <c>.asar</c> archives — the concatenated-blob format
/// Electron apps use to bundle their sources. Backed by a Chromium
/// <c>Pickle</c>-wrapped JSON directory tree; supports List / Extract / Create.
/// </summary>
public sealed class AsarFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable {

  public string Id => "Asar";
  public string DisplayName => "Electron Asar";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".asar";
  public IReadOnlyList<string> Extensions => [".asar"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    // Size-pickle prelude: uint32 = 4. Low confidence on its own (a bare "4"
    // little-endian collides with other formats), so extension-based dispatch is
    // the primary path and List() validates the full pickle shape.
    new([0x04, 0x00, 0x00, 0x00], Confidence: 0.30),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "Electron Asar archive (Chromium Pickle header + concatenated file blobs).";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    using var r = new AsarReader(stream, leaveOpen: true);
    var entries = r.Entries;
    var result = new List<ArchiveEntryInfo>(entries.Count);
    for (var i = 0; i < entries.Count; i++) {
      var e = entries[i];
      result.Add(new ArchiveEntryInfo(
        Index: i, Name: e.Path,
        OriginalSize: e.Size, CompressedSize: e.Size,
        Method: "stored", IsDirectory: e.IsDirectory, IsEncrypted: false, LastModified: null));
    }
    return result;
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    using var r = new AsarReader(stream, leaveOpen: true);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && files.Length > 0 && !MatchesFilter(e.Path, files)) continue;
      WriteFile(outputDir, e.Path, r.ReadData(e));
    }
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var w = new AsarWriter();
    foreach (var input in inputs) {
      if (input.IsDirectory) {
        w.AddDirectory(input.ArchiveName);
        continue;
      }
      w.AddFile(input.ArchiveName, input.ReadContent());
    }
    w.WriteTo(output);
  }
}
