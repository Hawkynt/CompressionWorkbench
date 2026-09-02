#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Asar;

/// <summary>
/// Descriptor for Electron <c>.asar</c> archives — the concatenated-blob format
/// Electron apps use to bundle their sources. Backed by a Chromium
/// <c>Pickle</c>-wrapped JSON directory tree; supports List / Extract / Create.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://github.com/electron/asar</c> — canonical tool and format description (README documents the Pickle header + JSON index + concatenated files layout)</description></item>
///   <item><description>Chromium <c>base/pickle.h</c> — the Pickle serialization the size/header prelude uses</description></item>
/// </list>
/// </summary>
public sealed class AsarFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable {

  /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "Asar";
  /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "Electron Asar";
  /// <summary>
  /// Gets the category.
  /// </summary>
public FormatCategory Category => FormatCategory.Archive;
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  /// <summary>
  /// Gets the default extension.
  /// </summary>
public string DefaultExtension => ".asar";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
public IReadOnlyList<string> Extensions => [".asar"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
public IReadOnlyList<MagicSignature> MagicSignatures => [
    // Size-pickle prelude: uint32 = 4. Low confidence on its own (a bare "4"
    // little-endian collides with other formats), so extension-based dispatch is
    // the primary path and List() validates the full pickle shape.
    new([0x04, 0x00, 0x00, 0x00], Confidence: 0.30),
  ];
  /// <summary>
  /// Gets the methods.
  /// </summary>
public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  /// <summary>
  /// Gets the tar compression format id.
  /// </summary>
public string? TarCompressionFormatId => null;
  /// <summary>
  /// Gets the family.
  /// </summary>
public AlgorithmFamily Family => AlgorithmFamily.Archive;
  /// <summary>
  /// Gets the description.
  /// </summary>
public string Description =>
    "Electron Asar archive (Chromium Pickle header + concatenated file blobs).";

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
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

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    using var r = new AsarReader(stream, leaveOpen: true);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && files.Length > 0 && !MatchesFilter(e.Path, files)) continue;
      WriteFile(outputDir, e.Path, r.ReadData(e));
    }
  }

  /// <summary>
  /// Performs the create operation.
  /// </summary>
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
