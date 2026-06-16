#pragma warning disable CS1591
using Compression.Lib;
using Compression.Registry;

namespace Compression.Tests.ConversionMatrix;

/// <summary>
/// Shared scaffolding for the conversion capability + round-trip matrix.
/// <para>
/// The matrix proves the "convert anything to anything" surface by enumerating
/// the registry for conversion SOURCES (formats that can be listed, extracted,
/// and — so the harness can synthesize a fixture — created) crossed against
/// every TARGET (formats implementing <see cref="IArchiveCreatable"/>). Each
/// pair is exercised end-to-end through the public
/// <see cref="ArchiveOperations.ConvertArchive(string,string,string?)"/> entry
/// point: synthesize a source archive/image, convert it, then re-list and
/// re-extract the output and verify the payload survives.
/// </para>
/// </summary>
public static class ConversionMatrixSupport {

  /// <summary>
  /// Representative, diverse SOURCE format IDs. Deliberately scoped (≈8) so the
  /// grid runs in reasonable time while still spanning archive containers,
  /// modern filesystems, a streaming-archive (TAR), and a retro disk image.
  /// Each must be both listable/extractable AND creatable so the harness can
  /// build the fixture without any binary asset on disk.
  /// </summary>
  public static readonly string[] SourceFormatIds = [
    "Zip",      // DEFLATE archive container, the canonical multi-file archive
    "Tar",      // streaming archive (writer already streams; no per-entry seek)
    "SevenZip", // solid-block archive container
    "Cpio",     // classic Unix archive container
    "Fat",      // FAT filesystem image (case-folding, 8.3 short names)
    "Ext",      // ext2/3/4 filesystem image (modern Unix FS)
    "Ntfs",     // NTFS filesystem image (LZNT1)
    "D64",      // Commodore 1541 retro disk image (synthesizes/uppercases names)
  ];

  /// <summary>
  /// Each representative payload file: archive-relative name + known bytes.
  /// Names are chosen to survive case-folding filesystems (uppercase, ≤8.3) so
  /// the same fixture can target FAT/D64 without name-shape failures; content
  /// verification is by case-insensitive basename match.
  /// </summary>
  public static IReadOnlyList<(string Name, byte[] Data)> BuildPayloadFiles() {
    var larger = new byte[4096];
    for (var i = 0; i < larger.Length; ++i) larger[i] = (byte)((i * 31 + 7) & 0xFF);
    return [
      ("HELLO.TXT", "hello conversion matrix"u8.ToArray()),
      ("DATA.BIN",  Enumerable.Range(0, 256).Select(i => (byte)i).ToArray()),
      ("BIG.DAT",   larger),
    ];
  }

  /// <summary>Subdirectory entry name used when both ends support directories.</summary>
  public const string SubdirName = "SUB";

  /// <summary>File placed inside <see cref="SubdirName"/> for directory-tree checks.</summary>
  public const string SubdirFileName = "SUB/INNER.TXT";

  public static byte[] SubdirFileData => "nested payload"u8.ToArray();

  /// <summary>True when the descriptor advertises <see cref="FormatCapabilities.SupportsDirectories"/>.</summary>
  public static bool SupportsDirectories(IFormatDescriptor d)
    => (d.Capabilities & FormatCapabilities.SupportsDirectories) != 0;

  /// <summary>True when the descriptor advertises multiple-entry support.</summary>
  public static bool SupportsMultipleEntries(IFormatDescriptor d)
    => (d.Capabilities & FormatCapabilities.SupportsMultipleEntries) != 0;

  /// <summary>A format that can be a conversion source: listable, extractable, and creatable.</summary>
  public static bool CanBeSyntheticSource(IFormatDescriptor d) {
    var ops = FormatRegistry.GetArchiveOps(d.Id);
    if (ops == null) return false;
    var caps = d.Capabilities;
    var listExtract = (caps & FormatCapabilities.CanList) != 0 && (caps & FormatCapabilities.CanExtract) != 0;
    return listExtract && ops is IArchiveCreatable;
  }

