#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Dtb;

/// <summary>
/// Pseudo-archive descriptor for Flattened Device Tree Blobs (DTB/DTBO). Walks
/// the structure block and emits one entry per leaf property. Property data that
/// parses cleanly as a UTF-8 string list is written as a <c>.txt</c> file;
/// anything else is written as raw bytes. A <c>metadata.ini</c> summarises the
/// FDT header + memory reservation map.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://github.com/devicetree-org/devicetree-specification</c> — Devicetree Specification — defines the flattened (FDT/DTB) encoding</description></item>
///   <item><description><c>https://www.devicetree.org</c> — devicetree.org portal</description></item>
/// </list>
/// </summary>
public sealed class DtbFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations,
    IArchiveCreatable, IArchiveModifiable, IArchiveDefragmentable, IArchiveShrinkable {

  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "Dtb";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "Flattened Device Tree Blob";
  /// <summary>
  /// Gets the category.
  /// </summary>
  public FormatCategory Category => FormatCategory.Archive;
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  /// <summary>
  /// Gets the default extension.
  /// </summary>
  public string DefaultExtension => ".dtb";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".dtb", ".dtbo"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([0xD0, 0x0D, 0xFE, 0xED], Confidence: 0.95),
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
    "Flattened Device Tree Blob — hierarchy-preserving create/add/replace/remove with reservation and boot CPU preservation.";

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
  public List<ArchiveEntryInfo> List(Stream stream, string? password) =>
    BuildEntries(stream).Select((e, i) => new ArchiveEntryInfo(
      Index: i, Name: e.Name,
      OriginalSize: e.Data.LongLength, CompressedSize: e.Data.LongLength,
      Method: e.Method, IsDirectory: false, IsEncrypted: false, LastModified: null)).ToList();

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    foreach (var e in BuildEntries(stream)) {
      if (files != null && files.Length > 0 && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, e.Data);
    }
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    var list = new List<(string Name, byte[] Data)>();
    foreach (var i in inputs) {
      if (i.IsDirectory) continue;
      var leaf = Path.GetFileName(i.ArchiveName);
      if (string.Equals(leaf, "metadata.ini", StringComparison.OrdinalIgnoreCase)) continue;
      list.Add((i.ArchiveName, i.ReadContent()));
    }
    DtbWriter.Write(output, list);
  }

  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs)
    => DtbModifier.Add(archive, inputs);

  public void Remove(Stream archive, string[] entryNames)
    => DtbModifier.Remove(archive, entryNames);

  public void Defragment(Stream archive)
    => DtbModifier.Defragment(archive);

  public void Defragment(Stream archive, DefragOptions options) {
    ArgumentNullException.ThrowIfNull(options);
    if (options.Mode != DefragMode.ConsolidateAtStart)
      throw new NotSupportedException("DTB has no movable allocation policy; only canonical consolidation is supported.");
    options.CancellationToken.ThrowIfCancellationRequested();
    DtbModifier.Defragment(archive);
    options.OnProgress?.Invoke(new DefragProgressEvent(
      "complete", 1, -1, -1, archive.Length, null, "DTB canonicalized"));
  }

  public void Shrink(Stream input, Stream output)
    => DtbModifier.Shrink(input, output);

  private static List<(string Name, byte[] Data, string Method)> BuildEntries(Stream stream) {
    if (stream.CanSeek) stream.Position = 0;
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    var fdt = DtbReader.Read(ms.GetBuffer().AsSpan(0, checked((int)ms.Length)));

    var entries = new List<(string, byte[], string)> {
      ("metadata.ini", BuildMetadata(fdt), "stored"),
    };

    var seen = new Dictionary<string, int>(StringComparer.Ordinal);
    foreach (var p in fdt.Properties) {
      var asText = TryStringifyPropertyValue(p.Data);
      var suffix = asText != null ? ".txt" : ".bin";
      var baseDir = p.NodePath.TrimStart('/');
      if (baseDir.Length == 0) baseDir = "_root";
      var entryName = $"{baseDir}/{p.Name}{suffix}";
      if (seen.TryGetValue(entryName, out var n)) {
        seen[entryName] = n + 1;
        entryName = $"{baseDir}/{p.Name}.{n + 1}{suffix}";
      } else {
        seen[entryName] = 1;
      }
      var data = asText != null ? Encoding.UTF8.GetBytes(asText) : p.Data;
      entries.Add((entryName, data, "stored"));
    }
    return entries;
  }

  private static string? TryStringifyPropertyValue(byte[] data) {
    if (data.Length == 0) return "";
    if (data[^1] != 0) return null;
    foreach (var b in data)
      if (b != 0 && (b < 0x20 || b > 0x7E)) return null;
    var parts = Encoding.ASCII.GetString(data, 0, data.Length - 1).Split('\0');
    return string.Join("\n", parts);
  }

  private static byte[] BuildMetadata(DtbReader.Fdt fdt) {
    var sb = new StringBuilder();
    sb.AppendLine("[fdt]");
    sb.Append(CultureInfo.InvariantCulture, $"magic = 0x{fdt.Header.Magic:X8}\n");
    sb.Append(CultureInfo.InvariantCulture, $"total_size = {fdt.Header.TotalSize}\n");
    sb.Append(CultureInfo.InvariantCulture, $"version = {fdt.Header.Version}\n");
    sb.Append(CultureInfo.InvariantCulture, $"last_comp_version = {fdt.Header.LastCompVersion}\n");
    sb.Append(CultureInfo.InvariantCulture, $"boot_cpuid_phys = 0x{fdt.Header.BootCpuidPhys:X8}\n");
    sb.Append(CultureInfo.InvariantCulture, $"off_dt_struct = 0x{fdt.Header.OffsetDtStruct:X}\n");
    sb.Append(CultureInfo.InvariantCulture, $"off_dt_strings = 0x{fdt.Header.OffsetDtStrings:X}\n");
    sb.Append(CultureInfo.InvariantCulture, $"off_mem_rsvmap = 0x{fdt.Header.OffsetMemRsvmap:X}\n");
    sb.Append(CultureInfo.InvariantCulture, $"size_dt_struct = {fdt.Header.SizeDtStruct}\n");
    sb.Append(CultureInfo.InvariantCulture, $"size_dt_strings = {fdt.Header.SizeDtStrings}\n");
    sb.Append(CultureInfo.InvariantCulture, $"property_count = {fdt.Properties.Count}\n");
    if (fdt.Reservations.Count > 0) {
      sb.AppendLine();
      sb.AppendLine("[memory_reservations]");
      for (var i = 0; i < fdt.Reservations.Count; i++) {
        var r = fdt.Reservations[i];
        sb.Append(CultureInfo.InvariantCulture,
          $"reserve_{i} = 0x{r.Address:X16} + 0x{r.Size:X16} bytes\n");
      }
    }
    return Encoding.UTF8.GetBytes(sb.ToString());
  }
}
