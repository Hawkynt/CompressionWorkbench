using System.Buffers.Binary;
using System.Text;
using Compression.Core.FileSystems.Cvf;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.DoubleSpace;

/// <summary>
/// The product a version-2 volume is created as: DoubleSpace (MS-DOS 6.0/6.2) writes DS clusters,
/// DriveSpace (MS-DOS 6.22) JM clusters — each only the codec the real driver of that product writes.
/// </summary>
internal sealed record CvfV2Product(string Codec, CvfLzMethod Method, string OptionValue, string Description) {
  /// <summary>DoubleSpace: DS-0-x clusters.</summary>
  public static CvfV2Product DoubleSpace { get; } = new("ds", CvfLzMethod.Ds, "DS", "DoubleSpace DS-0-0 LZ77");

  /// <summary>DriveSpace: JM-0-0 clusters.</summary>
  public static CvfV2Product DriveSpace { get; } = new("jm", CvfLzMethod.Jm, "JM", "DriveSpace JM-0-0 LZ77");

  /// <summary>The one codec name the product's Create accepts.</summary>
  public IReadOnlyDictionary<string, CvfLzMethod> Codecs { get; } = new Dictionary<string, CvfLzMethod> { [Codec] = Method };

  /// <summary>Published methods: stored, and the product's codec at three effort tiers.</summary>
  public IReadOnlyList<FormatMethodInfo> Methods { get; } = CvfCodecChoice.Methods((Codec, Description));
}

/// <summary>
/// The operations the DoubleSpace and DriveSpace descriptors share over the real MS-DOS 6.x
/// container (<c>MSDBL6.0</c>, version 2): reading through <see cref="GenuineCvfReader"/> (or
/// <see cref="DoubleSpaceReader"/> for real volumes outside its geometry), writing through
/// <see cref="GenuineCvfWriter"/>, and every edit as a rebuild of the whole volume — the layout dmsdos
/// mounts is the only one either descriptor produces.
/// <para>
/// A version-2 volume may hold DS clusters (DoubleSpace, and Windows 95 on DoubleSpace volumes) and JM
/// clusters (DriveSpace 6.22), both read; a rebuild keeps the codec the volume already uses. SQ-0-0 is a
/// DriveSpace 3 codec and is refused in a version-2 volume.
/// </para>
/// <para>
/// A DriveSpace 3 volume shares the <c>.cvf</c> name and the <c>MSDBL6.0</c> OEM name, so it may
/// arrive here by its file name; it is handed to the DriveSpace 3 code, which reads its 5-byte MDFAT
/// and rebuilds it as DriveSpace 3.
/// </para>
/// </summary>
internal static class GenuineCvfVolume {

  /// <summary>Create options of a version-2 volume created as <paramref name="product"/>.</summary>
  public static IReadOnlyList<FormatOptionDescriptor> Options(CvfV2Product product) => [
    new(Key: "VolumeLabel", DisplayName: "Volume label", Kind: FormatOptionKind.String, Default: "",
      Description: "Optional 11-char inner-volume label."),
    new(Key: "Timestamp", DisplayName: "File timestamp", Kind: FormatOptionKind.String, Default: "",
      Description: "Optional ISO-8601 date/time (e.g. 1993-05-01) stamped on every file's FAT directory entry. "
        + "Blank leaves the date/time unset."),
    new(Key: "Method", DisplayName: "Compression", Kind: FormatOptionKind.Enum, Default: product.OptionValue,
      AllowedValues: ["Stored", product.OptionValue],
      Description: $"Per-cluster compression. Stored = none. {product.OptionValue} = {product.Description}, "
        + "the codec the real driver writes for this product. dmsdos reads both."),
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

  public static void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options, CvfV2Product product) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    ArgumentNullException.ThrowIfNull(options);
    var (method, level) = CvfCodecChoice.Resolve(options, product.Codec, product.Codecs);
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
  /// names) and <paramref name="remove"/> dropped: packed contiguously, the inner label kept, every
  /// cluster recompressed with the codec the volume already uses (JM if it holds any JM cluster, DS if
  /// it holds DS ones), or <paramref name="product"/>'s codec for a volume without compressed clusters.
  /// </summary>
  public static byte[] Rebuild(byte[] data, IReadOnlyList<ArchiveInputInfo>? add, string[]? remove, CvfV2Product product) {
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
    var writer = new GenuineCvfWriter { VolumeLabel = volume.Label, CompressionMethod = CodecInUse(data) ?? product.Method, CompressionLevel = 2 };
    foreach (var (n, d) in keep) writer.AddFile(n, d);
    return writer.Build();
  }

  /// <summary>
  /// The compressed-cluster codec a version-2 volume uses, read from the method headers its MDFAT
  /// points at: JM when any cluster is JM, else DS when any is DS, else <see langword="null"/>.
  /// </summary>
  internal static CvfLzMethod? CodecInUse(byte[] data) {
    if (data.Length < 0x30) return null;
    var sectorSize = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(0x0B)) is var b and > 0 and <= 4096 ? b : 512;
    var mdfat = (BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(0x24)) + 1) * sectorSize
      + BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(0x2D)) * 4;
    var sawDs = false;
    for (var cluster = 2; cluster < 0xFF0; ++cluster) {
      var at = mdfat + cluster * 4;
      if (at + 4 > data.Length) break;
      var entry = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at));
      if ((entry & 0x8000_0000u) == 0 || (entry & 0x4000_0000u) != 0) continue;   // free or stored
      var payload = (long)((entry & 0x1FFFFFu) + 1) * sectorSize;
      if (payload + 2 > data.Length) continue;
      if (data[payload] == (byte)'J' && data[payload + 1] == (byte)'M') return CvfLzMethod.Jm;
      if (data[payload] == (byte)'D' && data[payload + 1] == (byte)'S') sawDs = true;
    }
    return sawDs ? CvfLzMethod.Ds : null;
  }

  /// <summary>Rebuilds <paramref name="image"/> in place; returns the bytes the volume shrank by.</summary>
  public static long RebuildInPlace(Stream image, CvfV2Product product, IReadOnlyList<ArchiveInputInfo>? add = null, string[]? remove = null) {
    var data = ReadAll(image);
    if (DriveSpace3.DriveSpace3FormatDescriptor.IsDriveSpace3(data))
      return DriveSpace3.DriveSpace3FormatDescriptor.RebuildInPlace(image, add, remove);
    var rebuilt = Rebuild(data, add, remove, product);
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
