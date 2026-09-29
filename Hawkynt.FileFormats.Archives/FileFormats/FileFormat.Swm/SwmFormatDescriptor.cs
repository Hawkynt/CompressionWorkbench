using System.Globalization;
using Compression.Registry;
using FileFormat.Wim;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Swm;

/// <summary>
/// Descriptor for a <b>Split WIM</b> (.swm / .swmN) volume — a WIM file that has been
/// chopped into N pieces for size-limited media (DVD, FAT32, etc.).
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://learn.microsoft.com/en-us/windows-hardware/manufacture/desktop/wim-and-esd-windows-image-files-overview</c> — Microsoft's WIM/ESD overview (DISM <c>/Split-Image</c> produces .swm sets)</description></item>
///   <item><description>Microsoft "Windows Imaging File Format (WIM)" whitepaper — defines <c>part_number</c>/<c>total_parts</c> in the shared header</description></item>
///   <item><description><c>https://wimlib.net</c> — open-source implementation with full split-WIM support</description></item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// On disk every volume is a self-describing WIM: same <c>"MSWIM\0\0\0"</c> magic at
/// offset 0, but the header carries non-default values for <c>part_number</c> and
/// <c>total_parts</c>. The first volume holds the resource lookup table; subsequent
/// volumes hold the spilled resource bodies. The naming convention is
/// <c>name.swm</c>, <c>name2.swm</c>, <c>name3.swm</c>, … (or, less commonly,
/// <c>name.swm</c>, <c>name.swm2</c>, <c>name.swm3</c>, …).
/// </para>
/// <para>
/// Detection is extension-only because the underlying magic is shared with WIM and
/// ESD — declaring a magic signature here would shadow the regular WIM descriptor.
/// </para>
/// </remarks>
public sealed class SwmFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveLayoutMap {

  /// <inheritdoc />
  public IEnumerable<DefragBlockInfo> EnumerateLayout(Stream archive) {
    if (archive.Length < WimConstants.HeaderSize)
      yield break;
    yield return new DefragBlockInfo(0, WimConstants.HeaderSize, DefragBlockKind.MetadataReserved, FileName: "SWM/WIM Header");
    WimReader r;
    try {
      archive.Position = 0;
      r = new WimReader(archive);
    } catch {
      yield break;
    }
    foreach (var res in r.Resources) {
      if (res.PartNumber != 1) continue;
      if (res.CompressedSize <= 0 || res.Offset < 0) continue;
      var kind = res.IsMetadata ? DefragBlockKind.MetadataReserved : DefragBlockKind.Used;
      var label = res.IsMetadata ? "Metadata Resource" : "Data Resource";
      yield return new DefragBlockInfo(res.Offset, res.CompressedSize, kind, FileName: label);
    }
  }

  /// <inheritdoc/>
  public string Id => "Swm";

  /// <inheritdoc/>
  public string DisplayName => "Split WIM";

  /// <inheritdoc/>
  public FormatCategory Category => FormatCategory.Archive;

  /// <inheritdoc/>
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;

  /// <inheritdoc/>
  public string DefaultExtension => ".swm";

  /// <inheritdoc/>
  public IReadOnlyList<string> Extensions =>
    [".swm", ".swm2", ".swm3", ".swm4", ".swm5", ".swm6", ".swm7", ".swm8", ".swm9"];

  /// <inheritdoc/>
  public IReadOnlyList<string> CompoundExtensions => [];

  /// <inheritdoc/>
  /// <remarks>
  /// Empty: all SWM volumes share the WIM <c>"MSWIM\0\0\0"</c> magic. Detection
  /// is extension-only to avoid shadowing the WIM descriptor.
  /// </remarks>
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [];

  /// <inheritdoc/>
  public IReadOnlyList<FormatMethodInfo> Methods => [
    new("xpress", "XPRESS"),
    new("xpress-huffman", "XPRESS Huffman"),
    new("lzx", "LZX"),
    new("lzms", "LZMS"),
    new("none", "Uncompressed"),
  ];

