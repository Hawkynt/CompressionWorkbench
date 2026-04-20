#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Nrg;

public sealed class NrgFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  public string Id => "Nrg";
  public string DisplayName => "NRG";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries |
    FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".nrg";
  public IReadOnlyList<string> Extensions => [".nrg"];
  public IReadOnlyList<string> CompoundExtensions => [];
  // NRG magic is a footer signature ("NER5" or "NERO" at a variable offset from EOF),
  // which cannot be represented as a fixed-offset MagicSignature.
  // Detection relies on the file extension and footer heuristic in NrgReader.
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("iso9660", "ISO 9660")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Nero Burning ROM disc image";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var r = new NrgReader(stream, leaveOpen: true);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(i, e.FullPath, e.Size,
      e.Size, "iso9660", e.IsDirectory, false, null)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var r = new NrgReader(stream, leaveOpen: true);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && !MatchesFilter(e.FullPath, files)) continue;
      WriteFile(outputDir, e.FullPath, r.Extract(e));
    }
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    // WORM: ISO 9660 image followed by an NRG v2 ("NER5") footer. The reader
    // only uses the footer to determine version; the chunk-table offset isn't
    // dereferenced for ISO extraction, so we point it at end-of-file.
    var iso = new FileFormat.Iso.IsoWriter();
    foreach (var (name, data) in FlatFiles(inputs))
      iso.AddFile(name, data);
    var image = iso.Build();
    output.Write(image);
    // Footer: "NER5" (4 bytes) + uint64 BE chunk-table offset.
    Span<byte> footer = stackalloc byte[12];
    footer[0] = (byte)'N'; footer[1] = (byte)'E'; footer[2] = (byte)'R'; footer[3] = (byte)'5';
    System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(footer[4..], (ulong)image.Length);
    output.Write(footer);
  }
}
