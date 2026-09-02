#pragma warning disable CS1591
using Compression.Registry;

namespace FileFormat.UnityBundle;

/// <summary>
/// Unity Asset Bundle (<c>.unity3d</c> / <c>.assets</c> / <c>.bundle</c>) — the UnityFS container
/// that ships serialized Unity assets bundled for runtime loading. Each bundled asset is listed
/// as a Node entry (path from the internal directory). Storage blocks can be stored, LZMA, or
/// LZ4/LZ4HC-compressed; all four are supported for reading and fresh UnityFS creation.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://docs.unity3d.com/Manual/AssetBundlesIntro.html</c> — official Unity AssetBundle documentation</description></item>
///   <item><description><c>https://github.com/K0lb3/UnityPy</c> — UnityPy — open UnityFS parser/writer interoperability reference</description></item>
///   <item><description><c>https://github.com/Perfare/AssetStudio</c> — AssetStudio — widely used bundle inspector</description></item>
/// </list>
/// </summary>
public sealed class UnityBundleFormatDescriptor :
    IFormatDescriptor,
    IArchiveFormatOperations,
    IArchiveCreatable,
    IArchiveModifiable,
    IArchiveDefragmentable,
    IFormatOptionsSchema {

    /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "UnityBundle";
    /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "Unity Asset Bundle";
    /// <summary>
  /// Gets the category.
  /// </summary>
public FormatCategory Category => FormatCategory.Archive;
    /// <summary>
  /// Gets the capabilities.
  /// </summary>
public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries |
    FormatCapabilities.SupportsOptimize;
    /// <summary>
  /// Gets the default extension.
  /// </summary>
public string DefaultExtension => ".bundle";
    /// <summary>
  /// Gets the extensions.
  /// </summary>
public IReadOnlyList<string> Extensions => [".bundle", ".unity3d", ".assetbundle"];
    /// <summary>
  /// Gets the compound extensions.
  /// </summary>
public IReadOnlyList<string> CompoundExtensions => [];
    /// <summary>
  /// Gets the magic signatures.
  /// </summary>
public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("UnityFS\0"u8.ToArray(), Confidence: 0.95),
    new("UnityWeb\0"u8.ToArray(), Confidence: 0.90),
    new("UnityRaw\0"u8.ToArray(), Confidence: 0.90),
    new("UnityArchive\0"u8.ToArray(), Confidence: 0.90),
  ];
    /// <summary>
  /// Gets the methods.
  /// </summary>
public IReadOnlyList<FormatMethodInfo> Methods => [
    new("auto", "Auto (Store/LZ4HC)", SupportsOptimize: true),
    new("stored", "Stored"),
    new("lzma", "LZMA", SupportsOptimize: true),
    new("lz4", "LZ4"),
    new("lz4hc", "LZ4HC", SupportsOptimize: true),
  ];
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
    "Unity Engine asset bundle. UnityFS v6-v8 supports fresh Stored/LZMA/LZ4/LZ4HC creation; " +
    "legacy UnityWeb/UnityRaw/UnityArchive signatures remain read-only because their container " +
    "layout is distinct. Add/remove/purge/defrag are verified rebuild-backed WORM verbs for " +
    "UnityFS only, so CanModify is intentionally not advertised.";

    /// <summary>
  /// Gets the options schema.
  /// </summary>
