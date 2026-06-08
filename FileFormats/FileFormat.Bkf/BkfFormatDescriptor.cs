#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Bkf;

/// <summary>
/// Microsoft NTBackup (<c>.bkf</c>) — Microsoft Tape Format (MTF) v1.0
/// container. Read-only: enumerates FILE/DIRB entries and extracts the
/// <c>STAN</c> (Standard) data streams. Compressed streams are surfaced as
/// "compressed" in the listing; the MTF spec does not name a compression
/// algorithm and most ntbackup.exe writes are uncompressed.
/// </summary>
public sealed class BkfFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {

  public string Id => "Bkf";
  public string DisplayName => "Microsoft NTBackup (MTF)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".bkf";
  public IReadOnlyList<string> Extensions => [".bkf"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures =>
    // First DBLK is always TAPE — 4 ASCII bytes at offset 0.
    [new("TAPE"u8.ToArray(), Offset: 0, Confidence: 0.95)];
  public IReadOnlyList<FormatMethodInfo> Methods => [
    new("stored", "Stored"),
    new("compressed", "Compressed (passthrough)"),
  ];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Microsoft NTBackup .bkf — MTF DBLK-chain reader (FILE+DATA, R/O)";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new BkfReader(stream);
    var result = new List<ArchiveEntryInfo>(r.Entries.Count);
    for (var i = 0; i < r.Entries.Count; ++i) {
      var e = r.Entries[i];
      var method = e.IsDirectory ? "stored" : (e.IsCompressed ? "compressed" : "stored");
      result.Add(new ArchiveEntryInfo(
        i, e.Name, e.Size, e.Size, method, e.IsDirectory, false, null
      ));
    }
    return result;
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new BkfReader(stream);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      // Skip compressed payloads — MTF does not name the algorithm and we
      // refuse to fake content. They show up in List() so callers know.
      if (e.IsCompressed) continue;
      var data = r.Extract(e);
      WriteFile(outputDir, e.Name, data);
    }
  }
}
