#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.AppleSparse;

/// <summary>
/// Apple <c>sparsebundle</c> — a directory-based expanding disk image used by
/// Time Machine, FileVault and <c>hdiutil create -type SPARSEBUNDLE</c>. The
/// bundle is a directory containing <c>Info.plist</c>, <c>Info.bckup</c>,
/// <c>token</c> and a <c>bands/</c> directory whose hex-named files each
/// hold one virtual band (default 8 MB).
///
/// References:
/// <list type="bullet">
///   <item><description>Apple <c>hdiutil(1)</c> man page — the creating tool; the bundle layout itself is undocumented by Apple</description></item>
///   <item><description><c>https://github.com/torarnv/sparsebundlefs</c> — sparsebundlefs — open-source FUSE implementation of the band layout</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/Sparse_image</c> — background on Apple sparse images/bundles</description></item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// Sparsebundle is a <em>directory</em> format and so doesn't fit cleanly into
/// the stream-based archive surface. The descriptor handles this by:
/// </para>
/// <list type="bullet">
///   <item><description>
///     If the input <see cref="Stream"/> is a <see cref="FileStream"/> over
///     <c>Info.plist</c>, we resolve the sibling bundle directory and walk
///     <c>bands/</c> from disk.
///   </description></item>
///   <item><description>
///     Otherwise we parse the supplied stream as an <c>Info.plist</c> document
///     and surface bundle metadata + a single virtual <c>disk.img</c> entry,
///     filled with whichever bands we can resolve relative to the bundle root
///     (zero bytes when bands are absent).
///   </description></item>
/// </list>
/// <para>
/// Read-only descriptor. No WORM <c>Create</c> support — synthesising a
/// sparsebundle requires a directory output target which isn't part of the
/// stream-based archive contract.
/// </para>
/// </remarks>
public sealed class SparsebundleFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {

  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "Sparsebundle";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "Apple Sparsebundle";
  /// <summary>
  /// Gets the category.
  /// </summary>
  public FormatCategory Category => FormatCategory.Archive;
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  /// <summary>
  /// Gets the default extension.
  /// </summary>
  public string DefaultExtension => ".sparsebundle";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".sparsebundle"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  // Sparsebundle Info.plist is generic Apple XML plist; no usable file-level
  // magic that doesn't collide with every other plist on the system. Detection
  // is via the .sparsebundle extension on a directory or path.
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  /// <summary>
  /// Gets the methods.
  /// </summary>
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
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
    "Apple sparsebundle (Time Machine / hdiutil bundle disk image). " +
    "R-only by design: sparsebundle is a directory layout " +
    "(Info.plist + Info.bckup + token + bands/<hex>) and the IArchiveModifiable " +
    "surface operates over a single seekable Stream — there is no on-disk byte " +
    "range to patch in place. Promotion to in-place R/W would require either " +
    "(a) a directory-target sibling interface (IDirectoryArchiveModifiable) so " +
    "the modifier can mutate individual band files alongside Info.plist, or " +
    "(b) a virtual TAR-of-directory façade that the modifier rewrites back to " +
    "disk on flush. The companion single-file Sparseimage descriptor is already " +
    "R/W via band rewrite at fixed offsets — callers needing in-place R/W on " +
    "Apple sparse imagery should use that.";

  // ── IArchiveFormatOperations ──────────────────────────────────────

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    ArgumentNullException.ThrowIfNull(stream);
    var reader = TryOpenReader(stream);
    if (reader == null) {
      // Detection-only fallback: parse stream as plist and surface metadata
      return ListFromPlistStream(stream);
    }

    // Try inner-FS delegation against the virtual disk view
    var vStream = new SparsebundleStream(reader);
    var inner = InnerFsDetector.Detect(vStream);
    if (inner is IArchiveFormatOperations ops) {
      try {
        vStream.Position = 0;
        return ops.List(vStream, password);
      } catch {
        // fall through to raw listing
      }
    }

    return [
      new ArchiveEntryInfo(0, "Info.plist", File.Exists(Path.Combine(reader.BundleRoot, "Info.plist"))
        ? new FileInfo(Path.Combine(reader.BundleRoot, "Info.plist")).Length : 0,
        0, "Stored", false, false, null, Kind: "Metadata"),
      new ArchiveEntryInfo(1, "disk.img", reader.VirtualSize, reader.VirtualSize, "Stored", false, false, null),
    ];
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    ArgumentNullException.ThrowIfNull(stream);
    ArgumentNullException.ThrowIfNull(outputDir);

    var reader = TryOpenReader(stream);
    if (reader == null) {
      // Detection-only fallback: copy the plist itself
      if (files == null || MatchesFilter("Info.plist", files)) {
        if (stream.CanSeek) stream.Position = 0;
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        WriteFile(outputDir, "Info.plist", ms.ToArray());
      }
      return;
    }

    // Try inner-FS delegation
    var vStream = new SparsebundleStream(reader);
    var inner = InnerFsDetector.Detect(vStream);
    if (inner is IArchiveFormatOperations ops) {
      try {
        vStream.Position = 0;
        ops.Extract(vStream, outputDir, password, files);
        return;
      } catch {
        // fall through to raw extraction
      }
    }

    var infoPath = Path.Combine(reader.BundleRoot, "Info.plist");
    if ((files == null || MatchesFilter("Info.plist", files)) && File.Exists(infoPath))
      WriteFile(outputDir, "Info.plist", File.ReadAllBytes(infoPath));
    if (files == null || MatchesFilter("disk.img", files))
      WriteFile(outputDir, "disk.img", reader.ExtractDisk());
  }

  // ── Private helpers ────────────────────────────────────────────────

  /// <summary>
  /// Tries to derive a <see cref="SparsebundleReader"/> from the input stream:
  /// only succeeds when the stream is a <see cref="FileStream"/> we can map
  /// back to a bundle directory on disk.
  /// </summary>
  private static SparsebundleReader? TryOpenReader(Stream stream) {
    if (stream is not FileStream fs) return null;
    try {
      return SparsebundleReader.TryFromPath(fs.Name);
    } catch {
      return null;
    }
  }

  /// <summary>
  /// Fallback for non-file streams: parse the input as an
  /// <c>Info.plist</c> document and report what we can deduce.
  /// </summary>
  private static List<ArchiveEntryInfo> ListFromPlistStream(Stream stream) {
    try {
      if (stream.CanSeek) stream.Position = 0;
      using var ms = new MemoryStream();
      stream.CopyTo(ms);
      var dict = InfoPlistParser.ParseTopLevelDict(ms.ToArray());
      if (dict.Count == 0) return [];
      var virtualSize = InfoPlistParser.GetInt64(dict, "size", defaultValue: 0);
      return [
        new ArchiveEntryInfo(0, "Info.plist", ms.Length, ms.Length, "Stored", false, false, null, Kind: "Metadata"),
        new ArchiveEntryInfo(1, "disk.img", virtualSize, virtualSize, "Stored", false, false, null),
      ];
    } catch {
      return [];
    }
  }
}
