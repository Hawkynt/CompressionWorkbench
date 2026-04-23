using System.Globalization;
using System.Text;
using Compression.Registry;
using FileFormat.Wim;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Swm;

/// <summary>
/// Descriptor for a <b>Split WIM</b> (.swm / .swmN) volume — a WIM file that has been
/// chopped into N pieces for size-limited media (DVD, FAT32, etc.).
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
/// <para>
/// Reference: same WIM spec —
/// <see href="https://learn.microsoft.com/en-us/windows-hardware/manufacture/desktop/wim-and-esd-windows-image-files-overview"/>.
/// </para>
/// </remarks>
public sealed class SwmFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {
  /// <inheritdoc/>
  public string Id => "Swm";

  /// <inheritdoc/>
  public string DisplayName => "Split WIM";

  /// <inheritdoc/>
  public FormatCategory Category => FormatCategory.Archive;

  /// <inheritdoc/>
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract |
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
  public IReadOnlyList<MagicSignature> MagicSignatures => [];

  /// <inheritdoc/>
  public IReadOnlyList<FormatMethodInfo> Methods => [new("wim", "WIM (split)")];

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
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    foreach (var e in BuildEntries(stream)) {
      if (files != null && files.Length > 0 && !MatchesFilter(e.Name, files))
        continue;
      WriteFile(outputDir, e.Name, e.Data);
    }
  }

  /// <summary>
  /// Materialises the entries this descriptor exposes for one SWM volume:
  /// a <c>metadata.ini</c> with the volume-number / sibling-discovery summary
  /// plus whichever resources can be decoded from this single piece. Resources
  /// whose bodies live in another volume cannot be assembled here.
  /// </summary>
  private static List<(string Name, byte[] Data, string Method)> BuildEntries(Stream stream) {
    WimHeader header;
    var resources = new List<WimResourceEntry>();
    var localResources = new List<(int Index, byte[] Data)>();
    var spilled = 0;

    var reader = new WimReader(stream);
    header = reader.Header;
    resources = [.. reader.Resources];

    if (header.TotalParts <= 1) {
      // Not actually split — fall back to plain WIM behaviour but emit metadata so
      // callers see the misnamed-extension case.
      for (var i = 0; i < resources.Count; ++i) {
        if (resources[i].IsMetadata)
          continue;
        try {
          localResources.Add((i, reader.ReadResource(i)));
        } catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or EndOfStreamException) {
          // skip — likely a malformed entry
          _ = ex;
        }
      }
    } else {
      for (var i = 0; i < resources.Count; ++i) {
        if (resources[i].IsMetadata)
          continue;
        try {
          localResources.Add((i, reader.ReadResource(i)));
        } catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException) {
          // Resource body lives in a sibling volume.
          ++spilled;
          _ = ex;
        }
      }
    }

    var siblings = TryDiscoverSiblings(stream, header.TotalParts, header.PartNumber);

    var result = new List<(string, byte[], string)> {
      ("metadata.ini", BuildMetadata(header, resources.Count, localResources.Count, spilled, siblings), "Tag"),
    };

    foreach (var (idx, data) in localResources)
      result.Add(($"resource_{idx:D4}.bin", data, "Payload"));

    return result;
  }

  /// <summary>
  /// Best-effort sibling discovery: if the stream is backed by a <see cref="FileStream"/>
  /// we walk the surrounding directory looking for files matching either of the SWM
  /// naming conventions (<c>name.swm</c> + <c>nameN.swm</c> or <c>name.swm</c> +
  /// <c>name.swmN</c>). Returns an empty list if the stream isn't a file or no siblings
  /// can be found.
  /// </summary>
  private static IReadOnlyList<string> TryDiscoverSiblings(Stream stream, ushort totalParts, ushort partNumber) {
    if (stream is not FileStream fs || string.IsNullOrEmpty(fs.Name) || totalParts <= 1)
      return [];

    var path = fs.Name;
    var dir = Path.GetDirectoryName(path);
    if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
      return [];

    var ext = Path.GetExtension(path);
    var stem = Path.GetFileNameWithoutExtension(path);
    if (string.IsNullOrEmpty(ext))
      return [];

    var found = new List<string>();
    var thisName = Path.GetFileName(path);

    // Pattern A: name.swm + name2.swm + name3.swm + ...
    // Strip a possible trailing digit from the stem to recover the base name.
    var baseStem = stem;
    if (partNumber > 1)
      while (baseStem.Length > 0 && char.IsDigit(baseStem[^1]))
        baseStem = baseStem[..^1];
    for (var p = 1; p <= totalParts; ++p) {
      var candidate = p == 1
        ? Path.Combine(dir, baseStem + ".swm")
        : Path.Combine(dir, baseStem + p.ToString(CultureInfo.InvariantCulture) + ".swm");
      if (File.Exists(candidate) && !candidate.Equals(path, StringComparison.OrdinalIgnoreCase)
          && !Path.GetFileName(candidate).Equals(thisName, StringComparison.OrdinalIgnoreCase))
        found.Add(Path.GetFileName(candidate));
    }

    // Pattern B: name.swm + name.swm2 + name.swm3 + ...
    for (var p = 2; p <= totalParts; ++p) {
      var candidate = Path.Combine(dir, stem + ".swm" + p.ToString(CultureInfo.InvariantCulture));
      if (File.Exists(candidate) && !candidate.Equals(path, StringComparison.OrdinalIgnoreCase))
        found.Add(Path.GetFileName(candidate));
    }

    return found;
  }

  private static byte[] BuildMetadata(
    WimHeader header,
    int totalResources,
    int decoded,
    int spilled,
    IReadOnlyList<string> siblings) {

    var sb = new StringBuilder();
    sb.AppendLine("[swm]");
    sb.Append("part_number = ").Append(header.PartNumber.ToString(CultureInfo.InvariantCulture)).AppendLine();
    sb.Append("total_parts = ").Append(header.TotalParts.ToString(CultureInfo.InvariantCulture)).AppendLine();
    sb.Append("compression_type = ").Append(CompressionName(header.CompressionType)).AppendLine();
    sb.Append("resource_count_in_table = ").Append(totalResources.ToString(CultureInfo.InvariantCulture)).AppendLine();
    sb.Append("decoded_in_this_volume = ").Append(decoded.ToString(CultureInfo.InvariantCulture)).AppendLine();
    sb.Append("spilled_to_siblings = ").Append(spilled.ToString(CultureInfo.InvariantCulture)).AppendLine();
    sb.Append("note = full extraction requires all ")
      .Append(header.TotalParts.ToString(CultureInfo.InvariantCulture))
      .AppendLine(" sibling files");
    if (siblings.Count > 0) {
      sb.AppendLine();
      sb.AppendLine("[siblings_discovered]");
      for (var i = 0; i < siblings.Count; ++i) {
        sb.Append("file_")
          .Append(i.ToString(CultureInfo.InvariantCulture))
          .Append(" = ")
          .AppendLine(siblings[i]);
      }
    }
    return Encoding.UTF8.GetBytes(sb.ToString());
  }

  private static string CompressionName(uint type) => type switch {
    WimConstants.CompressionNone => "none",
    WimConstants.CompressionXpress => "xpress",
    WimConstants.CompressionLzx => "lzx",
    WimConstants.CompressionLzms => "lzms",
    WimConstants.CompressionXpressHuffman => "xpress-huffman",
    _ => $"unknown(0x{type:X8})",
  };
}
