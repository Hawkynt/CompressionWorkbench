#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Stacker;

/// <summary>
/// Read-only descriptor for Stacker CVF (Stac Electronics, MS-DOS 5/6).
/// Stacker is the historical predecessor of Microsoft's DoubleSpace
/// (DOS 6.0) and DriveSpace (DOS 6.22 / Win 95) — it wraps a compressed
/// FAT inside an SCB (Stacker Compressed Block) container.
/// Detection is by the "STK" + version-byte magic at file offset 0.
/// </summary>
public sealed class StackerFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  public string Id => "Stacker";
  public string DisplayName => "Stacker CVF";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest;
  public string DefaultExtension => ".sta";
  public IReadOnlyList<string> Extensions => [".sta", ".stk"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    // "STK" + version byte 3 (Stacker 3.x).
    new([0x53, 0x54, 0x4B, 0x03], Offset: 0, Confidence: 0.90),
    // "STK" + version byte 4 (Stacker 4.x).
    new([0x53, 0x54, 0x4B, 0x04], Offset: 0, Confidence: 0.90),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stacker-lzs", "Stacker LZS")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "Stacker CVF (Stac Electronics, MS-DOS 5/6) — stub: detection-only, inner volume surfaced opaque.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    using var r = new StackerReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stacker-LZS", e.IsDirectory, false, null)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    using var r = new StackerReader(stream);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && files.Length > 0 && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }
}
