using System.Text;
using Compression.Core.FileSystems.Cvf;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.DoubleSpace;

/// <summary>
/// The operations the DoubleSpace and DriveSpace descriptors share over the real MS-DOS 6.x
/// container (<c>MSDBL6.0</c>, version 2): reading through <see cref="GenuineCvfReader"/> (or
/// <see cref="DoubleSpaceReader"/> for real volumes outside its geometry), writing through
/// <see cref="GenuineCvfWriter"/>, and every edit as a rebuild of the whole volume — the layout dmsdos
/// mounts is the only one either descriptor produces.
/// <para>
/// A DriveSpace 3 volume shares the <c>.cvf</c> name and the <c>MSDBL6.0</c> OEM name, so it may
/// arrive here by its file name; it is handed to the DriveSpace 3 code, which reads its 5-byte MDFAT
/// and rebuilds it as DriveSpace 3.
/// </para>
/// </summary>
internal static class GenuineCvfVolume {
  /// <summary>Codecs a version-2 volume records, by method name.</summary>
  public static readonly IReadOnlyDictionary<string, CvfLzMethod> Codecs = new Dictionary<string, CvfLzMethod> {
    ["ds"] = CvfLzMethod.Ds,
    ["jm"] = CvfLzMethod.Jm,
    ["auto"] = CvfLzMethod.Auto,
  };

  /// <summary>Published methods: stored, and DS / JM / Auto at three effort tiers.</summary>
  public static IReadOnlyList<FormatMethodInfo> Methods { get; } = CvfCodecChoice.Methods(
    ("ds", "DoubleSpace DS-0-0 LZ77"),
    ("jm", "DriveSpace JM-0-0 LZ77"),
    ("auto", "Smaller of DS and JM per cluster"));

  /// <summary>Create options of a version-2 volume, <paramref name="defaultMethod"/> preselected.</summary>
  public static IReadOnlyList<FormatOptionDescriptor> Options(string defaultMethod) => [
    new(Key: "VolumeLabel", DisplayName: "Volume label", Kind: FormatOptionKind.String, Default: "",
      Description: "Optional 11-char inner-volume label."),
    new(Key: "Timestamp", DisplayName: "File timestamp", Kind: FormatOptionKind.String, Default: "",
      Description: "Optional ISO-8601 date/time (e.g. 1993-05-01) stamped on every file's FAT directory entry. "
        + "Blank leaves the date/time unset."),
    new(Key: "Method", DisplayName: "Compression", Kind: FormatOptionKind.Enum, Default: defaultMethod,
      AllowedValues: ["Stored", "DS", "JM", "Auto"],
      Description: "Per-cluster compression. Stored = none. DS = DoubleSpace 'DS-0-x' LZ (DOS 6.0/6.2). "
        + "JM = DriveSpace 'JM-0-x' LZ (DOS 6.22). Auto = per cluster the smaller of DS and JM, else stored. "
        + "dmsdos reads all of them."),
    new(Key: "Level", DisplayName: "Compression level", Kind: FormatOptionKind.Integer, Default: "2",
      Description: "Codec search effort (1 = fast, up to 4; higher = better ratio, slower)."),
    new(Key: "ForceCompress", DisplayName: "Force compression", Kind: FormatOptionKind.Boolean, Default: "false",
      Description: "Keep the compressed form even when it does not shrink a cluster."),
  ];

  /// <summary>The container <see cref="GenuineCvfReader"/> reads: MSDBL6.0 with 16 sectors per cluster.</summary>
  public static bool IsGenuineV2(byte[] data) =>
    data.Length > 0x0E && Encoding.ASCII.GetString(data, 3, 8) == "MSDBL6.0" && data[0x0D] == 16;

  private sealed record Volume(IReadOnlyList<DoubleSpaceEntry> Entries, Func<DoubleSpaceEntry, byte[]> Extract, string Label);

  private static Volume Open(byte[] data) {
    if (IsGenuineV2(data)) {
      var g = new GenuineCvfReader(new MemoryStream(data));
      return new(g.Entries, g.Extract, g.VolumeLabel);
    }
    var r = new DoubleSpaceReader(new MemoryStream(data));
    return new(r.Entries, r.Extract, "");
  }

