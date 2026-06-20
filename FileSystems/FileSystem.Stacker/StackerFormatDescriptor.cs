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
public sealed class StackerFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IFormatOptionsSchema {
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
    + "inner FAT12 directory walked, STORED and Stac-LZS clusters read/written. "
    + "Choose the 'Genuine' layout for byte-exact compatibility with the real Stacker "
    + "driver / dmsdos, or 'Extended' for CompressionWorkbench-only LZS compression.";

  // ── IFormatOptionsSchema ──────────────────────────────────────────────────
  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    new FormatOptionDescriptor(
      Key: "Compatibility",
      DisplayName: "On-disk layout",
      Kind: FormatOptionKind.Enum,
      Default: "Extended",
      AllowedValues: ["Genuine", "Extended"],
      Description:
        "Genuine — the real Stac Electronics STACVOL layout (obfuscated superblock + "
        + "emulated boot block + interleaved AMAP). Mounted and read byte-exact by the "
        + "independent dmsdos driver (and by the original Stacker 3.x/4.x DOS driver). "
        + "Clusters are STORED (uncompressed); single flat root directory; up to ~511 "
        + "clusters. Use this for interoperability with real Stacker tooling.\n"
        + "Extended — CompressionWorkbench's own layout (STKMAP01 sector-map trailer) with "
        + "Stac-LZS per-cluster compression. Smaller images, but readable ONLY by "
        + "CompressionWorkbench — NOT by the genuine Stacker driver or dmsdos."),
    new FormatOptionDescriptor(
      Key: "Version",
      DisplayName: "Stacker version",
      Kind: FormatOptionKind.Enum,
      Default: "3",
      AllowedValues: ["3", "4"],
      Description:
        "Stacker format version stamped into the volume superblock. 3 = Stacker 3.x "
        + "(MS-DOS 6 era); 4 = Stacker 4.x. The dmsdos driver reads both. Applies to the "
        + "Genuine layout only.",
      DependsOn: "Compatibility=Genuine"),
    new FormatOptionDescriptor(
      Key: "VolumeLabel", DisplayName: "Volume label", Kind: FormatOptionKind.String,
      Default: "",
      Description: "Optional 11-char inner-volume label written to the root directory (Genuine layout only).",
      DependsOn: "Compatibility=Genuine"),
    new FormatOptionDescriptor(
      Key: "Timestamp", DisplayName: "File timestamp", Kind: FormatOptionKind.String,
      Default: "",
      Description: "Optional ISO-8601 date/time (e.g. 1994-02-01) stamped on every file's "
        + "FAT directory entry. Blank leaves the date/time unset (Genuine layout only).",
      DependsOn: "Compatibility=Genuine"),
  ];

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var data = ReadAll(stream);
    if (TryGenuine(data, out var g))
      return g!.Entries.Select((e, i) => new ArchiveEntryInfo(
        i, e.Name, e.Size, e.Size, "Stored", e.IsDirectory, false, null)).ToList();
    using var r = new StackerReader(new MemoryStream(data));
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stacker-LZS", e.IsDirectory, false, null)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var data = ReadAll(stream);
    if (TryGenuine(data, out var g)) {
      foreach (var e in g!.Entries) {
        if (e.IsDirectory) continue;
        if (files != null && files.Length > 0 && !MatchesFilter(e.Name, files)) continue;
        WriteFile(outputDir, e.Name, g.Extract(e));
      }
      return;
    }
    using var r = new StackerReader(new MemoryStream(data));
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && files.Length > 0 && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    ArgumentNullException.ThrowIfNull(options);

    var genuine = options.GetOption("Compatibility", "Extended")
      .Equals("Genuine", StringComparison.OrdinalIgnoreCase);

    byte[] image;
    if (genuine) {
      var w = new GenuineStackerWriter {
        Version = options.GetOptionInt("Version", 3),
        VolumeLabel = options.GetOption("VolumeLabel", ""),
        Timestamp = FatDirStamp.Parse(options.GetOption("Timestamp", "")),
      };
      foreach (var input in inputs) {
        if (input.IsDirectory) continue;
        w.AddFile(Path.GetFileName(input.ArchiveName), input.ReadContent());
      }
      image = w.Build();
    } else {
      var w = new StackerWriter();
      foreach (var input in inputs) {
        if (input.IsDirectory) continue;
        w.AddFile(Path.GetFileName(input.ArchiveName), input.ReadContent());
      }
      image = w.Build();
    }

    output.Write(image, 0, image.Length);
  }

  // Reads the whole stream once so we can probe both layouts non-destructively.
  private static byte[] ReadAll(Stream stream) {
    if (stream.CanSeek) stream.Position = 0;
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return ms.ToArray();
  }

  // Genuine STACVOLs carry the obfuscated 0x1A0A superblock signature; the
  // reader throws InvalidDataException for the Extended (STKMAP01) layout.
  private static bool TryGenuine(byte[] data, out GenuineStackerReader? reader) {
    try {
      reader = new GenuineStackerReader(new MemoryStream(data));
      return true;
    } catch (InvalidDataException) {
      reader = null;
      return false;
    }
  }
}