  /// <inheritdoc/>
  public string? TarCompressionFormatId => null;

  /// <inheritdoc/>
  public AlgorithmFamily Family => AlgorithmFamily.Archive;

  /// <inheritdoc/>
  public string Description =>
    "Split Windows Imaging Format — WIM volume of an N-part .swm/.swmN set.";

  /// <inheritdoc/>
  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var entries = BuildEntries(stream);
    return entries.Select((e, i) => new ArchiveEntryInfo(
      Index: i,
      Name: e.Name,
      OriginalSize: e.Data.Length,
      CompressedSize: e.Data.Length,
      Method: e.Method,
      IsDirectory: false,
      IsEncrypted: false,
      LastModified: null)).ToList();
  }

  /// <inheritdoc/>
  /// <summary>
  /// Opens a single entry as a bounded read-only stream. Each entry's
  /// decoded byte buffer is produced by <see cref="BuildEntries"/> and
  /// wrapped in a
  /// <see cref="Compression.Registry.Streaming.BoundedEntryStream"/> sized
  /// to its logical length.
  /// </summary>
  public Stream OpenEntry(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryName);
    if (archive.CanSeek) archive.Position = 0;
    foreach (var e in BuildEntries(archive)) {
      if (!string.Equals(e.Name, entryName, StringComparison.OrdinalIgnoreCase)) continue;
      return new Compression.Registry.Streaming.BoundedEntryStream(
        new MemoryStream(e.Data, writable: false), e.Data.Length, leaveOpen: false);
    }
    return new Compression.Registry.Streaming.BoundedEntryStream(
      new MemoryStream(System.Array.Empty<byte>(), writable: false), 0, leaveOpen: false);
  }

  /// <summary>Native in-memory single-entry extraction routed through the bounded <see cref="OpenEntry"/>.</summary>
  public byte[] ExtractEntryToMemory(Stream archive, string entryName, string? password) {
    using var s = this.OpenEntry(archive, entryName, password);
    using var memoryStream = new MemoryStream();
    s.CopyTo(memoryStream);
    return memoryStream.ToArray();
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    foreach (var e in BuildEntries(stream)) {
      if (files != null && files.Length > 0 && !MatchesFilter(e.Name, files))
        continue;
      WriteFile(outputDir, e.Name, e.Data);
    }
  }

  /// <summary>
  /// Materialises the named image files from the first SWM part and its siblings.
  /// </summary>
  private static List<(string Name, byte[] Data, string Method)> BuildEntries(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    if (stream.CanSeek) stream.Position = 0;
    var header = WimHeader.Read(stream);
    if (header.TotalParts == 0 || header.PartNumber == 0 || header.PartNumber > header.TotalParts)
      throw new InvalidDataException("The WIM split-part numbering is invalid.");
    if (header.PartNumber != 1)
      throw new InvalidDataException("A split WIM must be opened from its first part.");

    var ownedParts = OpenSiblingParts(stream, header);
    try {
      using var reader = new WimReader(stream, ownedParts);
      var named = reader.GetNamedFiles();
      if (named.Count > 0)
        return named.Select(file => (
          file.FileName,
          file.ResourceIndex < 0 ? [] : reader.ReadResource(file.ResourceIndex),
          header.CompressionType == WimConstants.CompressionNone ? "Store" : "Compressed"
        )).ToList();

      return reader.Resources
        .Select((resource, index) => (resource, index))
        .Where(item => !item.resource.IsMetadata)
        .Select(item => ($"resource_{item.index}", reader.ReadResource(item.index),
          item.resource.IsCompressed ? "Compressed" : "Store"))
        .ToList();
    } finally {
      foreach (var part in ownedParts)
        part.Dispose();
    }
  }

  private static IReadOnlyList<Stream> OpenSiblingParts(Stream stream, WimHeader header) {
    if (header.TotalParts <= 1)
      return [];
    if (stream is not FileStream file || string.IsNullOrEmpty(file.Name))
      return [];

    var currentPath = Path.GetFullPath(file.Name);
    var directory = Path.GetDirectoryName(currentPath)!;
    var fileName = Path.GetFileName(currentPath);
    string baseStem;
    var extension = Path.GetExtension(fileName);
    var extensionNumbered = extension.StartsWith(".swm", StringComparison.OrdinalIgnoreCase)
        && extension.Length > 4
        && int.TryParse(extension.AsSpan(4), NumberStyles.None, CultureInfo.InvariantCulture, out _);
    if (extensionNumbered) {
      baseStem = fileName[..^extension.Length];
    } else {
      baseStem = Path.GetFileNameWithoutExtension(fileName);
      if (header.PartNumber > 1)
        while (baseStem.Length > 0 && char.IsDigit(baseStem[^1]))
          baseStem = baseStem[..^1];
    }

    var paths = new string[header.TotalParts];
    paths[0] = Path.Combine(directory, baseStem + ".swm");
    for (var part = 2; part <= header.TotalParts; ++part) {
      var conventional = Path.Combine(directory, baseStem + part.ToString(CultureInfo.InvariantCulture) + ".swm");
      var extended = Path.Combine(directory, baseStem + ".swm" + part.ToString(CultureInfo.InvariantCulture));
      paths[part - 1] = extensionNumbered
        ? File.Exists(extended) ? extended : conventional
        : File.Exists(conventional) ? conventional : extended;
    }

    var streams = new List<Stream>(header.TotalParts - 1);
    try {
      for (var part = 2; part <= header.TotalParts; ++part) {
        var path = paths[part - 1];
        if (!File.Exists(path))
          throw new InvalidDataException($"Split WIM part {part} is missing: {Path.GetFileName(path)}.");
        var sibling = File.OpenRead(path);
        streams.Add(sibling);
        var siblingHeader = WimHeader.Read(sibling);
        if (siblingHeader.PartNumber != part || siblingHeader.TotalParts != header.TotalParts
            || siblingHeader.Guid != header.Guid)
          throw new InvalidDataException($"File {Path.GetFileName(path)} is not part {part} of this split WIM.");
      }
      return streams;
    } catch {
      foreach (var part in streams)
        part.Dispose();
      throw;
    }
  }

  private static uint CompressionType(FormatCreateOptions options) => options.MethodName?.ToLowerInvariant() switch {
    null or "xpress" or "wim" => WimConstants.CompressionXpress,
    "xpress-huffman" or "xpresshuffman" => WimConstants.CompressionXpressHuffman,
    "lzx" => WimConstants.CompressionLzx,
    "lzms" => WimConstants.CompressionLzms,
    "none" or "store" => WimConstants.CompressionNone,
    var method => throw new NotSupportedException($"Unsupported Split WIM compression method: {method}.")
  };

  /// <inheritdoc />
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    ArgumentNullException.ThrowIfNull(options);
    var chunkSize = options.GetOptionInt("chunk-size", WimConstants.DefaultChunkSize);
    new WimWriter(output, CompressionType(options), chunkSize)
      .Write(FormatHelpers.FilesOnly(inputs).ToList());
  }

  /// <summary>Creates a genuine multi-part SWM set; resources remain whole across volume boundaries.</summary>
  public static byte[][] CreateSplit(
      long maxVolumeSize,
      IReadOnlyList<ArchiveInputInfo> inputs,
      FormatCreateOptions? options = null) {
    ArgumentNullException.ThrowIfNull(inputs);
    options ??= new FormatCreateOptions();
    var chunkSize = options.GetOptionInt("chunk-size", WimConstants.DefaultChunkSize);
    return WimWriter.CreateSplit(maxVolumeSize, FormatHelpers.FilesOnly(inputs).ToList(),
      CompressionType(options), chunkSize);
  }

}
