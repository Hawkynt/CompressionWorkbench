#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.ApplePascal;

/// <summary>
/// Descriptor for Apple UCSD Pascal disk volumes (Apple II, Apple III, Lisa
/// Pascal — late 1970s / early 1980s). Volume directory header is at fixed
/// disk block 2 (file offset 0x400); files are stored as contiguous block
/// extents with at most 77 directory entries.
///
/// <para><b>Flat-only by spec.</b> Apple Pascal does not support
/// subdirectories — its 26-byte directory entry has no parent-pointer or
/// nested-volume indirection. Writer / reader treat all inputs as living at
/// the volume root; a leaf-name-only round trip is the maximum possible. This
/// is honest and documented in the writer's xmldoc.</para>
///
/// <para><b>Spec.</b> Apple Pascal Operating System Reference Manual (1980).</para>
/// </summary>
public sealed class ApplePascalFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations,
    IArchiveCreatable, IArchiveDefragmentable, IFilesystemExtentMap, IWipeEmpty, IFormatOptionsSchema {

  public string Id => "ApplePascal";
  public string DisplayName => "Apple UCSD Pascal";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".pvol";
  public IReadOnlyList<string> Extensions => [".pvol", ".pdv", ".pas"];
  public IReadOnlyList<string> CompoundExtensions => [];
  // Apple Pascal volumes have no fixed magic in the boot sector; detection
  // is by extension. The reader's Parse() validates volume-header invariants
  // (type=0, first=0, 6 <= next <= 18, valid name length, plausible block counts).
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "Apple UCSD Pascal disk volume (Apple II/III/Lisa); 512-byte blocks, contiguous extents, max 77 entries; flat (no subdirs).";

  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    new FormatOptionDescriptor(
      Key: "BlockSize",
      DisplayName: "Block size",
      Kind: FormatOptionKind.Enum,
      Default: "512",
      AllowedValues: ["512"],
      Description: "Apple Pascal volumes always use 512-byte blocks — fixed by spec."),
    new FormatOptionDescriptor(
      Key: "VolumeSize",
      DisplayName: "Volume size (blocks)",
      Kind: FormatOptionKind.Enum,
      Default: "Auto",
      AllowedValues: ["Auto", "280", "560", "1024", "1600", "2048"],
      Description: "Total volume size in 512-byte blocks. Pascal convention: multiples of 8 (8-block allocation tiles). 280 = 140 KB SS floppy, 560 = 280 KB DS floppy."),
    new FormatOptionDescriptor(
      Key: "VolumeName",
      DisplayName: "Volume name",
      Kind: FormatOptionKind.String,
      Default: "PASCAL",
      Description: "Volume name (1..7 ASCII chars, uppercased on disk)."),
  ];

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    using var r = new ApplePascalReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored", e.IsDirectory, false, null)).ToList();
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    using var r = new ApplePascalReader(stream);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      if (files != null && files.Length > 0 && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    options ??= new FormatCreateOptions();

    var vsizeLabel = options.GetOption("VolumeSize", "Auto");
    int volumeBlocks;
    if (vsizeLabel.Equals("Auto", StringComparison.OrdinalIgnoreCase)) {
      // Auto: optimizer picks size from the actual file payload.
      var sizes = inputs.Where(i => !i.IsDirectory).Select(i => (long)i.ReadContent().Length).ToList();
      volumeBlocks = ApplePascalOptimizer.Find(sizes).VolumeBlocks;
    } else {
      volumeBlocks = int.TryParse(vsizeLabel, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : 280;
    }
    var volName = options.GetOption("VolumeName", "PASCAL");

    var w = new ApplePascalWriter();
    foreach (var (name, data) in FormatHelpers.FlatFiles(inputs))
      w.AddFile(name, data);
    output.Write(w.Build(volumeBlocks, volName));
  }

  public IEnumerable<DefragBlockInfo> EnumerateExtents(Stream image)
    => ApplePascalExtentMap.Enumerate(image);

  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  public void Defragment(Stream archive, DefragOptions options) {
    DefragRebuilder.Rebuild(archive, options,
      readEntries: stream => {
        using var r = new ApplePascalReader(stream);
        var live = r.Entries.Where(e => !e.IsDirectory).Select(e => (e.Name, r.Extract(e))).ToList();
        return live;
      },
      buildImage: files => {
        var w = new ApplePascalWriter();
        foreach (var (n, d) in files) w.AddFile(n, d);
        // Preserve the image's original block count if known.
        var blocks = (int)(archive.Length / ApplePascalReader.BlockSize);
        if (blocks < 8) blocks = 280;
        return w.Build(blocks);
      });
  }

  public long WipeUnusedSpace(Stream image, bool wipeClusterTips = true, bool wipeDeletedEntries = true) {
    ArgumentNullException.ThrowIfNull(image);
    image.Position = 0;
    var imageSize = image.Length;

    // File-size lookup so cluster-tip wiping zeros the trailing bytes of each
    // contiguous extent beyond the recorded file size.
    Func<string, long>? lookup = null;
    if (wipeClusterTips) {
      try {
        image.Position = 0;
        using var r = new ApplePascalReader(image);
        var map = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in r.Entries)
          if (!e.IsDirectory)
            map[e.Name] = e.Size;
        lookup = n => map.TryGetValue(n, out var s) ? s : -1;
      } catch { lookup = null; }
    }

    image.Position = 0;
    var extents = ApplePascalExtentMap.Enumerate(image);
    return UnusedSpaceWiper.Wipe(image, extents, imageSize, wipeClusterTips, lookup);
  }
}