  /// <summary>A format that can be a conversion target: it implements <see cref="IArchiveCreatable"/>.</summary>
  public static bool CanBeTarget(IFormatDescriptor d) {
    var ops = FormatRegistry.GetArchiveOps(d.Id);
    return ops is IArchiveCreatable;
  }

  /// <summary>All registry descriptors that qualify as conversion targets, sorted by ID.</summary>
  public static List<IFormatDescriptor> AllTargets() {
    FormatRegistration.EnsureInitialized();
    return FormatRegistry.All
      .Where(CanBeTarget)
      .GroupBy(d => d.Id, StringComparer.OrdinalIgnoreCase)
      .Select(g => g.First())
      .OrderBy(d => d.Id, StringComparer.OrdinalIgnoreCase)
      .ToList();
  }

  /// <summary>
  /// Whether the target descriptor overrides the streaming-safe
  /// <see cref="IArchiveCreatable.CreateFromStreams"/> hook. Targets that do NOT
  /// override it fall back to the buffer-the-whole-entry default, which is the
  /// large-file-safety gap the coverage report surfaces.
  /// </summary>
  public static bool OverridesCreateFromStreams(IFormatDescriptor d) {
    var ops = FormatRegistry.GetArchiveOps(d.Id);
    if (ops is not IArchiveCreatable) return false;
    var method = ops.GetType().GetMethod(
      nameof(IArchiveCreatable.CreateFromStreams),
      [typeof(Stream), typeof(IEnumerable<Compression.Registry.Streaming.StreamingArchiveInput>), typeof(FormatCreateOptions)]);
    // A class-declared override has its DeclaringType be the concrete ops type,
    // not the interface that carries the default virtual implementation.
    return method != null && method.DeclaringType != typeof(IArchiveCreatable);
  }

  /// <summary>
  /// Synthesizes a source archive/image of <paramref name="sourceId"/> at a
  /// fresh temp path and returns it. The fixture contains the representative
  /// payload files, plus a subdirectory when the source supports directories
  /// and more than one entry. Throws if creation is not possible — callers
  /// translate that into an Assert.Ignore for genuinely un-synthesizable pairs.
  /// </summary>
  public static string SynthesizeSource(string sourceId, string workDir) {
    FormatRegistration.EnsureInitialized();
    var desc = FormatRegistry.GetById(sourceId)
      ?? throw new InvalidOperationException($"No descriptor for source '{sourceId}'.");

    var ext = string.IsNullOrEmpty(desc.DefaultExtension) ? ".bin" : desc.DefaultExtension;
    var srcPath = Path.Combine(workDir, $"src_{sourceId}_{Guid.NewGuid():N}{ext}");

    // Stage payload files on disk so ArchiveInput can reference them.
    var inputs = new List<ArchiveInput>();
    var multi = SupportsMultipleEntries(desc);
    var payload = BuildPayloadFiles();
    var staged = multi ? payload : payload.Take(1).ToList();
    foreach (var (name, data) in staged) {
      var p = Path.Combine(workDir, $"stage_{Guid.NewGuid():N}_{Path.GetFileName(name)}");
      File.WriteAllBytes(p, data);
      inputs.Add(new ArchiveInput(p, name));
    }

    if (multi && SupportsDirectories(desc)) {
      inputs.Add(new ArchiveInput("", SubdirName + "/"));
      var innerPath = Path.Combine(workDir, $"stage_inner_{Guid.NewGuid():N}.txt");
      File.WriteAllBytes(innerPath, SubdirFileData);
      inputs.Add(new ArchiveInput(innerPath, SubdirFileName));
    }

    var fmt = Enum.Parse<FormatDetector.Format>(sourceId, ignoreCase: true);
    ArchiveOperations.Create(srcPath, inputs, new CompressionOptions { ForceCompress = true }, fmt);
    return srcPath;
  }
}
