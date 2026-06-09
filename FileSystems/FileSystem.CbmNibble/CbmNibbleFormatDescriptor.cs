#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.CbmNibble;

/// <summary>
/// Shared implementation for the .nib + .g64 Commodore nibble-dump pseudo-archives.
/// Both variants share the same section walk: <c>metadata.ini</c> plus one
/// <c>track_{NN}.bin</c> entry per (half-)track containing raw GCR bytes.
/// Converting GCR back to a cleanly sectored D64 is a separate, non-trivial
/// undertaking (see nibtools); this descriptor is intentionally read-only.
/// </summary>
internal static class CbmNibbleEntries {
  public static List<(string Name, byte[] Data)> Build(Stream stream, string? fileName) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var img = CbmNibbleReader.Read(ms.GetBuffer().AsSpan(0, (int)ms.Length), fileName);

    var result = new List<(string, byte[])> {
      ("metadata.ini", CbmNibbleReader.BuildMetadata(img)),
    };
    foreach (var t in img.Tracks) {
      if (t.Data.Length == 0) continue;  // skip empty half-tracks
      result.Add(($"track_{t.Index:D2}.bin", t.Data));
    }
    return result;
  }

  public static List<ArchiveEntryInfo> List(Stream stream, string? fileName) =>
    Build(stream, fileName).Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Data.LongLength, e.Data.LongLength, "stored", false, false, null
    )).ToList();

  public static void Extract(Stream stream, string outputDir, string[]? files, string? fileName) {
    foreach (var e in Build(stream, fileName)) {
      if (files != null && files.Length > 0 && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, e.Data);
    }
  }
}

/// <summary>
/// Commodore G64 GCR track container (VICE emulator). Detected by the
/// 8-byte "GCR-1541" ASCII magic at offset 0.
///
/// <para><b>WORM tier.</b> <see cref="Create"/> emits a G64 v2 image with
/// the canonical 35-track / 84-half-track layout, spec-correct speed-zone
/// table, SYNC + header + data framing on every sector, and a minimal CBM
/// DOS BAM + directory on track 18. Files are GCR-encoded (4-to-5 nibble
/// mapping) and placed on tracks 1..17 in track-order with up to eight
/// directory entries. The reader still surfaces per-track raw GCR data —
/// see <see cref="CbmNibbleReader"/>.</para>
/// </summary>
public sealed class G64FormatDescriptor : IFormatDescriptor, IArchiveFormatOperations,
    IArchiveCreatable, IFormatOptionsSchema {
  public string Id => "G64";
  public string DisplayName => "G64 (Commodore GCR)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.CanCreate | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".g64";
  public IReadOnlyList<string> Extensions => [".g64"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures =>
    [new("GCR-1541"u8.ToArray(), Offset: 0, Confidence: 0.90)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Commodore 1541 GCR-encoded disk image (VICE G64)";

  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    new FormatOptionDescriptor(
      Key: "DiskName",
      DisplayName: "Disk label",
      Kind: FormatOptionKind.String,
      Default: "WORMDISK",
      Description: "CBM disk label (max 16 chars, uppercased and PETSCII-mapped)."),
    new FormatOptionDescriptor(
      Key: "DiskId",
      DisplayName: "Disk ID",
      Kind: FormatOptionKind.String,
      Default: "WW",
      Description: "Two-character disk identifier (BAM offsets 0xA2..0xA3)."),
    new FormatOptionDescriptor(
      Key: "MaxTrackSize",
      DisplayName: "Max track size (bytes)",
      Kind: FormatOptionKind.Integer,
      Default: CbmNibbleWriter.DefaultMaxTrackSize.ToString(System.Globalization.CultureInfo.InvariantCulture),
      Description: "Per-track byte budget (G64 header field at offset 0x0A). 7928 is the VICE reference value."),
  ];

  public List<ArchiveEntryInfo> List(Stream stream, string? password) =>
    CbmNibbleEntries.List(stream, "image.g64");

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) =>
    CbmNibbleEntries.Extract(stream, outputDir, files, "image.g64");

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    options ??= new FormatCreateOptions();

    var diskName = options.GetOption("DiskName", "WORMDISK");
    var diskId = options.GetOption("DiskId", "WW");
    var maxTrackSize = options.GetOptionInt("MaxTrackSize", CbmNibbleWriter.DefaultMaxTrackSize);

    var w = new CbmNibbleWriter();
    foreach (var input in inputs.Where(i => !i.IsDirectory))
      w.AddFile(input.ArchiveName, input.ReadContent());
    output.Write(w.Build(diskName, diskId, maxTrackSize));
  }
}

/// <summary>
/// Commodore NIB raw nibble dump (nibtools / ZoomFloppy). No magic header —
/// detected by file extension only; the typical dump is exactly 84 × 8192 bytes.
/// </summary>
public sealed class NibFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  public string Id => "Nib";
  public string DisplayName => "NIB (Commodore nibble dump)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".nib";
  public IReadOnlyList<string> Extensions => [".nib"];
  public IReadOnlyList<string> CompoundExtensions => [];
  // NIB has no leading magic — detection is purely extension-based.
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "Commodore 1541 raw nibble dump (nibtools / ZoomFloppy)";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) =>
    CbmNibbleEntries.List(stream, "image.nib");

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) =>
    CbmNibbleEntries.Extract(stream, outputDir, files, "image.nib");
}
