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
/// offset 0, the same GUID, <c>part_number</c>/<c>total_parts</c> in the header and the
/// spanned flag. Each part carries its own lookup table listing only the resources
/// stored in it, plus a copy of the XML data; the image metadata lives in part 1, and
/// no resource is cut across parts (checked against <c>wimlib-imagex split</c>). The
/// naming convention is
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
  /// <remarks>
  /// Names and sizes come from the image metadata in part 1, so listing needs neither the sibling
  /// parts nor any decompression. A later part on its own lists as a single <c>metadata.ini</c>
  /// naming the set it belongs to.
  /// </remarks>
  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    ArgumentNullException.ThrowIfNull(stream);
    if (stream.CanSeek) stream.Position = 0;
    var header = WimHeader.Read(stream);
    if (header.PartNumber != 1) {
      var info = PartInfo(header);
      return [new ArchiveEntryInfo(0, "metadata.ini", info.Length, info.Length, "Store", false, false, null)];
    }
    // Sizes live in each part's lookup table, so the siblings are consulted when present; a file
    // whose part is missing lists with an unknown (-1) size instead of failing the listing.
    var siblings = OpenSiblingParts(stream, header, requireAll: false);
    try {
      stream.Position = 0;
      using var reader = new WimReader(stream, siblings);
      var complete = siblings.Count + 1 == header.TotalParts;
      var method = header.CompressionType == WimConstants.CompressionNone ? "Store" : "Compressed";
      return reader.GetNamedFiles().Select((file, i) => new ArchiveEntryInfo(
        Index: i,
        Name: file.FileName,
        OriginalSize: file.ResourceIndex < 0 && !complete ? -1 : file.FileSize,
        CompressedSize: file.ResourceIndex < 0 ? (complete ? 0 : -1) : reader.Resources[file.ResourceIndex].CompressedSize,
        Method: method,
        IsDirectory: false,
        IsEncrypted: false,
        LastModified: null)).ToList();
    } finally {
      foreach (var part in siblings) part.Dispose();
    }
  }

  private static byte[] PartInfo(WimHeader header) {
    var sb = new System.Text.StringBuilder();
    sb.Append("[swm]\n");
    sb.Append(CultureInfo.InvariantCulture, $"part_number = {header.PartNumber}\n");
    sb.Append(CultureInfo.InvariantCulture, $"total_parts = {header.TotalParts}\n");
    sb.Append(CultureInfo.InvariantCulture, $"guid = {header.Guid}\n");
    sb.Append("note = open part 1 of the set to list or extract its files\n");
    return System.Text.Encoding.UTF8.GetBytes(sb.ToString());
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
      return [("metadata.ini", PartInfo(header), "Store")];

    stream.Position = 0;
    var ownedParts = OpenSiblingParts(stream, header, requireAll: true);
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

  private static IReadOnlyList<Stream> OpenSiblingParts(Stream stream, WimHeader header, bool requireAll) {
    if (header.TotalParts <= 1)
      return [];
    if (stream is not FileStream file || string.IsNullOrEmpty(file.Name))
      return requireAll
        ? throw new InvalidDataException($"This split WIM has {header.TotalParts} parts; the others are found beside part 1 on disk, and this stream is not a file.")
        : [];

    var currentPath = Path.GetFullPath(file.Name);
    var directory = Path.GetDirectoryName(currentPath)!;
    var fileName = Path.GetFileName(currentPath);
    string baseStem;
    var extension = Path.GetExtension(fileName);
    var extensionNumbered = extension.StartsWith(".swm", StringComparison.OrdinalIgnoreCase)
        && extension.Length > 4
        && int.TryParse(extension.AsSpan(4), NumberStyles.None, CultureInfo.InvariantCulture, out _);
    // Siblings take the first part's extension casing: IMAGE.SWM pairs with IMAGE2.SWM, which
    // matters on case-sensitive file systems.
    var swm = extension[..Math.Min(4, extension.Length)];
    if (extensionNumbered) {
      baseStem = fileName[..^extension.Length];
    } else {
      baseStem = Path.GetFileNameWithoutExtension(fileName);
      if (header.PartNumber > 1)
        while (baseStem.Length > 0 && char.IsDigit(baseStem[^1]))
          baseStem = baseStem[..^1];
    }

    var paths = new string[header.TotalParts];
    paths[0] = currentPath;
    for (var part = 2; part <= header.TotalParts; ++part) {
      var conventional = Path.Combine(directory, baseStem + part.ToString(CultureInfo.InvariantCulture) + swm);
      var extended = Path.Combine(directory, baseStem + swm + part.ToString(CultureInfo.InvariantCulture));
      paths[part - 1] = extensionNumbered
        ? File.Exists(extended) ? extended : conventional
        : File.Exists(conventional) ? conventional : extended;
    }

    var streams = new List<Stream>(header.TotalParts - 1);
    try {
      for (var part = 2; part <= header.TotalParts; ++part) {
        var path = paths[part - 1];
        if (!File.Exists(path)) {
          if (!requireAll) continue;
          throw new InvalidDataException($"Split WIM part {part} is missing: {Path.GetFileName(path)}.");
        }
        var sibling = File.OpenRead(path);
        var siblingHeader = WimHeader.Read(sibling);
        if (siblingHeader.PartNumber != part || siblingHeader.TotalParts != header.TotalParts
            || siblingHeader.Guid != header.Guid) {
          sibling.Dispose();
          if (!requireAll) continue;
          throw new InvalidDataException($"File {Path.GetFileName(path)} is not part {part} of this split WIM.");
        }
        streams.Add(sibling);
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
