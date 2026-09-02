#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Hdf4;

/// <summary>
/// Read-only descriptor for the classic HDF4 container (NCSA HDF v4). Walks the
/// Data Descriptor (DD) linked list from the file prefix and emits one entry
/// per non-empty tag/ref pair, plus a <c>metadata.ini</c> with the detected
/// magic, total DD count and per-tag histogram.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://www.hdfgroup.org/solutions/hdf4/</c> — The HDF Group's HDF4 page (maintained but deprecated in favour of HDF5)</description></item>
///   <item><description>"HDF4 Specification and Developer's Guide" (NCSA / The HDF Group) — the defining document for the DD list and tag/ref model</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/Hierarchical_Data_Format</c> — format overview</description></item>
/// </list>
/// </summary>
/// <remarks>
/// Distinct from HDF5 (handled by <c>FileFormat.Hdf5</c>).
/// </remarks>
public sealed class Hdf4FormatDescriptor : IFormatDescriptor, IArchiveFormatOperations {

  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "Hdf4";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "HDF4";
  /// <summary>
  /// Gets the category.
  /// </summary>
  public FormatCategory Category => FormatCategory.Archive;
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries;
  /// <summary>
  /// Gets the default extension.
  /// </summary>
  public string DefaultExtension => ".hdf";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".hdf", ".hdf4", ".h4"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([0x0E, 0x03, 0x13, 0x01], Confidence: 0.95),
    new([(byte)'H', (byte)'D', (byte)'F', 0x00, 0x00, 0x00, 0x0E, 0x02], Confidence: 0.90),
  ];
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
    "Hierarchical Data Format v4 (NCSA HDF, predecessor of HDF5). Walks the DD linked list " +
    "and surfaces each tag/ref pair as a raw payload entry.";

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
  public List<ArchiveEntryInfo> List(Stream stream, string? password) =>
    this.BuildEntries(stream)
      .Select((e, i) => new ArchiveEntryInfo(i, e.Name, e.Data.LongLength, e.Data.LongLength,
        "stored", false, false, null, e.Kind))
      .ToList();

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    foreach (var e in this.BuildEntries(stream)) {
      if (files != null && files.Length > 0 && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, e.Data);
    }
  }

  private List<(string Name, byte[] Data, string Kind)> BuildEntries(Stream stream) {
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var total = (int)ms.Length;
    var data = ms.GetBuffer().AsSpan(0, total);

    var result = new List<(string, byte[], string)>();
    try {
      var hdf = Hdf4Reader.Read(data);
      result.Add(("metadata.ini", BuildMetadata(hdf, total), "Metadata"));
      foreach (var dd in hdf.DataDescriptors) {
        // Skip zero-length descriptors (free-space, linked-block sentinels, etc.).
        if (dd.Length == 0) continue;
        var end = (long)dd.Offset + dd.Length;
        if (dd.Offset >= total || end > total) continue;
        var slice = data.Slice((int)dd.Offset, (int)dd.Length).ToArray();
        var name = $"tag_{dd.Tag:D4}_ref_{dd.Reference:D4}.bin";
        var kind = Hdf4Reader.TagName(dd.Tag);
        result.Add((name, slice, kind));
      }
    } catch (Exception ex) {
      var sb = new StringBuilder();
      sb.Append("[hdf4]\r\n");
      sb.Append("parse_status=error\r\n");
      sb.Append(CultureInfo.InvariantCulture, $"file_size={total}\r\n");
      sb.Append("error=").Append(ex.Message).Append("\r\n");
      result.Add(("metadata.ini", Encoding.UTF8.GetBytes(sb.ToString()), "Metadata"));
    }
    return result;
  }

  private static byte[] BuildMetadata(Hdf4Reader.Hdf4File hdf, long fileSize) {
    var sb = new StringBuilder();
    sb.Append("[hdf4]\r\n");
    sb.Append("parse_status=ok\r\n");
    sb.Append(CultureInfo.InvariantCulture, $"file_size={fileSize}\r\n");
    sb.Append("magic=").Append(hdf.MagicKind).Append("\r\n");
    sb.Append(CultureInfo.InvariantCulture, $"dd_count={hdf.DataDescriptors.Count}\r\n");
    sb.Append(CultureInfo.InvariantCulture, $"unique_tags={hdf.TagHistogram.Count}\r\n");
    sb.Append("\r\n[tag_histogram]\r\n");
    foreach (var (tag, count) in hdf.TagHistogram.OrderBy(p => p.Key))
      sb.Append(CultureInfo.InvariantCulture, $"tag_{tag:D4}_{Hdf4Reader.TagName(tag)}={count}\r\n");
    return Encoding.UTF8.GetBytes(sb.ToString());
  }
}
