#pragma warning disable CS1591
using Compression.Registry;
using FileFormat.Tar;
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
/// <para>Stream creation uses a TAR transport of the bundle members. Creating
/// from <c>disk.img</c> synthesizes the standard plist/token/bands members;
/// supplying bundle members instead preserves their contents verbatim.</para>
/// </remarks>
public sealed class SparsebundleFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IFormatOptionsSchema {

  /// <inheritdoc />
  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema => [
    new("BandSize", "Band size", FormatOptionKind.Integer, "8388608",
      Description: "Sparsebundle band size in bytes; Apple hdiutil defaults to 8 MiB."),
  ];

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
    FormatCapabilities.CanTest | FormatCapabilities.CanCreate | FormatCapabilities.SupportsMultipleEntries;
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
    "Apple sparsebundle (Time Machine / hdiutil bundle disk image). Stream creation uses a TAR transport containing the bundle directory entries.";

  // ── IArchiveFormatOperations ──────────────────────────────────────

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    ArgumentNullException.ThrowIfNull(stream);
    if (IsTarTransport(stream))
      return new TarFormatDescriptor().List(stream, password);
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

    if (IsTarTransport(stream)) {
      new TarFormatDescriptor().Extract(stream, outputDir, password, files);
      return;
    }

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

  /// <summary>Creates a TAR transport of a sparsebundle directory.</summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    ArgumentNullException.ThrowIfNull(options);

    var bandSize = options.GetOptionInt("BandSize", 8 * 1024 * 1024);
    if (bandSize <= 0 || bandSize > 1 << 30)
      throw new ArgumentOutOfRangeException(nameof(options), "BandSize must be between 1 byte and 1 GiB.");

    var disk = inputs.FirstOrDefault(i => !i.IsDirectory &&
      Path.GetFileName(i.ArchiveName).Equals("disk.img", StringComparison.OrdinalIgnoreCase));
    // Explicit bundle members take precedence when there is no virtual disk
    // entry. This preserves every supplied band and metadata byte verbatim.
    var hasBundleMembers = inputs.Any(i => !i.IsDirectory &&
      (i.ArchiveName.Equals("Info.plist", StringComparison.OrdinalIgnoreCase) ||
       i.ArchiveName.StartsWith("bands/", StringComparison.OrdinalIgnoreCase)));
    using var writer = new TarWriter(output, leaveOpen: true, format: TarHeaderFormat.Pax);
    if (hasBundleMembers && disk == null) {
      foreach (var input in inputs) {
        var name = NormalizeBundlePath(input.ArchiveName);
        if (input.IsDirectory) {
          writer.AddEntry(new TarEntry { Name = name.TrimEnd('/') + "/", TypeFlag = (byte)'5' });
          continue;
        }
        var bytes = input.ReadContent();
        writer.AddEntry(new TarEntry { Name = name, Size = bytes.Length }, bytes);
      }
      writer.Finish();
      return;
    }