public IReadOnlyList<FormatOptionDescriptor> OptionsSchema => [
    new("FormatVersion", "UnityFS format version", FormatOptionKind.Enum, "7",
      ["6", "7", "8"], "UnityFS container format version. Version 7+ aligns the header to 16 bytes."),
    new("UnityVersion", "Unity generation version", FormatOptionKind.String, "5.x.x",
      Description: "Generation-version string written into the UnityFS header."),
    new("UnityRevision", "Unity engine revision", FormatOptionKind.String, "2022.3.0f1",
      Description: "Exact engine revision string written into the UnityFS header."),
    new("BlockSize", "Storage block size", FormatOptionKind.Integer, "131072",
      ["32768", "65536", "131072", "262144", "1048576"],
      "Maximum uncompressed bytes per independently compressed UnityFS storage block."),
    new("BlocksInfoCompression", "BlocksInfo compression", FormatOptionKind.Enum, "lz4hc",
      ["stored", "lzma", "lz4", "lz4hc", "auto"],
      "Compression used for the block/directory metadata record."),
    new("BlocksInfoAtEnd", "BlocksInfo at end", FormatOptionKind.Boolean, "false",
      Description: "Store the compressed block/directory table after storage blocks (UnityFS flag 0x80)."),
  ];

    /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var reader = Open(stream);
    var entries = new List<ArchiveEntryInfo>(reader.Nodes.Count);
    for (var i = 0; i < reader.Nodes.Count; ++i) {
      var n = reader.Nodes[i];
      entries.Add(new ArchiveEntryInfo(
        Index: i,
        Name: n.Path,
        OriginalSize: n.Size,
        CompressedSize: n.Size,
        Method: MethodLabel(reader),
        IsDirectory: false,
        IsEncrypted: false,
        LastModified: null));
    }
    return entries;
  }

    /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var reader = Open(stream);
    if (reader.Nodes.Count == 0 || !reader.CanExtract)
      return;

    foreach (var node in reader.Nodes) {
      if (files != null && files.Length > 0 && !FormatHelpers.MatchesFilter(node.Path, files))
        continue;
      FormatHelpers.WriteFile(outputDir, node.Path, reader.ExtractNode(node));
    }
  }

  /// <summary>
  /// Creates a fresh modern UnityFS bundle. The selected method controls storage-block
  /// compression; BlocksInfo compression/layout and Unity header strings are independently
  /// configurable through <see cref="OptionsSchema"/>.
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options)
    => UnityBundleWriter.Write(output, inputs, options);

  /// <summary>
  /// Adds or replaces nodes through the repository's verified extract/re-create path.
  /// This is deliberately rebuild-backed WORM behavior, not a CanModify claim.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    EnsureRebuildableUnityFs(archive);
    RebuildVerb.EditViaRebuild(archive, this, this, tmpDir => {
      foreach (var input in inputs) {
        if (input.IsDirectory || string.IsNullOrEmpty(input.ArchiveName)) continue;
        var normalized = NormalizeRebuildPath(input.ArchiveName);
        var destination = Path.Combine(tmpDir, normalized.Replace('/', Path.DirectorySeparatorChar));
        var directory = Path.GetDirectoryName(destination);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        File.WriteAllBytes(destination, input.ReadContent());
      }
    });
  }

  /// <summary>
  /// Removes named nodes through verified rebuild, which also wipes stale container bytes
  /// because the complete UnityFS image is replaced.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames) {
    EnsureRebuildableUnityFs(archive);
    var remove = new HashSet<string>(entryNames ?? [], StringComparer.OrdinalIgnoreCase);
    RebuildVerb.EditViaRebuild(archive, this, this, tmpDir => {
      foreach (var file in Directory.GetFiles(tmpDir, "*", SearchOption.AllDirectories)) {
        var relative = Path.GetRelativePath(tmpDir, file).Replace('\\', '/');
        if (remove.Contains(relative) || remove.Contains(Path.GetFileName(relative)))
          File.Delete(file);
      }
    });
  }

  /// <summary>
  /// Rebuilds a UnityFS archive with compact contiguous blocks. Legacy UnityWeb/UnityRaw/
  /// UnityArchive containers are rejected because their distinct layout cannot be recreated.
  /// </summary>
  public void Defragment(Stream archive) {
    EnsureRebuildableUnityFs(archive);
    RebuildVerb.RebuildInPlace(archive, this, this);
  }

  private static void EnsureRebuildableUnityFs(Stream archive) {
    ArgumentNullException.ThrowIfNull(archive);
    var reader = Open(archive);
    if (!string.Equals(reader.Signature, "UnityFS", StringComparison.Ordinal) || !reader.CanExtract)
      throw new NotSupportedException(
        "Rebuild-backed Unity bundle verbs require a fully decodable UnityFS container; legacy UnityWeb/UnityRaw/UnityArchive bundles remain read-only.");
    if (archive.CanSeek)
      archive.Position = 0;
  }

  private static string NormalizeRebuildPath(string path) {
    var normalized = path.Replace('\\', '/').TrimStart('/');
    if (normalized.Length == 0 || normalized.EndsWith('/'))
      throw new ArgumentException("UnityFS node path must name a file.", nameof(path));
    foreach (var part in normalized.Split('/'))
      if (part.Length == 0 || part is "." or ".." || part.IndexOf('\0') >= 0)
        throw new ArgumentException("Unsafe UnityFS node path.", nameof(path));
    return normalized;
  }

  private static string MethodLabel(UnityBundleReader reader) {
    if (reader.Blocks.Count == 0) return reader.Signature;
    var methods = reader.Blocks.Select(block => (int)(block.Flags & 0x3F)).Distinct().ToArray();
    if (methods.Length != 1)
      return "Mixed";
    return methods[0] switch {
      0 => "Stored",
      1 => "LZMA",
      2 => "LZ4",
      3 => "LZ4HC",
      _ => $"Unknown({methods[0]})"
    };
  }

  private static UnityBundleReader Open(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    if (stream.CanSeek)
      stream.Position = 0;
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return new UnityBundleReader(ms.ToArray());
  }
}