  public static List<ArchiveEntryInfo> List(Stream stream) {
    var data = ReadAll(stream);
    if (DriveSpace3.DriveSpace3FormatDescriptor.IsDriveSpace3(data))
      return new DriveSpace3.DriveSpace3FormatDescriptor().List(new MemoryStream(data), null);
    return Open(data).Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, -1, "cvf", e.IsDirectory, false, null)).ToList();
  }

  public static void Extract(Stream stream, string outputDir, string[]? files) {
    var data = ReadAll(stream);
    if (DriveSpace3.DriveSpace3FormatDescriptor.IsDriveSpace3(data)) {
      new DriveSpace3.DriveSpace3FormatDescriptor().Extract(new MemoryStream(data), outputDir, null, files);
      return;
    }
    var volume = Open(data);
    foreach (var e in volume.Entries) {
      if (e.IsDirectory) continue;
      if (files is { Length: > 0 } && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, volume.Extract(e));
    }
  }

  public static void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options, string defaultCodec) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    ArgumentNullException.ThrowIfNull(options);
    var (method, level) = CvfCodecChoice.Resolve(options, defaultCodec, Codecs);
    var writer = new GenuineCvfWriter {
      VolumeLabel = options.GetOption("VolumeLabel", ""),
      Timestamp = FatDirStamp.Parse(options.GetOption("Timestamp", "")),
      CompressionMethod = method,
      CompressionLevel = level,
      ForceCompress = options.GetOptionBool("ForceCompress", false),
    };
    foreach (var (name, data) in FlatFiles(inputs))
      writer.AddFile(name, data);
    output.Write(writer.Build());
  }

  /// <summary>
  /// The volume rebuilt from its current files, with <paramref name="add"/> added (replacing equal
  /// names) and <paramref name="remove"/> dropped: packed contiguously, every cluster recompressed with
  /// the smaller codec, the inner label kept.
  /// </summary>
  public static byte[] Rebuild(byte[] data, IReadOnlyList<ArchiveInputInfo>? add, string[]? remove) {
    var volume = Open(data);
    var removeSet = remove is null ? null : new HashSet<string>(remove.Select(LeafLower));
    var keep = volume.Entries
      .Where(e => !e.IsDirectory && (removeSet is null || !removeSet.Contains(LeafLower(e.Name))))
      .Select(e => (e.Name, Data: volume.Extract(e)))
      .ToList();
    if (add is not null)
      foreach (var (n, d) in FlatFiles(add)) {
        keep.RemoveAll(k => LeafLower(k.Name) == LeafLower(n));
        keep.Add((n, d));
      }
    var writer = new GenuineCvfWriter { VolumeLabel = volume.Label, CompressionMethod = CvfLzMethod.Auto, CompressionLevel = 2 };
    foreach (var (n, d) in keep) writer.AddFile(n, d);
    return writer.Build();
  }

  /// <summary>Rebuilds <paramref name="image"/> in place; returns the bytes the volume shrank by.</summary>
  public static long RebuildInPlace(Stream image, IReadOnlyList<ArchiveInputInfo>? add = null, string[]? remove = null) {
    var data = ReadAll(image);
    if (DriveSpace3.DriveSpace3FormatDescriptor.IsDriveSpace3(data))
      return DriveSpace3.DriveSpace3FormatDescriptor.RebuildInPlace(image, add, remove);
    var rebuilt = Rebuild(data, add, remove);
    WriteBack(image, rebuilt);
    return Math.Max(0, data.Length - rebuilt.Length);
  }

  public static byte[] ReadAll(Stream stream) {
    if (stream.CanSeek) stream.Position = 0;
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return ms.ToArray();
  }

  public static void WriteBack(Stream s, byte[] img) {
    s.Position = 0; s.SetLength(img.Length); s.Write(img); s.Position = 0;
  }

  public static string LeafLower(string name) {
    var slash = Math.Max(name.LastIndexOf('/'), name.LastIndexOf('\\'));
    return (slash >= 0 ? name[(slash + 1)..] : name).ToLowerInvariant();
  }
}
