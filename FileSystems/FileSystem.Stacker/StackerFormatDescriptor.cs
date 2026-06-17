#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Stacker;

/// <summary>
/// Descriptor for the Stacker STACVOL compressed volume (Stac Electronics,
/// MS-DOS) — the historical predecessor of Microsoft's DoubleSpace (DOS 6.0)
/// and DriveSpace (DOS 6.22 / Win 95). A STACVOL wraps a compressed inner
/// FAT12 volume behind an ASCII banner and a Stacker Control Block (BPB);
/// clusters are STORED verbatim or Stac-LZS compressed (RFC 1967/2395).
/// Detection is by the ASCII "STACKER" banner at file offset 0.
/// </summary>
public sealed class StackerFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable {
  public string Id => "Stacker";
  public string DisplayName => "Stacker CVF";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract
    | FormatCapabilities.CanTest | FormatCapabilities.CanCreate;
  public string DefaultExtension => ".sta";
  public IReadOnlyList<string> Extensions => [".sta", ".stk"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    // ASCII "STACKER" at offset 0 — the start of the STACVOL banner sector
    // ("STACKER  version  N    volume:  <path>"), verified against a STACVOL
    // produced by the genuine Stacker 3.10 CREATE tool.
    new([0x53, 0x54, 0x41, 0x43, 0x4B, 0x45, 0x52], Offset: 0, Confidence: 0.90),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stacker-lzs", "Stacker LZS")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "Stacker STACVOL (Stac Electronics, MS-DOS) — banner + Stacker Control Block parsed, "
    + "inner FAT12 directory walked, STORED and Stac-LZS clusters read/written.";

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

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    var writer = new StackerWriter();
    foreach (var input in inputs) {
      if (input.IsDirectory) continue;
      writer.AddFile(Path.GetFileName(input.ArchiveName), input.ReadContent());
    }

    var image = writer.Build();
    output.Write(image, 0, image.Length);
  }
}
