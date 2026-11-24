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
  // CanModify covers the edits Windows Server 2022 (ReFS 3.7) and 2025 (ReFS 3.14) accept from
  // this editor - each mounted read-only and read-write, every file hashed as expected, no ReFS
  // event: same-length overwrite (RefsInPlaceOverwrite), rename within a directory
  // (RefsInPlaceRename), remove (RefsInPlaceRemove) and length-changing replace
  // (RefsInPlaceResize) and adding a file (RefsInPlaceAdd) held in its directory row, and
  // creating a directory on ReFS 3.7 (RefsInPlaceMkdir), in the layouts those checks cover.
  // Defragment and everything outside those layouts is refused before a byte is touched.
  // See tools/foreign-os-oracles and RefsWindowsOracleTests.
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanModify |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsMultipleEntries;

  /// <summary>Why an edit outside the Windows-checked set is refused.</summary>
  internal const string EditsWithheld =
    "ReFS: this edit is not made in place. Only overwrite, rename, remove and length-changing replace of files held in " +
    "their directory row are, as Windows Server 2022 and 2025 accept them; the image is unchanged.";
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
  public string Description => "Microsoft ReFS 3.x volume image, read: namespace and allocation parsing checked file for file against volumes Windows Server 2022 (ReFS 3.7) and 2025 (ReFS 3.14) formatted. In place: overwrite, length-changing replace, rename, remove, adding files and creating directories (ReFS 3.7), as Windows accepts them; defragmenting is refused.";

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
    throw new NotSupportedException(EditsWithheld);
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
        InPlaceChanges = [],
        Notes = [
          EditsWithheld,
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
    // Files whose bytes this reader does not locate: refused by name once everything else is out,
    // never written as empty or short files.
    var unread = new List<string>();
    var decoded = false;
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
          // A stream layout the namespace reader does not decode surfaces as "resident" without
          // resident bytes of its size; it is refused, never written empty.
          if (record.IsResident && (record.ResidentContent?.LongLength ?? (record.Size == 0 ? 0 : -1)) != record.Size) {
            unread.Add(record.Path);
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
        decoded = true;
      }
    } catch (Exception e) when (e is InvalidDataException or NotSupportedException or IOException or ArgumentException or OverflowException) {
      // Diagnostic fallback below remains useful for partially damaged images.
    }

    if (!decoded) {
      ExtractDiagnosticSurface(stream, outputDir, files);
      return;
    }
    if (unread.Count > 0)
      throw new NotSupportedException(
      $"ReFS: the data of {unread.Count} file(s) lies in a stream layout this reader does not decode; they were not extracted: " +
      string.Join(", ", unread.Take(20)) + (unread.Count > 20 ? ", ..." : "."));
  }

  /// <summary>
  /// Replaces or adds files in place: a same-length overwrite of any number of files
  /// (<see cref="RefsInPlaceOverwrite"/>), or one length-changing replacement
  /// (<see cref="RefsInPlaceResize"/>), or one new file (<see cref="RefsInPlaceAdd"/>).
  /// A new directory is created by <see cref="RefsInPlaceMkdir"/>.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(inputs);
    // Every input is planned and read before any byte is written, so a refusal leaves the
    // image unchanged.
    var metadata = RefsMetadataReader.Open(archive);
    var files = inputs.Where(static i => !i.IsDirectory).ToList();
    var sizes = new RefsNamespaceReader(metadata).ReadAll()
      .Where(static f => !f.IsDirectory)
      .GroupBy(static f => f.Path, StringComparer.OrdinalIgnoreCase)
      .ToDictionary(static g => g.Key, static g => g.First().Size, StringComparer.OrdinalIgnoreCase);
    var directories = new RefsNamespaceReader(metadata).ReadAll().Where(static f => f.IsDirectory)
      .Select(static f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
    var newDirectories = inputs.Where(static i => i.IsDirectory)
      .Where(i => !directories.Contains(i.ArchiveName.Replace('\\', '/').Trim('/'))).ToList();
    if (newDirectories.Count > 0) {
      // A new directory adds an object, a page and rows, so it is made alone.
      if (inputs.Count != 1)
        throw new NotSupportedException("ReFS: a new directory is created one per call.");
      RefsInPlaceMkdir.Create(archive, newDirectories[0].ArchiveName);
      return;
    }
    var contents = files.Select(static i => i.ReadContent()).ToList();
    var creating = files.Where(i => !sizes.ContainsKey(i.ArchiveName.Replace('\\', '/').Trim('/'))).ToList();
    if (creating.Count > 0) {
      // A new file inserts rows into its directory, so it is made alone.
      if (files.Count != 1)
        throw new NotSupportedException("ReFS: a new file is added one file per call.");
      RefsInPlaceAdd.Add(archive, files[0].ArchiveName, contents[0]);
      return;
    }
    var resizing = files.Select((input, i) =>
      sizes.TryGetValue(input.ArchiveName.Replace('\\', '/').Trim('/'), out var size) && size != contents[i].LongLength).ToList();
    if (resizing.Any(static r => r)) {
      // A length change rewrites metadata, so it is made alone.
      if (files.Count != 1)
        throw new NotSupportedException("ReFS: a replacement that changes a file's length is made one file per call.");
      var graph = new RefsMetadataGraph(archive, metadata);
      var plan = RefsInPlaceResize.Prepare(archive, metadata, graph, files[0].ArchiveName, contents[0].LongLength);
      RefsInPlaceResize.Apply(archive, metadata, graph, plan, contents[0]);
      return;
    }
    var planned = files.Select((input, i) => RefsInPlaceOverwrite.Plan(archive, metadata, input.ArchiveName, contents[i].LongLength, allowIntegrity: true)).ToList();
    var checksums = files.Select((input, i) => RefsInPlaceOverwrite.PlanChecksums(archive, metadata, input.ArchiveName, planned[i], contents[i])).ToList();
    var overwriteGraph = new RefsMetadataGraph(archive, metadata);
    foreach (var plan in checksums)
      if (plan is { } c && !RefsPageEditor.CanReplaceValue(overwriteGraph, c.Row, c.Value.Length))
        throw new NotSupportedException("ReFS: the directory page cannot take the rewritten checksums.");
    for (var i = 0; i < planned.Count; ++i)
      RefsInPlaceOverwrite.Apply(archive, metadata.ClusterSize, planned[i], contents[i]);
    foreach (var plan in checksums)
      if (plan is { } c)
        overwriteGraph.RefreshChecksumPaths([RefsPageEditor.ReplaceValue(overwriteGraph, c.Row, c.Value)]);
  }

  /// <summary>Removes files held in their directory row, in place (<see cref="RefsInPlaceRemove"/>).</summary>
  public void Remove(Stream archive, string[] entryNames) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryNames);
    // One file per call keeps each removal a complete edit; a refusal leaves the image as the
    // removals before it left it.
    foreach (var name in entryNames.Where(static n => !string.IsNullOrWhiteSpace(n)))
      RefsInPlaceRemove.Remove(archive, name);
  }

  /// <summary>Renames a file within its directory, in place (<see cref="RefsInPlaceRename"/>).</summary>
  public void Rename(Stream image, string sourcePath, string destinationPath)
    => RefsInPlaceRename.Rename(image, sourcePath, destinationPath);

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
