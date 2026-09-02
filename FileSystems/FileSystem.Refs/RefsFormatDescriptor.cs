#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.Refs;

/// <summary>
/// Microsoft ReFS (Resilient File System) volume descriptor.
///
/// Read/list/extract follows metadata reachable from the active checkpoint.
/// Offline layout writes are coordinated by the ReFS placement manager, which
/// can relocate file data, live MSB+ metadata and checkpoint pages while
/// preserving the format-fixed VBR/SUPB bootstrap anchors.
/// </summary>
public sealed class RefsFormatDescriptor :
  IFormatDescriptor,
  IArchiveFormatOperations,
  IArchiveModifiable,
  IFilesystemExtentMap,
  IArchiveDefragmentable,
  ILayoutOptimizable,
  IFilesystemDriverProvider,
  IFilesystemDriverReadinessProvider {

  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "Refs";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "ReFS";
  /// <summary>
  /// Gets the category.
  /// </summary>
  public FormatCategory Category => FormatCategory.Archive;
  // CanModify covers the offline-quiescent image editor only. A mounted ReFS
  // driver's transactional write path is a separate readiness tier and is not
  // what this flag reports — see DRIVER_READINESS.md.
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanModify |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;
  /// <summary>
  /// Gets the default extension.
  /// </summary>
  public string DefaultExtension => ".refs";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".refs"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([0x52, 0x65, 0x46, 0x53, 0x00, 0x00, 0x00, 0x00], Offset: 3, Confidence: 0.85),
  ];
  /// <summary>
  /// Gets the methods.
  /// </summary>
  public IReadOnlyList<FormatMethodInfo> Methods => [
    new("resident", "Resident / inline"),
    new("extent", "Extent-backed"),
    new("stored", "Raw image"),
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
  public string Description => "Microsoft ReFS 3.x volume image with native read-only driver projection, namespace/allocation parsing, and offline-quiescent existing-file replace/remove plus metadata placement. Native mounted-driver transactions remain a separate readiness tier.";

  /// <summary>
  /// Probes the image and reports the filesystem driver profile.
  /// </summary>
  public FilesystemDriverProfile ProbeFilesystem(Stream image)
    => RefsFilesystemDriver.Probe(image);

  /// <summary>
  /// Opens a filesystem session over the image.
  /// </summary>
  public IFilesystemSession OpenFilesystem(Stream image, FilesystemOpenOptions options)
    => RefsFilesystemDriver.Open(image, options);

  /// <summary>
  /// Describes how ready the filesystem driver is for the requested access.
  /// </summary>
  public FilesystemDriverReadinessReport DescribeFilesystemDriverReadiness(
      Stream image,
      FilesystemDriverTarget target)
    => RefsFilesystemDriver.Readiness(image, target);

  /// <summary>
  /// Enumerates the extents.
  /// </summary>
  public IEnumerable<DefragBlockInfo> EnumerateExtents(Stream image)
    => RefsExtentMap.Enumerate(image);

  /// <summary>
  /// Performs the defragment operation.
  /// </summary>
  public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions());

  /// <summary>
  /// Performs the defragment operation.
  /// </summary>
  public void Defragment(Stream archive, DefragOptions options) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(options);
    new RefsPlacementManager(archive).Execute(options);
  }

  /// <summary>
  /// Performs the analyze layout operation.
  /// </summary>
  public LayoutAnalysis AnalyzeLayout(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    try {
      var metadata = RefsMetadataReader.Open(image);
      var files = new RefsNamespaceReader(metadata).ReadAll();
      var graph = new RefsMetadataGraph(image, metadata);
      var bootstrap = RefsBootstrapState.Open(image);
      long slack = 0;
      foreach (var file in files) {
        if (file.IsDirectory) continue;
        var allocated = Math.Max(0, file.AllocatedSize);
        var size = Math.Max(0, file.Size);
        if (allocated > size) slack = checked(slack + allocated - size);
      }
      var movableMetadata = new RefsMetadataMover(image).RelocatableMetadata.Count;
      return new LayoutAnalysis {
        ImageSize = image.CanSeek ? image.Length : 0,
        CurrentUnitSize = metadata.ClusterSize,
        CurrentSlackBytes = slack,
        OptimalUnitSize = metadata.ClusterSize,
        OptimalSlackBytes = slack,
        InPlaceChanges = [
          "File extent placement / consolidation / interleave",
          "Live ReFS MSB+ metadata-page placement",
          "ReFS checkpoint placement through SUPB repointing",
        ],
        Notes = [
          $"Active graph contains {graph.Pages.Count:N0} live MSB+ page(s); {movableMetadata:N0} metadata/checkpoint region(s) are currently allocator-addressable and relocatable.",
          $"The winning SUPB is fixed at LCN 0x{bootstrap.WinningSuperblockLcn:X}; VBR and all three SUPB slots are format-fixed bootstrap anchors, not movable extents.",
          "ReFS cluster geometry is preserved; placement changes physical location while retaining the volume's allocation-unit size.",
          "Resident non-empty files are promoted to extent-backed storage when they must participate in physical layout.",
          "Sparse, integrity-checksummed, snapshot/shared and otherwise undecoded stream allocations remain pinned rather than being guessed.",
        ],
        RequiresRebuild = ["Changing ReFS cluster size requires formatting/rebuilding the volume."],
      };
    } catch (Exception e) when (e is InvalidDataException or NotSupportedException or IOException) {
      return new LayoutAnalysis {
        ImageSize = image.CanSeek ? image.Length : 0,
        Notes = [$"ReFS layout analysis could not traverse the active metadata: {e.Message}"],
      };
    }
  }

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var entries = new List<ArchiveEntryInfo>();
    try {
      var metadata = RefsMetadataReader.Open(stream);
      var files = new RefsNamespaceReader(metadata).ReadAll();
      var index = 0;
      foreach (var file in files) {
        entries.Add(new ArchiveEntryInfo(
          index++,
          file.Path,
          file.Size,
          file.IsResident ? file.Size : file.AllocatedSize,
          file.IsDirectory ? "directory" : file.IsResident ? "resident" : "extent",
          file.IsDirectory,
          false,
          file.Modified,
          file.IsDirectory ? "directory" : "stream"));
      }
      if (entries.Count > 0) return entries;
    } catch (Exception e) when (e is InvalidDataException or NotSupportedException or IOException or ArgumentException) {
      // Preserve the cheap diagnostic surface for damaged or synthetic images.
    }
    return ListDiagnosticSurface(stream);
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    try {
      var metadata = RefsMetadataReader.Open(stream);
      var records = new RefsNamespaceReader(metadata).ReadAll();
      if (records.Count > 0) {
        foreach (var record in records) {
          if (!MatchesRequested(record.Path, files)) continue;
          if (record.IsDirectory) {
            Directory.CreateDirectory(Path.Combine(outputDir, record.Path.Replace('/', Path.DirectorySeparatorChar)));
            continue;
          }

          using var target = CreateEntryFile(outputDir, record.Path);
          if (record.IsResident) {
            if (record.ResidentContent != null) target.Write(record.ResidentContent);
            continue;
          }

          var remaining = record.Size;
          foreach (var extent in record.Extents.OrderBy(e => e.FileVcn)) {
            if (remaining <= 0) break;
            if (extent.IsSparse) {
              var zeros = Math.Min(remaining, checked((long)extent.ClusterCount * metadata.ClusterSize));
              WriteZeros(target, zeros);
              remaining -= zeros;
              continue;
            }
            for (uint i = 0; i < extent.ClusterCount && remaining > 0; ++i) {
              var physical = metadata.TranslateVirtualLcn(checked(extent.VirtualLcn + i));
              var sourceOffset = checked((long)physical * metadata.ClusterSize);
              if (sourceOffset < 0 || sourceOffset + metadata.ClusterSize > stream.Length)
                throw new InvalidDataException($"ReFS extent for '{record.Path}' points outside the image.");
              stream.Position = sourceOffset;
              var take = checked((int)Math.Min(remaining, metadata.ClusterSize));
              CopyExactly(stream, target, take);
              remaining -= take;
            }
          }
          if (remaining != 0)
            throw new InvalidDataException($"ReFS extents for '{record.Path}' do not cover its logical size.");
        }
        return;
      }
    } catch (Exception e) when (e is InvalidDataException or NotSupportedException or IOException or ArgumentException or OverflowException) {
      // Diagnostic fallback below remains useful for partially damaged images.
    }

    ExtractDiagnosticSurface(stream, outputDir, files);
  }

  /// <summary>
  /// Offline-quiescent existing-file replacement for the proven regular-stream profile.
  /// A new name is rejected before mutation until ReFS file-identity/security/link fields
  /// are proven for every supported 3.x profile.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs)
    => RefsOfflineModifier.Add(archive, inputs);

  /// <summary>
  /// Removes regular files or empty directories from an unmounted ReFS image. Namespace
  /// deletion is published through immutable B+ replacement pages and the alternate CHKP.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames)
    => RefsOfflineModifier.Remove(archive, entryNames);

  private static List<ArchiveEntryInfo> ListDiagnosticSurface(Stream stream) {
    var entries = new List<ArchiveEntryInfo>();
    var imageLength = stream.CanSeek ? stream.Length : 0;
    byte[] header;
    try { header = ReadHeader(stream); }
    catch {
      entries.Add(new ArchiveEntryInfo(0, "FULL.refs", imageLength, imageLength, "stored", false, false, null));
      entries.Add(new ArchiveEntryInfo(1, "metadata.ini", 0, 0, "stored", false, false, null));
      return entries;
    }

    RefsVolumeHeader hdr;
    try { hdr = RefsVolumeHeader.TryParse(header); }
    catch {
      entries.Add(new ArchiveEntryInfo(0, "FULL.refs", imageLength, imageLength, "stored", false, false, null));
      entries.Add(new ArchiveEntryInfo(1, "metadata.ini", 0, 0, "stored", false, false, null));
      return entries;
    }

    entries.Add(new ArchiveEntryInfo(0, "FULL.refs", imageLength, imageLength, "stored", false, false, null));
    entries.Add(new ArchiveEntryInfo(1, "metadata.ini", 0, 0, "stored", false, false, null));
    if (hdr.Valid)
      entries.Add(new ArchiveEntryInfo(2, "volume_header.bin", hdr.RawBytes.LongLength, hdr.RawBytes.LongLength, "stored", false, false, null));
    return entries;
  }

  private static void ExtractDiagnosticSurface(Stream stream, string outputDir, string[]? files) {
    byte[] header;
    try { header = ReadHeader(stream); }
    catch {
      WriteFile(outputDir, "metadata.ini", Encoding.UTF8.GetBytes("parse_status=partial\n"));
      return;
    }

    RefsVolumeHeader hdr;
    try { hdr = RefsVolumeHeader.TryParse(header); }
    catch {
      WriteIfMatch(outputDir, "metadata.ini", Encoding.UTF8.GetBytes("parse_status=partial\n"), files);
      return;
    }

    if (MatchesRequested("FULL.refs", files)) {
      var outPath = Path.Combine(outputDir, "FULL.refs");
      Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
      using var output = File.Create(outPath);
      if (stream.CanSeek) stream.Position = 0;
      stream.CopyTo(output);
    }
    WriteIfMatch(outputDir, "metadata.ini", BuildMetadata(hdr), files);
    if (hdr.Valid) WriteIfMatch(outputDir, "volume_header.bin", hdr.RawBytes, files);
  }

  private static bool MatchesRequested(string name, string[]? filter)
    => filter == null || filter.Length == 0 || MatchesFilter(name, filter);

  private static void WriteIfMatch(string outputDir, string name, byte[] data, string[]? filter) {
    if (!MatchesRequested(name, filter)) return;
    WriteFile(outputDir, name, data);
  }

  private static void WriteZeros(Stream target, long count) {
    var buffer = new byte[64 * 1024];
    while (count > 0) {
      var take = (int)Math.Min(count, buffer.Length);
      target.Write(buffer, 0, take);
      count -= take;
    }
  }

  private static void CopyExactly(Stream source, Stream target, int count) {
    var buffer = new byte[Math.Min(64 * 1024, Math.Max(1, count))];
    var remaining = count;
    while (remaining > 0) {
      var take = Math.Min(remaining, buffer.Length);
      var read = source.Read(buffer, 0, take);
      if (read == 0) throw new EndOfStreamException();
      target.Write(buffer, 0, read);
      remaining -= read;
    }
  }

  private static byte[] BuildMetadata(RefsVolumeHeader hdr) {
    var bldr = new StringBuilder();
    bldr.Append(CultureInfo.InvariantCulture, $"parse_status={(hdr.Valid ? "ok" : "partial")}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"oem_id={hdr.OemId}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"sector_size={hdr.SectorSize}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"sectors_per_cluster={hdr.SectorsPerCluster}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"bytes_per_cluster={hdr.BytesPerCluster}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"total_sectors={hdr.TotalSectors}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"total_clusters={hdr.TotalClusters}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"version_major={hdr.MajorVersion}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"version_minor={hdr.MinorVersion}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"checksum_algorithm=0x{hdr.ChecksumAlgorithm:X4}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"volume_flags=0x{hdr.VolumeFlags:X8}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"volume_serial=0x{hdr.VolumeSerialNumber:X16}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"bytes_per_container={hdr.BytesPerContainer}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"fsrs_found={hdr.FsrsFound}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"fsrs_offset={hdr.FsrsOffset}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"fsrs_length={hdr.FsrsLength}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"fsrs_checksum=0x{hdr.FsrsCheckSum:X4}\n");
    bldr.Append(CultureInfo.InvariantCulture, $"vbr_checksum_valid={hdr.FsrsChecksumValid}\n");
    return Encoding.UTF8.GetBytes(bldr.ToString());
  }

  private static byte[] ReadHeader(Stream stream) {
    if (stream.CanSeek) stream.Position = 0;
    var buffer = new byte[512];
    var total = 0;
    while (total < buffer.Length) {
      var read = stream.Read(buffer, total, buffer.Length - total);
      if (read == 0) break;
      total += read;
    }
    return total == buffer.Length ? buffer : buffer[..total];
  }
}
