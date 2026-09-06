#pragma warning disable CS1591
using Compression.Core.Layout;
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
///
/// Existing-image add/replace/remove is implemented by a flavor-preserving
/// rebuild: genuine STACVOL images remain genuine STACVOL images and Extended
/// images remain Extended. Under the repository's capability contract this is
/// R/W even though it is not a byte-local mutation.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://github.com/sandsmark/dmsdos</c> — dmsdos driver — the de-facto public documentation of the STACVOL layout and cluster compression</description></item>
///   <item><description><c>https://www.rfc-editor.org/rfc/rfc1967</c> — LZS-DCP (the Stac LZS algorithm)</description></item>
///   <item><description><c>https://www.rfc-editor.org/rfc/rfc2395</c> — LZS in IPsec — independent description of the same algorithm</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/Stac_Electronics</c> — Wikipedia article</description></item>
/// </list>
/// </summary>
public sealed class StackerFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveModifiable, IArchiveDefragmentable, IArchiveShrinkable, IWipeEmpty, IFormatOptionsSchema, ILayoutOptimizable {
  // IArchiveShrinkable uses the interface default: a verified extract →
  // re-create rebuild that only replaces the image when the result round-trips
  // AND is smaller; otherwise the original bytes are copied through unchanged.
  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "Stacker";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "Stacker CVF";
  /// <summary>
  /// Gets the category.
  /// </summary>
  public FormatCategory Category => FormatCategory.Archive;
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract
    | FormatCapabilities.CanTest | FormatCapabilities.CanCreate | FormatCapabilities.CanModify;
  /// <summary>
  /// Gets the default extension.
  /// </summary>
  public string DefaultExtension => ".sta";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".sta", ".stk"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    // ASCII "STACKER" at offset 0 — the start of the STACVOL banner sector
    // ("STACKER  version  N    volume:  <path>"), verified against a STACVOL
    // produced by the genuine Stacker 3.10 CREATE tool.
    new([0x53, 0x54, 0x41, 0x43, 0x4B, 0x45, 0x52], Offset: 0, Confidence: 0.90),
  ];
  /// <summary>
  /// Gets the methods.
  /// </summary>
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stacker-lzs", "Stacker LZS")];
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
    "Stacker STACVOL (Stac Electronics, MS-DOS) — banner + Stacker Control Block parsed, "
    + "inner FAT12 directory walked, STORED and Stac-LZS clusters read/written. "
    + "Choose the 'Genuine' layout for byte-exact compatibility with the real Stacker "
    + "driver / dmsdos, or 'Extended' for CompressionWorkbench-only LZS compression.";

  // ── IFormatOptionsSchema ──────────────────────────────────────────────────
  /// <summary>
  /// Gets the options schema.
  /// </summary>
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
    new FormatOptionDescriptor(
      Key: "Method", DisplayName: "Compression", Kind: FormatOptionKind.Enum,
      Default: "Auto",
      AllowedValues: ["Stored", "DS", "SD4", "Auto"],
      Description: "Per-cluster compression for the Genuine layout. Stored = none. "
        + "DS = the 'DS' LZ stream the Stacker driver (and dmsdos) decode. SD4 = Stacker 4 "
        + "native Huffman codec (header 0x0081). Auto = per cluster keep the smaller of DS/SD4, "
        + "else stored.",
      DependsOn: "Compatibility=Genuine"),
    new FormatOptionDescriptor(
      Key: "Level", DisplayName: "Compression level", Kind: FormatOptionKind.Integer,
      Default: "2",
      Description: "Codec search effort (1 = fast, higher = better ratio, slower).",
      DependsOn: "Compatibility=Genuine"),
    new FormatOptionDescriptor(
      Key: "ForceCompress", DisplayName: "Force compression", Kind: FormatOptionKind.Boolean,
      Default: "false",
      Description: "Keep the compressed form even when it does not shrink a cluster.",
      DependsOn: "Compatibility=Genuine"),
  ];

  private static Compression.Registry.Cvf.CvfLzMethod ParseMethod(string s) => s.ToLowerInvariant() switch {
    "ds" => Compression.Registry.Cvf.CvfLzMethod.Ds,
    "sd4" => Compression.Registry.Cvf.CvfLzMethod.Sd4,
    "auto" => Compression.Registry.Cvf.CvfLzMethod.Auto,
    _ => Compression.Registry.Cvf.CvfLzMethod.Stored,
  };

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var data = ReadAll(stream);
    if (TryGenuine(data, out var g))
      return g!.Entries.Select((e, i) => new ArchiveEntryInfo(
        i, e.Name, e.Size, e.Size, "Stored", e.IsDirectory, false, null)).ToList();
    using var r = new StackerReader(new MemoryStream(data));
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stacker-LZS", e.IsDirectory, false, null)).ToList();
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
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

  /// <summary>
  /// Performs the create operation.
  /// </summary>
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
        CompressionMethod = ParseMethod(options.GetOption("Method", "Auto")),
        CompressionLevel = options.GetOptionInt("Level", 2),
        ForceCompress = options.GetOptionBool("ForceCompress", false),
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

  // ── Modify / defrag / purge (rebuild) ─────────────────────────────────────

  /// <summary>
  /// Adds the supplied entry to the target container.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(inputs);
    WriteBack(archive, Rebuild(ReadAll(archive), inputs, null));
  }

  /// <summary>
  /// Removes the specified entry from the target container.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames) {
    ArgumentNullException.ThrowIfNull(entryNames);
    WriteBack(archive, Rebuild(ReadAll(archive), null, entryNames));
  }

  /// <summary>
  /// Performs the defragment operation.
  /// </summary>
  public void Defragment(Stream archive)
    => WriteBack(archive, Rebuild(ReadAll(archive), null, null));

  /// <summary>
  /// Performs the defragment operation.
  /// </summary>
  public void Defragment(Stream archive, DefragOptions options)
    => this.Defragment(archive);

  /// <summary>
  /// Shrinks the CVF by repacking it through the flavor-preserving rebuild —
  /// a genuine STACVOL stays a genuine STACVOL (label kept, auto-best
  /// recompression), an Extended image stays Extended — instead of the
  /// interface default, whose plain re-create would silently convert a
  /// driver-compatible genuine volume into the CompressionWorkbench-only
  /// Extended layout. The rebuilt image is emitted only when it lists the
  /// same file set AND is smaller; otherwise the original bytes are copied
  /// through unchanged, so Shrink never corrupts or grows the source.
  /// </summary>
  public void Shrink(Stream input, Stream output) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);
    var data = ReadAll(input);
    byte[]? rebuilt;
    try {
      rebuilt = Rebuild(data, null, null);
      // Verify the repack is non-lossy before trusting it: same leaf-name multiset.
      var before = this.List(new MemoryStream(data), null)
        .Where(e => !e.IsDirectory).Select(e => LeafLower(e.Name)).Order().ToList();
      var after = this.List(new MemoryStream(rebuilt), null)
        .Where(e => !e.IsDirectory).Select(e => LeafLower(e.Name)).Order().ToList();
      if (!before.SequenceEqual(after))
        rebuilt = null;
    } catch {
      rebuilt = null;
    }
    output.Position = 0;
    output.SetLength(0);
    output.Write(rebuilt is not null && rebuilt.Length > 0 && rebuilt.Length < data.Length ? rebuilt : data);
  }

  /// <summary>Purges unused space by repacking; returns bytes reclaimed.</summary>
  public long WipeUnusedSpace(Stream image, bool wipeClusterTips = true, bool wipeDeletedEntries = true) {
    var data = ReadAll(image);
    var rebuilt = Rebuild(data, null, null);
    WriteBack(image, rebuilt);
    return Math.Max(0, data.Length - rebuilt.Length);
  }

  // Read every file and re-emit: contiguous AMAP packing (defrag), auto-best
  // recompression (optimize/shrink) and a fresh image (purge). Works for the
  // genuine layout (preserving the volume label) and the Extended self-format.
  private static byte[] Rebuild(byte[] data, IReadOnlyList<ArchiveInputInfo>? add, string[]? remove) {
    var keep = new List<(string Name, byte[] Data)>();
    var removeSet = remove is null ? null : new HashSet<string>(remove.Select(LeafLower));
    var label = "";
    var genuine = TryGenuine(data, out var gr);
    if (genuine) {
      label = gr!.VolumeLabel;
      foreach (var e in gr.Entries) {
        if (e.IsDirectory) continue;
        if (removeSet is not null && removeSet.Contains(LeafLower(e.Name))) continue;
        keep.Add((e.Name, gr.Extract(e)));
      }
    } else {
      using var r = new StackerReader(new MemoryStream(data));
      foreach (var e in r.Entries) {
        if (e.IsDirectory) continue;
        if (removeSet is not null && removeSet.Contains(LeafLower(e.Name))) continue;
        keep.Add((e.Name, r.Extract(e)));
      }
    }
    if (add is not null)
      foreach (var input in add) {
        if (input.IsDirectory) continue;
        var n = Path.GetFileName(input.ArchiveName);
        keep.RemoveAll(k => LeafLower(k.Name) == LeafLower(n));
        keep.Add((n, input.ReadContent()));
      }

    if (genuine) {
      var w = new GenuineStackerWriter {
        VolumeLabel = label,
        CompressionMethod = Compression.Registry.Cvf.CvfLzMethod.Auto,
        CompressionLevel = 2,
      };
      foreach (var (n, d) in keep) w.AddFile(n, d);
      return w.Build();
    } else {
      var w = new StackerWriter();
      foreach (var (n, d) in keep) w.AddFile(n, d);
      return w.Build();
    }
  }

  private static string LeafLower(string name) {
    var slash = Math.Max(name.LastIndexOf('/'), name.LastIndexOf('\\'));
    return (slash >= 0 ? name[(slash + 1)..] : name).ToLowerInvariant();
  }

  private static void WriteBack(Stream s, byte[] img) {
    s.Position = 0; s.SetLength(img.Length); s.Write(img); s.Position = 0;
  }
}
