#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Dtb;

/// <summary>
/// Pseudo-archive descriptor for Flattened Device Tree Blobs (DTB/DTBO). The
/// archive view exposes device-tree nodes as directories and properties as files;
/// <c>metadata.ini</c> carries header values that are not themselves properties.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://github.com/devicetree-org/devicetree-specification</c> — Devicetree Specification v0.4, flattened device-tree encoding</description></item>
///   <item><description><c>https://github.com/dgibson/dtc/tree/main/libfdt</c> — libfdt reference behavior for v17 mutable/packed blobs</description></item>
/// </list>
/// </summary>
public sealed class DtbFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations,
    IArchiveCreatable, IArchiveModifiable, IArchiveDefragmentable, IArchiveShrinkable, IArchiveLayoutMap {

  public string Id => "Dtb";
  public string DisplayName => "Flattened Device Tree Blob";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify | FormatCapabilities.CanTest |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
  public string DefaultExtension => ".dtb";
  public IReadOnlyList<string> Extensions => [".dtb", ".dtbo"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([0xD0, 0x0D, 0xFE, 0xED], Confidence: 0.95),
  ];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "Flattened Device Tree Blob — hierarchy-preserving read/write, pack, wipe, shrink and purge.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password)
    => BuildEntries(stream).Select((entry, index) => new ArchiveEntryInfo(
      Index: index,
      Name: entry.Name,
      OriginalSize: entry.Data.LongLength,
      CompressedSize: entry.Data.LongLength,
      Method: entry.Method,
      IsDirectory: entry.IsDirectory,
      IsEncrypted: false,
      LastModified: null)).ToList();

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    foreach (var entry in BuildEntries(stream)) {
      if (files != null && files.Length > 0 && !MatchesFilter(entry.Name, files)) continue;
      if (entry.IsDirectory) {
        CreateSafeDirectory(outputDir, entry.Name);
        continue;
      }
      WriteFile(outputDir, entry.Name, entry.Data);
    }
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);

    var properties = new List<DtbWriter.PropertySpec>();
    var nodePaths = new List<string> { "/" };
    MetadataInfo? metadata = null;

    foreach (var input in inputs) {
      if (input.IsDirectory) {
        nodePaths.Add(DtbWriter.FromArchiveDirectory(input.ArchiveName));
        continue;
      }

      if (string.Equals(Path.GetFileName(input.ArchiveName), "metadata.ini", StringComparison.OrdinalIgnoreCase)) {
        metadata = ParseMetadata(input.ReadContent());
        continue;
      }

      properties.Add(DtbWriter.FromArchiveEntry(input.ArchiveName, input.ReadContent()));
    }

    DtbWriter.Write(output, properties, nodePaths.Distinct(StringComparer.Ordinal).ToList(),
      metadata?.Reservations ?? [], metadata?.BootCpuidPhys ?? 0,
      addDefaultRootCells: metadata is null);
  }

  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs)
    => DtbModifier.Add(archive, inputs);

  public void Remove(Stream archive, string[] entryNames)
    => DtbModifier.Remove(archive, entryNames);

  /// <summary>Packs all FDT blocks tightly, equivalent to a logical rebuild defragmentation.</summary>
  public void Defragment(Stream archive)
    => DtbModifier.Pack(archive);

  /// <summary>Writes the smallest packed representation while preserving bytes outside FDT totalsize.</summary>
  public void Shrink(Stream input, Stream output)
    => DtbModifier.Pack(input, output);

  /// <summary>Removes all nodes below root and all properties while preserving reservations and boot CPU metadata.</summary>
  public void Purge(Stream archive)
    => DtbModifier.Purge(archive);

  /// <summary>
  /// Maps the real FDT block layout. Gaps inside header totalsize are proven growth/padding
  /// space and are therefore free; bytes outside totalsize remain reserved because they are
  /// not described by the FDT and may belong to an enclosing/concatenated payload.
  /// </summary>
  public IEnumerable<DefragBlockInfo> EnumerateLayout(Stream archive) {
    var (fdt, length) = ReadForLayout(archive);
    var ranges = new (long Offset, long Length, string Name)[] {
      (0, 40, "FDT header"),
      (fdt.Header.OffsetMemRsvmap,
        fdt.ReservationMapEnd - (long)fdt.Header.OffsetMemRsvmap,
        "memory reservation map"),
      (fdt.Header.OffsetDtStruct, fdt.Header.SizeDtStruct, "structure block"),
      (fdt.Header.OffsetDtStrings, fdt.Header.SizeDtStrings, "strings block"),
    };

    long cursor = 0;
    foreach (var range in ranges.Where(range => range.Length > 0).OrderBy(range => range.Offset)) {
      if (range.Offset > cursor)
        yield return new DefragBlockInfo(cursor, range.Offset - cursor, DefragBlockKind.Free, "FDT growth padding");
      yield return new DefragBlockInfo(range.Offset, range.Length, DefragBlockKind.MetadataReserved, range.Name);
      cursor = Math.Max(cursor, checked(range.Offset + range.Length));
    }

    var totalSize = (long)fdt.Header.TotalSize;
    if (cursor < totalSize)
      yield return new DefragBlockInfo(cursor, totalSize - cursor, DefragBlockKind.Free, "FDT growth padding");
    if (length > totalSize)
      yield return new DefragBlockInfo(totalSize, length - totalSize, DefragBlockKind.MetadataReserved,
        "bytes outside FDT totalsize");
  }

  private static List<ViewEntry> BuildEntries(Stream stream) {
    var fdt = Read(stream);
    var entries = new List<ViewEntry> {
      new("metadata.ini", BuildMetadata(fdt), "stored", false),
    };

    foreach (var node in fdt.Nodes) {
      var directory = node.Path.Trim('/');
      if (directory.Length != 0)
        entries.Add(new ViewEntry(directory + "/", [], "stored", true));
    }

    var seen = new Dictionary<string, int>(StringComparer.Ordinal);
    foreach (var property in fdt.Properties) {
      var asText = TryStringifyPropertyValue(property.Data);
      var suffix = asText != null ? ".txt" : ".bin";
      var baseDirectory = property.NodePath.TrimStart('/');
      if (baseDirectory.Length == 0) baseDirectory = "_root";
      var entryName = $"{baseDirectory}/{property.Name}{suffix}";
      if (seen.TryGetValue(entryName, out var count)) {
        seen[entryName] = count + 1;
        entryName = $"{baseDirectory}/{property.Name}.{count + 1}{suffix}";
      } else {
        seen[entryName] = 1;
      }
      var data = asText != null ? Encoding.UTF8.GetBytes(asText) : property.Data;
      entries.Add(new ViewEntry(entryName, data, "stored", false));
    }
    return entries;
  }

  private static DtbReader.Fdt Read(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    if (!stream.CanRead)
      throw new ArgumentException("DTB input must be readable.", nameof(stream));
    if (stream.CanSeek) stream.Position = 0;
    using var buffer = new MemoryStream();
    stream.CopyTo(buffer);
    if (buffer.Length > int.MaxValue)
      throw new NotSupportedException("DTB images larger than 2 GiB are not supported.");
    return DtbReader.Read(buffer.GetBuffer().AsSpan(0, checked((int)buffer.Length)));
  }

  private static (DtbReader.Fdt Fdt, long Length) ReadForLayout(Stream stream) {
    ArgumentNullException.ThrowIfNull(stream);
    if (!stream.CanRead || !stream.CanSeek)
      throw new ArgumentException("DTB layout mapping requires a readable, seekable stream.", nameof(stream));
    var length = stream.Length;
    var fdt = Read(stream);
    return (fdt, length);
  }

  private static string? TryStringifyPropertyValue(byte[] data) {
    // Zero-length DT properties are boolean properties, not empty strings. Keeping
    // them binary makes extract -> create preserve length 0 instead of inventing NUL.
    if (data.Length == 0 || data[^1] != 0) return null;
    foreach (var value in data)
      if (value != 0 && (value < 0x20 || value > 0x7E)) return null;
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
    sb.Append(CultureInfo.InvariantCulture, $"node_count = {fdt.Nodes.Count}\n");
    sb.Append(CultureInfo.InvariantCulture, $"property_count = {fdt.Properties.Count}\n");
    if (fdt.Reservations.Count > 0) {
      sb.AppendLine();
      sb.AppendLine("[memory_reservations]");
      for (var i = 0; i < fdt.Reservations.Count; ++i) {
        var reservation = fdt.Reservations[i];
        sb.Append(CultureInfo.InvariantCulture,
          $"reserve_{i} = 0x{reservation.Address:X16} + 0x{reservation.Size:X16} bytes\n");
      }
    }
    return Encoding.UTF8.GetBytes(sb.ToString());
  }

  private static MetadataInfo ParseMetadata(byte[] data) {
    var bootCpuidPhys = 0u;
    var reservations = new List<DtbReader.Reservation>();
    var text = Encoding.UTF8.GetString(data);
    foreach (var rawLine in text.Split('\n')) {
      var line = rawLine.Trim();
      if (line.Length == 0 || line.StartsWith('[')) continue;
      var equals = line.IndexOf('=');
      if (equals < 0) continue;
      var key = line[..equals].Trim();
      var value = line[(equals + 1)..].Trim();

      if (key.Equals("boot_cpuid_phys", StringComparison.OrdinalIgnoreCase)) {
        var parsed = ParseHex(value, key);
        if (parsed > uint.MaxValue)
          throw new InvalidDataException("DTB metadata: boot_cpuid_phys exceeds 32 bits.");
        bootCpuidPhys = (uint)parsed;
        continue;
      }

      if (!key.StartsWith("reserve_", StringComparison.OrdinalIgnoreCase)) continue;
      if (value.EndsWith("bytes", StringComparison.OrdinalIgnoreCase))
        value = value[..^5].TrimEnd();
      var plus = value.IndexOf('+');
      if (plus < 0)
        throw new InvalidDataException($"DTB metadata: malformed reservation '{line}'.");
      var address = ParseHex(value[..plus].Trim(), key);
      var size = ParseHex(value[(plus + 1)..].Trim(), key);
      reservations.Add(new DtbReader.Reservation(address, size));
    }
    return new MetadataInfo(bootCpuidPhys, reservations);
  }

  private static ulong ParseHex(string value, string key) {
    var digits = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? value[2..] : value;
    if (!ulong.TryParse(digits, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var result))
      throw new InvalidDataException($"DTB metadata: '{key}' has invalid hexadecimal value '{value}'.");
    return result;
  }

  private static void CreateSafeDirectory(string outputDirectory, string archiveName) {
    var root = Path.GetFullPath(outputDirectory);
    var relative = archiveName.TrimEnd('/', '\\').Replace('/', Path.DirectorySeparatorChar);
    var target = Path.GetFullPath(Path.Combine(root, relative));
    var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    var rootPrefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
    if (!target.Equals(root, comparison) && !target.StartsWith(rootPrefix, comparison))
      throw new InvalidDataException($"DTB: node path '{archiveName}' escapes the extraction directory.");
    Directory.CreateDirectory(target);
  }

  private sealed record ViewEntry(string Name, byte[] Data, string Method, bool IsDirectory);
  private sealed record MetadataInfo(uint BootCpuidPhys, IReadOnlyList<DtbReader.Reservation> Reservations);
}