    if (disk == null)
      disk = inputs.FirstOrDefault(i => !i.IsDirectory);
    var data = disk?.ReadContent() ?? [];
    var preservedNames = new HashSet<string>(StringComparer.Ordinal);
    foreach (var input in inputs) {
      if (input.IsDirectory) {
        var directoryName = NormalizeBundlePath(input.ArchiveName);
        writer.AddEntry(new TarEntry { Name = directoryName.TrimEnd('/') + "/", TypeFlag = (byte)'5' });
        continue;
      }
      var name = NormalizeBundlePath(input.ArchiveName);
      if (Path.GetFileName(name).Equals("disk.img", StringComparison.OrdinalIgnoreCase) ||
          name.StartsWith("bands/", StringComparison.OrdinalIgnoreCase))
        continue;
      var bytes = input.ReadContent();
      if (name.Equals("Info.plist", StringComparison.OrdinalIgnoreCase)) {
        var dict = InfoPlistParser.ParseTopLevelDict(bytes);
        if (!options.HasOption("BandSize")) {
          var existing = InfoPlistParser.GetInt64(dict, "band-size", bandSize);
          if (existing is > 0 and <= 1L << 30) bandSize = (int)existing;
        } else {
          bytes = UpdatePlistBandSize(bytes, bandSize);
        }
      } else if (name.Equals("Info.bckup", StringComparison.OrdinalIgnoreCase) && options.HasOption("BandSize")) {
        bytes = UpdatePlistBandSize(bytes, bandSize);
      }
      writer.AddEntry(new TarEntry { Name = name, Size = bytes.Length }, bytes);
      preservedNames.Add(name);
    }
    if (!preservedNames.Contains("Info.plist")) {
      var plist = BuildInfoPlist(data.LongLength, bandSize);
      writer.AddEntry(new TarEntry { Name = "Info.plist", Size = plist.Length }, plist);
    }
    if (!preservedNames.Contains("Info.bckup")) {
      var plist = BuildInfoPlist(data.LongLength, bandSize);
      writer.AddEntry(new TarEntry { Name = "Info.bckup", Size = plist.Length }, plist);
    }
    if (!preservedNames.Contains("token"))
      writer.AddEntry(new TarEntry { Name = "token", Size = 0 }, ReadOnlySpan<byte>.Empty);
    if (!inputs.Any(i => i.IsDirectory && NormalizeBundlePath(i.ArchiveName).TrimEnd('/').Equals("bands", StringComparison.Ordinal)))
      writer.AddEntry(new TarEntry { Name = "bands/", TypeFlag = (byte)'5' });
    for (var offset = 0L; offset < data.LongLength; offset += bandSize) {
      var length = (int)Math.Min(bandSize, data.LongLength - offset);
      var band = data.AsSpan((int)offset, length);
      if (IsAllZero(band)) continue;
      var name = "bands/" + (offset / bandSize).ToString("x", System.Globalization.CultureInfo.InvariantCulture);
      writer.AddEntry(new TarEntry { Name = name, Size = length }, band);
    }
    writer.Finish();
  }

  private static string NormalizeBundlePath(string path) {
    ArgumentException.ThrowIfNullOrWhiteSpace(path);
    var normalized = path.Replace('\\', '/').TrimStart('/');
    if (normalized.Split('/').Any(part => part is ".." or "."))
      throw new InvalidDataException($"Invalid sparsebundle member path: {path}");
    return normalized;
  }

  private static bool IsTarTransport(Stream stream) {
    if (!stream.CanSeek || stream.Length < 265) return false;
    var position = stream.Position;
    try {
      stream.Position = 257;
      Span<byte> magic = stackalloc byte[5];
      return stream.Read(magic) == magic.Length && magic.SequenceEqual("ustar"u8);
    } finally {
      stream.Position = position;
    }
  }

  private static byte[] BuildInfoPlist(long size, int bandSize) => System.Text.Encoding.UTF8.GetBytes($"""
    <?xml version="1.0" encoding="UTF-8"?>
    <plist version="1.0"><dict>
    <key>band-size</key><integer>{bandSize}</integer>
    <key>bundle-backingstore-version</key><integer>1</integer>
    <key>diskimage-bundle-type</key><string>com.apple.diskimage.sparsebundle</string>
    <key>size</key><integer>{size}</integer>
    </dict></plist>
    """);

  private static byte[] UpdatePlistBandSize(byte[] xml, int bandSize) {
    var settings = new System.Xml.XmlReaderSettings {
      DtdProcessing = System.Xml.DtdProcessing.Ignore,
      XmlResolver = null,
    };
    using var text = new StringReader(System.Text.Encoding.UTF8.GetString(xml));
    using var reader = System.Xml.XmlReader.Create(text, settings);
    var document = System.Xml.Linq.XDocument.Load(reader);
    var elements = document.Root?.Element("dict")?.Elements().ToList()
      ?? throw new InvalidDataException("Sparsebundle Info.plist has no top-level dictionary.");
    for (var i = 0; i + 1 < elements.Count; ++i) {
      if (elements[i].Name.LocalName != "key" || elements[i].Value != "band-size") continue;
      elements[i + 1].ReplaceWith(new System.Xml.Linq.XElement(elements[i + 1].Name, bandSize));
      return System.Text.Encoding.UTF8.GetBytes(document.ToString(System.Xml.Linq.SaveOptions.DisableFormatting));
    }
    throw new InvalidDataException("Sparsebundle Info.plist has no band-size key.");
  }

  private static bool IsAllZero(ReadOnlySpan<byte> data) {
    foreach (var value in data)
      if (value != 0) return false;
    return true;
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
