#pragma warning disable CS1591
using Compression.Registry;

namespace FileFormat.UnrealPak;

/// <summary>
/// Legacy-index Unreal Engine Pak archive. Versions 1-7 are read with strict index/entry
/// SHA-1 verification and block-aware Stored/Zlib extraction. Fresh archives are emitted as
/// the widely interoperable version-3 layout. Version 8+ compression-name/path-hash index
/// generations and IoStore (<c>.utoc</c>/<c>.ucas</c>) are intentionally separate concerns.
///
/// References:
/// <list type="bullet">
///   <item><description>Epic <c>FPakInfo</c>/<c>FPakEntry</c> in Runtime/PakFile</description></item>
///   <item><description><c>https://github.com/panzi/u4pak</c> — open UE4 legacy-index reader/packer</description></item>
/// </list>
/// </summary>
public sealed class UnrealPakFormatDescriptor :
    IFormatDescriptor,
    IArchiveFormatOperations,
    IArchiveCreatable,
    IArchiveModifiable,
    IArchiveDefragmentable,
    IFormatOptionsSchema {

  /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "UnrealPak";
  /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "Unreal Pak";
  /// <summary>
  /// Gets the category.
  /// </summary>
public FormatCategory Category => FormatCategory.Archive;
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  /// <summary>
  /// Gets the default extension.
  /// </summary>
public string DefaultExtension => ".pak";

  /// <summary>
  /// Only <c>.pak</c> belongs here. <c>.utoc</c>/<c>.ucas</c> are Unreal IoStore containers,
  /// which have a different TOC/chunk layout and must not be routed through the Pak parser.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".pak"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
public IReadOnlyList<string> CompoundExtensions => [];

  // Pak magic lives in the footer, not at offset zero. Extension routing disambiguates it from
  // Quake PAK while the reader validates the footer magic and its complete index hash.
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
public IReadOnlyList<MagicSignature> MagicSignatures => [];
  /// <summary>
  /// Gets the methods.
  /// </summary>
public IReadOnlyList<FormatMethodInfo> Methods => [
    new("auto", "Auto (Stored/Zlib)"),
    new("stored", "Stored"),
    new("zlib", "Zlib"),
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
    "Unreal legacy Pak index: v1-v7 read with SHA-1 verification and block-aware Stored/Zlib extraction; " +
    "deterministic v3 creation plus trailer-only v3 add/replace/remove with verified rebuild fallback. " +
    "v8+ modern indexes and IoStore are not falsely claimed.";

  /// <summary>
  /// Gets the options schema.
  /// </summary>
public IReadOnlyList<FormatOptionDescriptor> OptionsSchema => [
    new("CompressionBlockSize", "Zlib block size", FormatOptionKind.Integer, "65536",
      ["16384", "32768", "65536", "131072", "262144", "1048576"],
      "Maximum uncompressed bytes in each independently zlib-compressed Pak v3 block."),
    new("MountPoint", "Pak mount point", FormatOptionKind.String, string.Empty,
      Description: "Legacy Pak mount-point prefix. Empty preserves archive input paths exactly; Unreal projects often use ../../../Game/."),
  ];

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var reader = Open(stream);
    var entries = new List<ArchiveEntryInfo>();
    foreach (var entry in reader.Entries) {
      if (entry.IsDeleted)
        continue;
      entries.Add(new ArchiveEntryInfo(
        Index: entries.Count,
        Name: CombinePath(reader.MountPoint, entry.Path),
        OriginalSize: entry.UncompressedSize,
        CompressedSize: entry.Size,
        Method: MethodLabel(entry),
        IsDirectory: false,
        IsEncrypted: entry.IsEncrypted,
        LastModified: null));
    }
    return entries;
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    ArgumentNullException.ThrowIfNull(outputDir);
    var reader = Open(stream);
    foreach (var entry in reader.Entries) {
      if (entry.IsDeleted || entry.UnsupportedReason != null || entry.IsEncrypted)
        continue;
      var fullPath = CombinePath(reader.MountPoint, entry.Path);
      if (files != null && files.Length > 0 && !FormatHelpers.MatchesFilter(fullPath, files))
        continue;
      FormatHelpers.WriteFile(outputDir, fullPath, reader.Extract(entry));
    }
  }

  /// <summary>
  /// Opens one verified entry as a bounded in-memory stream. Unsupported/encrypted/deleted
  /// entries return an empty bounded stream rather than leaking raw ciphertext or tombstones.
  /// </summary>
  public Stream OpenEntry(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(entryName);
    var reader = Open(archive);
    foreach (var entry in reader.Entries) {
      var fullPath = CombinePath(reader.MountPoint, entry.Path);
      if (!string.Equals(fullPath, entryName, StringComparison.OrdinalIgnoreCase))
        continue;
      if (entry.IsDeleted || entry.UnsupportedReason != null || entry.IsEncrypted)
        return EmptyBoundedStream();
      var bytes = reader.Extract(entry);
      return new Compression.Registry.Streaming.BoundedEntryStream(
        new MemoryStream(bytes, writable: false), bytes.Length, leaveOpen: false);
    }
    return EmptyBoundedStream();
  }

  /// <summary>
  /// Performs the extract entry to memory operation.
  /// </summary>
public byte[] ExtractEntryToMemory(Stream archive, string entryName, string? password) {
    using var stream = this.OpenEntry(archive, entryName, password);
    using var output = new MemoryStream();
    stream.CopyTo(output);
    return output.ToArray();
  }

  /// <summary>
  /// Creates a deterministic Pak v3 archive. <see cref="FormatCreateOptions.MethodName"/> may
  /// be <c>auto</c>, <c>stored</c>, or <c>zlib</c>; v3 deliberately keeps the legacy index and
  /// absolute compression-block offsets for broad UE4-era interoperability.
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options)
    => UnrealPakWriter.Write(output, inputs, options);

  /// <summary>
  /// Adds or replaces files. Pak v3 takes the random-access trailer path: changed payload
  /// records are written where the old index began, then only the monolithic index and fixed
  /// footer are regenerated. Untouched payloads remain byte-identical at their original
  /// offsets. Older supported legacy versions fall back to the verified extract/re-create
  /// path because their record/footer profiles have not yet been proven for in-place edits.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    try {
      UnrealPakModifier.Add(archive, inputs);
      return;
    } catch (NotSupportedException) {
      if (archive.CanSeek)
        archive.Position = 0;
    }

    EnsureRebuildable(archive);
    RebuildVerb.EditViaRebuild(archive, this, this, temporaryDirectory => {
      foreach (var input in inputs) {
        if (input.IsDirectory || string.IsNullOrEmpty(input.ArchiveName))
          continue;
        var relative = NormalizeRebuildPath(input.ArchiveName);
        var destination = Path.Combine(temporaryDirectory, relative.Replace('/', Path.DirectorySeparatorChar));
        var directory = Path.GetDirectoryName(destination);
        if (!string.IsNullOrEmpty(directory))
          Directory.CreateDirectory(directory);
        File.WriteAllBytes(destination, input.ReadContent());
      }
    });
  }

  /// <summary>
  /// Removes files. Pak v3 rewrites only the trailing index/footer, then zeros the removed
  /// local record and payload ranges; surviving payloads are neither moved nor recompressed.
  /// Unsupported legacy profiles keep the verified full-rebuild fallback.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames) {
    try {
      UnrealPakModifier.Remove(archive, entryNames);
      return;
    } catch (NotSupportedException) {
      if (archive.CanSeek)
        archive.Position = 0;
    }

    EnsureRebuildable(archive);
    var remove = new HashSet<string>(entryNames ?? [], StringComparer.OrdinalIgnoreCase);
    RebuildVerb.EditViaRebuild(archive, this, this, temporaryDirectory => {
      foreach (var file in Directory.GetFiles(temporaryDirectory, "*", SearchOption.AllDirectories)) {
        var relative = Path.GetRelativePath(temporaryDirectory, file).Replace('\\', '/');
        if (remove.Contains(relative) || remove.Contains(Path.GetFileName(relative)))
          File.Delete(file);
      }
    });
  }

  /// <summary>Rewrites live entries contiguously as a verified Pak v3 rebuild.</summary>
  public void Defragment(Stream archive) {
    EnsureRebuildable(archive);
    RebuildVerb.RebuildInPlace(archive, this, this);
  }

  private static void EnsureRebuildable(Stream archive) {
    var reader = Open(archive);
    var blocked = reader.Entries.FirstOrDefault(entry =>
      !entry.IsDeleted && (entry.IsEncrypted || entry.UnsupportedReason != null));
    if (blocked != null)
      throw new NotSupportedException(
        $"Pak rebuild requires every live entry to be decodable; '{blocked.Path}' is unsupported or encrypted.");
    foreach (var entry in reader.Entries.Where(entry => !entry.IsDeleted))
      reader.VerifyEntry(entry);
    if (archive.CanSeek)
      archive.Position = 0;
  }

  private static string MethodLabel(UnrealPakReader.UnrealPakEntry entry) => entry.CompressionMethod switch {
    UnrealPakReader.CompressionNone => "Stored",
    UnrealPakReader.CompressionZlib => "Zlib",
    _ => $"Unknown(0x{entry.CompressionMethod:X8})",
  };

  private static string CombinePath(string mount, string path) {
    var normalizedMount = mount.Replace('\\', '/');
    while (normalizedMount.StartsWith("../", StringComparison.Ordinal))
      normalizedMount = normalizedMount[3..];
    normalizedMount = normalizedMount.Trim('/');
    var normalizedPath = path.Replace('\\', '/').TrimStart('/');
    return normalizedMount.Length == 0 ? normalizedPath : normalizedMount + "/" + normalizedPath;
  }

  private static string NormalizeRebuildPath(string path) {
    var normalized = path.Replace('\\', '/').TrimStart('/');
    if (normalized.Length == 0 || normalized.EndsWith('/'))
      throw new ArgumentException("Pak path must name a file.", nameof(path));
    foreach (var part in normalized.Split('/'))
      if (part.Length == 0 || part is "." or ".." || part.IndexOf('\0') >= 0)
        throw new ArgumentException("Unsafe Pak path.", nameof(path));
    return normalized;
  }

  private static UnrealPakReader Open(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    if (stream.CanSeek) {
      stream.Position = 0;
      return new UnrealPakReader(stream);
    }
    var copy = new MemoryStream();
    stream.CopyTo(copy);
    copy.Position = 0;
    return new UnrealPakReader(copy);
  }

  private static Stream EmptyBoundedStream()
    => new Compression.Registry.Streaming.BoundedEntryStream(
      new MemoryStream([], writable: false), 0, leaveOpen: false);
}
