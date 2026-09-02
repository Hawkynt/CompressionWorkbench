#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Registry;
using Compression.Registry.Streaming;
using static Compression.Registry.FormatHelpers;

namespace FileSystem.BcacheFs;

/// <summary>
/// Full workbench descriptor for the single-device bcachefs profile implemented
/// here: native b-trees, true in-place CRUD, allocation/accounting maintenance,
/// in-place defragmentation, purge and unused-space wiping.
/// </summary>
public sealed class BcacheFsFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations,
    IArchiveCreatable, IArchiveShrinkable, IArchiveModifiable, IArchiveDefragmentable,
    IArchiveWriteConstraints, IFormatOptionsSchema, ILayoutOptimizable,
    IFilesystemExtentMap, IWipeEmpty {

    /// <summary>
  /// Gets the options schema.
  /// </summary>
public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    FilesystemSchemaPresets.VolumeLabel(maxChars: 31),
    FilesystemSchemaPresets.ImageSize(["128 MB", "256 MB", "512 MB"],
      description: "Total image capacity. Must be at least 128 MB so the superblock copies fit."),
  ];

    /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "BcacheFs";
    /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "BcacheFS";
    /// <summary>
  /// Gets the category.
  /// </summary>
public FormatCategory Category => FormatCategory.Archive;
    /// <summary>
  /// Gets the capabilities.
  /// </summary>
public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.CanCreate | FormatCapabilities.CanModify |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsOptimize |
    FormatCapabilities.SupportsMultipleEntries | FormatCapabilities.SupportsDirectories;
    /// <summary>
  /// Gets the default extension.
  /// </summary>
public string DefaultExtension => ".bcachefs";
    /// <summary>
  /// Gets the extensions.
  /// </summary>
public IReadOnlyList<string> Extensions => [".bcachefs"];
    /// <summary>
  /// Gets the compound extensions.
  /// </summary>
public IReadOnlyList<string> CompoundExtensions => [];
    /// <summary>
  /// Gets the magic signatures.
  /// </summary>
public IReadOnlyList<MagicSignature> MagicSignatures => [
    new(BcacheFsSuperblock.MagicUuid, Offset: BcacheFsSuperblock.MagicOffset, Confidence: 0.85f),
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
    "BcacheFS Linux filesystem image — native b-tree R/W with true in-place add/replace/remove, "
    + "purge, defragment, optimize/layout maintenance and free-space/slack wiping.";

    /// <summary>
  /// Gets the max total archive size.
  /// </summary>
public long? MaxTotalArchiveSize => null;
    /// <summary>
  /// Gets the min total archive size.
  /// </summary>
public long? MinTotalArchiveSize => BcacheFsWriter.MinImageSize;
    /// <summary>
  /// Gets the accepted inputs description.
  /// </summary>
public string AcceptedInputsDescription =>
    "Regular files and nested directories; each UTF-8 path component is limited to 255 bytes.";

    /// <summary>
  /// Performs the can accept operation.
  /// </summary>
public bool CanAccept(ArchiveInputInfo input, out string? reason) {
    ArgumentNullException.ThrowIfNull(input);
    var path = input.ArchiveName.Replace('\\', '/').Trim('/');
    if (path.Length == 0) {
      reason = "The bcachefs entry name must not be empty.";
      return false;
    }

    foreach (var component in path.Split('/', StringSplitOptions.RemoveEmptyEntries)) {
      if (component is "." or "..") {
        reason = $"The path component '{component}' is reserved.";
        return false;
      }
      if (component.Contains('\0')) {
        reason = "bcachefs file names cannot contain NUL.";
        return false;
      }
      if (Encoding.UTF8.GetByteCount(component) > 255) {
        reason = $"The path component '{component}' exceeds bcachefs' 255-byte UTF-8 name limit.";
        return false;
      }
    }

    reason = null;
    return true;
  }

    /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var entries = new List<ArchiveEntryInfo>();
    byte[] header;
    try {
      header = ReadHeader(stream);
    } catch {
      return [
        new ArchiveEntryInfo(0, "FULL.bcachefs", 0, 0, "stored", false, false, null),
        new ArchiveEntryInfo(1, "metadata.ini", 0, 0, "stored", false, false, null),
      ];
    }

    BcacheFsSuperblock sb;
    try {
      sb = BcacheFsSuperblock.TryParse(header);
    } catch {
      return [
        new ArchiveEntryInfo(0, "FULL.bcachefs", header.LongLength, header.LongLength, "stored", false, false, null),
        new ArchiveEntryInfo(1, "metadata.ini", 0, 0, "stored", false, false, null),
      ];
    }

    try {
      if (stream.CanSeek) stream.Position = 0;
      using var reader = new BcacheFsReader(stream);
      if (reader.Valid) {
        var index = 0;
        foreach (var entry in reader.Entries)
          entries.Add(new ArchiveEntryInfo(index++, entry.Name, entry.Size, entry.Size,
            "stored", false, false, null));

        // the volume's own description travels with its files, on a readable
        // filesystem exactly as on a carved one
        entries.Add(new ArchiveEntryInfo(index++, "metadata.ini", 0, 0, "stored", false, false, null));
        if (sb.Valid)
          entries.Add(new ArchiveEntryInfo(index, "superblock.bin", sb.RawBytes.LongLength,
            sb.RawBytes.LongLength, "stored", false, false, null));
        return entries;
      }
    } catch {
      // A partial/carved superblock still has useful synthetic entries below.
    }

    var fullLength = stream.CanSeek ? stream.Length : header.LongLength;
    entries.Add(new ArchiveEntryInfo(0, "FULL.bcachefs", fullLength, fullLength, "stored", false, false, null));
    entries.Add(new ArchiveEntryInfo(1, "metadata.ini", 0, 0, "stored", false, false, null));
    if (sb.Valid)
      entries.Add(new ArchiveEntryInfo(2, "superblock.bin", sb.RawBytes.LongLength,
        sb.RawBytes.LongLength, "stored", false, false, null));
    return entries;
  }

    /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    byte[] header;
    try {
      header = ReadHeader(stream);
    } catch {
      WriteFile(outputDir, "metadata.ini", Encoding.UTF8.GetBytes("parse_status=partial\n"));
      return;
    }

    BcacheFsSuperblock sb;
    try {
      sb = BcacheFsSuperblock.TryParse(header);
    } catch {
      WriteIfMatch(outputDir, "FULL.bcachefs", header, files);
      WriteIfMatch(outputDir, "metadata.ini", Encoding.UTF8.GetBytes("parse_status=partial\n"), files);
      return;
    }

    try {
      if (stream.CanSeek) stream.Position = 0;
      using var reader = new BcacheFsReader(stream);
      if (reader.Valid) {
        foreach (var entry in reader.Entries) {
          if (files is { Length: > 0 } && !MatchesFilter(entry.Name, files)) continue;
          var target = Path.Combine(outputDir, entry.Name.Replace('/', Path.DirectorySeparatorChar));
          Directory.CreateDirectory(Path.GetDirectoryName(target) ?? outputDir);
          using var output = File.Create(target);
          reader.ExtractTo(entry, output);
        }

        WriteIfMatch(outputDir, "metadata.ini", BuildMetadata(sb), files);
        if (sb.Valid) WriteIfMatch(outputDir, "superblock.bin", sb.RawBytes, files);
        return;
      }
    } catch {
      // Fall through to the conservative carver view.
    }

    if (stream.CanSeek) {
      stream.Position = 0;
      using var full = new MemoryStream();
      stream.CopyTo(full);
      WriteIfMatch(outputDir, "FULL.bcachefs", full.ToArray(), files);
    } else {
      WriteIfMatch(outputDir, "FULL.bcachefs", header, files);
    }
    WriteIfMatch(outputDir, "metadata.ini", BuildMetadata(sb), files);
    if (sb.Valid) WriteIfMatch(outputDir, "superblock.bin", sb.RawBytes, files);
  }

    /// <summary>
  /// Performs the open entry operation.
  /// </summary>
public Stream OpenEntry(Stream archive, string entryName, string? password) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(entryName);
    if (archive.CanSeek) archive.Position = 0;

    using var reader = new BcacheFsReader(archive);
    if (reader.Valid) {
      var entry = reader.Entries.FirstOrDefault(e =>
        string.Equals(e.Name, entryName, StringComparison.Ordinal));
      if (entry != null) {
        var scratch = new FileStream(Path.GetTempFileName(), FileMode.Create, FileAccess.ReadWrite,
          FileShare.None, 64 * 1024, FileOptions.DeleteOnClose);
        try {
          reader.ExtractTo(entry, scratch);
          scratch.Position = 0;
          return new BoundedEntryStream(scratch, entry.Size, leaveOpen: false);
        } catch {
          scratch.Dispose();
          throw;
        }
      }
    }

    return new BoundedEntryStream(new MemoryStream([], writable: false), 0, leaveOpen: false);
  }

    /// <summary>
  /// Performs the extract entry to memory operation.
  /// </summary>
public byte[] ExtractEntryToMemory(Stream archive, string entryName, string? password) {
    using var entry = this.OpenEntry(archive, entryName, password);
    using var output = new MemoryStream();
    entry.CopyTo(output);
    return output.ToArray();
  }

    /// <summary>
  /// Performs the create operation.
  /// </summary>
public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    var writer = NewWriter(options);

    var sizes = new List<long>();
    foreach (var input in inputs) {
      if (!this.CanAccept(input, out var reason))
        throw new ArgumentException(reason, nameof(inputs));
      if (input.IsDirectory) continue;

      var length = input.InMemoryContent?.LongLength ?? new FileInfo(input.FullPath).Length;
      sizes.Add(length);
      if (input.InMemoryContent is { } bytes)
        writer.AddFile(input.ArchiveName, bytes);
      else {
        var path = input.FullPath;
        writer.AddStreamingFile(input.ArchiveName, length, () => File.OpenRead(path));
      }
    }

    var requested = FilesystemSchemaPresets.ParseSize(options?.GetOption("ImageSize", ""));
    writer.SetImageSize(Math.Max(requested, BcacheFsWriter.EstimateSize(sizes)));
    WriteVolume(writer, output);
  }

    /// <summary>
  /// Performs the create from streams operation.
  /// </summary>
public void CreateFromStreams(Stream output, IEnumerable<StreamingArchiveInput> inputs,
      FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(inputs);
    var files = inputs.Where(i => !i.IsDirectory).ToList();
    var writer = NewWriter(options);

    foreach (var input in files) {
      var probe = ArchiveInputInfo.InMemory(input.Name, []);
      if (!this.CanAccept(probe, out var reason))
        throw new ArgumentException(reason, nameof(inputs));
      writer.AddStreamingFile(input.Name, input.Size, input.OpenStream);
    }

    var requested = FilesystemSchemaPresets.ParseSize(options?.GetOption("ImageSize", ""));
    writer.SetImageSize(Math.Max(requested,
      BcacheFsWriter.EstimateSize(files.Select(i => i.Size))));
    WriteVolume(writer, output);
  }

  /// <summary>
  /// True in-place add/replace. Unchanged file extents are not copied or relocated;
  /// new bytes go to free buckets and only bcachefs metadata is committed afterwards.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(inputs);
    foreach (var input in inputs)
      if (!this.CanAccept(input, out var reason))
        throw new ArgumentException(reason, nameof(inputs));
    BcacheFsInPlaceModifier.Add(archive, inputs);
    BcacheFsSuperblockEditor.Restamp(archive);
  }

  /// <summary>
  /// True in-place remove/purge. Metadata keys are removed in the metadata zone and
  /// the old user extents are overwritten with zeroes after the new roots are live.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames) {
    ArgumentNullException.ThrowIfNull(archive);
    BcacheFsInPlaceModifier.Remove(archive, entryNames ?? []);
    BcacheFsSuperblockEditor.Restamp(archive);
  }

  /// <summary>
  /// Entries the reader derives rather than reads: the volume's own description,
  /// and the carver's view of an image that cannot be walked as a filesystem.
  /// </summary>
  private static readonly HashSet<string> SyntheticNames =
    new(StringComparer.OrdinalIgnoreCase) { "FULL.bcachefs", "metadata.ini", "superblock.bin" };

  /// <summary>
  /// Rebuilds the volume tightly around its content. The generic default writes
  /// the derived entries back as ordinary files, so the rebuilt volume lists more
  /// than the original did and the round-trip guard refuses it — leaving an
  /// oversized image at its original size.
  /// </summary>
  public void Shrink(Stream input, Stream output) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);

    // a file, not a MemoryStream: a rebuilt volume can outgrow what a byte[] holds
    using var rebuilt = new FileStream(
      Path.Combine(Path.GetTempPath(), "cwb_bch_shrink_" + Guid.NewGuid().ToString("N") + ".tmp"),
      FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 64 * 1024, FileOptions.DeleteOnClose);
    var useRebuilt = false;
    try {
      RebuildVerb.RebuildToStream(input, rebuilt, this, this, null, SyntheticNames);
      useRebuilt = rebuilt.Length > 0 && rebuilt.Length < input.Length;
    } catch {
      useRebuilt = false;
    }

    input.Position = 0;
    output.Position = 0;
    output.SetLength(0);
    if (useRebuilt) {
      rebuilt.Position = 0;
      rebuilt.CopyTo(output);
    } else
      input.CopyTo(output);
  }

    /// <summary>
  /// Performs the defragment operation.
  /// </summary>
public void Defragment(Stream archive)
    => this.Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <summary>
  /// Runs the bcachefs-specific offline relocation engine. It operates from the
  /// physical bucket map, may COW-relocate metadata according to the requested
  /// metadata zone/interleave policy, then moves data around the resulting live
  /// metadata barriers and republishes allocation metadata from the final map.
  /// There is no extract/re-create fallback.
  /// </summary>
  public void Defragment(Stream archive, DefragOptions options) {
    ArgumentNullException.ThrowIfNull(archive);
    ArgumentNullException.ThrowIfNull(options);
    BcacheFsDefragmenter.Defragment(archive, options);
  }

  /// <summary>
  /// bcachefs' allocation unit is fixed for this profile, so optimize means choosing
  /// a better extent placement; there is no fictional smaller bucket size to propose.
  /// </summary>
  public LayoutAnalysis AnalyzeLayout(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    if (image.CanSeek) image.Position = 0;
    using var reader = new BcacheFsReader(image);
    if (!reader.Valid)
      return new LayoutAnalysis { ImageSize = image.CanSeek ? image.Length : 0 };

    var slack = 0L;
    foreach (var entry in reader.Entries) {
      var allocated = entry.Extents.Sum(e => (long)e.Sectors * BcacheFsFormat.SectorSize);
      slack += Math.Max(0, allocated - entry.Size);
    }

    return new LayoutAnalysis {
      ImageSize = reader.Length,
      CurrentUnitSize = BcacheFsFormat.BucketBytes,
      CurrentSlackBytes = slack,
      OptimalUnitSize = BcacheFsFormat.BucketBytes,
      OptimalSlackBytes = slack,
      InPlaceChanges = ["VolumeLabel", "extent placement"],
      Notes = [
        "bcachefs bucket geometry is fixed by this profile; optimize is an in-place extent-layout pass.",
        "Use defragmentation modes/layout profiles to consolidate, tail-pack, fill holes or carve a region without rebuilding the volume.",
      ],
    };
  }

    /// <summary>
  /// Performs the patch in place operation.
  /// </summary>
public void PatchInPlace(Stream image, LayoutPatch patch) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(patch);
    if (patch.SerialNumber != null)
      throw new NotSupportedException("bcachefs has no FAT-style 32-bit serial field.");
    if (patch.Extra is { Count: > 0 })
      throw new NotSupportedException(
        $"Unsupported bcachefs in-place layout fields: {string.Join(", ", patch.Extra.Keys)}.");
    if (patch.VolumeLabel != null)
      BcacheFsSuperblockEditor.SetLabel(image, patch.VolumeLabel);
  }

  /// <summary>
  /// Structural optimize contract. When source and target are the same stream the
  /// operation is genuinely in-place. A distinct target necessarily receives one
  /// copy first, then the exact same in-place optimizer runs on that target.
  /// </summary>
  public void RebuildStreaming(Stream source, Stream target, LayoutRebuildOptions options) {
    ArgumentNullException.ThrowIfNull(source);
    ArgumentNullException.ThrowIfNull(target);
    ArgumentNullException.ThrowIfNull(options);
    if (options.UnitSize is not (0 or BcacheFsFormat.BucketBytes))
      throw new NotSupportedException(
        $"bcachefs bucket size is fixed at {BcacheFsFormat.BucketBytes} bytes in this profile.");
    if (options.MakeSparse || options.DeduplicateWithLinks)
      throw new NotSupportedException("Sparse/reflink optimization is not part of the current writer profile.");

    if (!ReferenceEquals(source, target)) {
      if (!target.CanWrite || !target.CanSeek)
        throw new ArgumentException("bcachefs optimized target must be writable and seekable.", nameof(target));
      source.Position = 0;
      target.Position = 0;
      target.SetLength(0);
      source.CopyTo(target);
      target.Flush();
    }

    if (options.ImageSize > 0 && options.ImageSize != target.Length)
      throw new NotSupportedException(
        "Changing bcachefs device size is a resize operation, not an in-place layout optimization.");

    target.Position = 0;
    this.Defragment(target, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

    if (options.Parameters != null
        && options.Parameters.TryGetValue("VolumeLabel", out var label))
      BcacheFsSuperblockEditor.SetLabel(target, label);
  }

  private void DefragmentWithPlanner(Stream archive, DefragOptions options) {
    archive.Position = 0;
    var mover = new BcacheFsBlockMover();
    mover.Init(archive);

    var extents = this.EnumerateExtents(archive).ToList();
    options.OnProgress?.Invoke(new DefragProgressEvent(
      "scanning", 0, 0, -1, archive.Length, extents, "Analysing layout"));

    var volumeEnd = archive.Length
      - (long)BcacheFsFormat.SbSlotSectors * BcacheFsFormat.SectorSize;
    var moves = Compression.Core.Layout.DefragPlanner.Plan(
      extents, mover.FirstDataByte, volumeEnd, mover.BlockSize,
      options.Profile, options.Mode, holeSize: options.HoleSize, holeAt: options.HoleAt,
      metadataZone: options.MetadataZonePlacement);

    if (moves.Count == 0) {
      options.OnProgress?.Invoke(new DefragProgressEvent(
        "complete", 1, -1, -1, archive.Length, extents, "Already defragmented"));
      return;
    }

    Compression.Core.Layout.DefragPlannerExecutor.Execute(
      archive, options, mover, moves, volumeEnd, reinitAfterMove: null);
    mover.Settle(archive);

    // Extent pointers now name the moved data. Rebuild every dependent metadata
    // account in the metadata reservation, including accounting/backpointers; this
    // is still in-place and removes the old single-node limitation of SettleAllocation.
    archive.Position = 0;
    BcacheFsInPlaceModifier.NormalizeMetadata(archive);
    BcacheFsSuperblockEditor.Restamp(archive);

    archive.Position = 0;
    var post = this.EnumerateExtents(archive).ToList();
    options.OnProgress?.Invoke(new DefragProgressEvent(
      "complete", 1, -1, -1, archive.Length, post, "Defragmentation complete"));
  }

    /// <summary>
  /// Enumerates the extents.
  /// </summary>
public IEnumerable<DefragBlockInfo> EnumerateExtents(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    var result = new List<DefragBlockInfo>();
    try {
      if (image.CanSeek) image.Position = 0;
      using var reader = new BcacheFsReader(image);
      if (!reader.Valid) return [];

      var files = new List<DefragBlockInfo>();
      foreach (var entry in reader.Entries)
        foreach (var extent in entry.Extents)
          files.Add(new DefragBlockInfo(
            extent.FirstSector * BcacheFsFormat.SectorSize,
            (long)extent.Sectors * BcacheFsFormat.SectorSize,
            DefragBlockKind.Used, entry.Name));

      result.Add(new DefragBlockInfo(0, BcacheFsFormat.MetadataEndBytes,
        DefragBlockKind.MetadataReserved,
        "Superblock slots, journal and reserved b-tree metadata zone"));
      files.Sort((a, b) => a.Offset.CompareTo(b.Offset));
      result.AddRange(files);

      var tail = reader.Length
        - (long)BcacheFsFormat.SbSlotSectors * BcacheFsFormat.SectorSize;
      if (tail > BcacheFsFormat.MetadataEndBytes)
        result.Add(new DefragBlockInfo(tail, reader.Length - tail,
          DefragBlockKind.MetadataReserved, "Trailing superblock copy"));
    } catch {
      return [];
    }
    return result;
  }

  /// <summary>
  /// Wipe/clean support: zero every unused byte plus optional final-extent slack,
  /// while preserving every live file and the image size.
  /// </summary>
  public long WipeUnusedSpace(Stream image, bool wipeClusterTips = true,
      bool wipeDeletedEntries = true) {
    ArgumentNullException.ThrowIfNull(image);
    var extents = this.EnumerateExtents(image).ToList();
    if (extents.Count == 0) return 0;
    _ = wipeDeletedEntries;

    var wiped = UnusedSpaceWiper.Wipe(image, extents, image.Length,
      wipeClusterTips: false, fileSizeLookup: null);
    if (!wipeClusterTips) return wiped;

    if (image.CanSeek) image.Position = 0;
    using var reader = new BcacheFsReader(image);
    if (!reader.Valid) return wiped;

    var zeros = new byte[64 * 1024];
    foreach (var entry in reader.Entries) {
      var remaining = entry.Size;
      foreach (var extent in entry.Extents) {
        var allocated = (long)extent.Sectors * BcacheFsFormat.SectorSize;
        var live = Math.Min(remaining, allocated);
        remaining -= live;
        if (remaining != 0 || live >= allocated) continue;
        wiped += ZeroNonZeroRange(image,
          extent.FirstSector * BcacheFsFormat.SectorSize + live,
          allocated - live, zeros);
      }
    }

    image.Flush();
    return wiped;
  }

  private static BcacheFsWriter NewWriter(FormatCreateOptions? options) {
    var writer = new BcacheFsWriter();
    var label = options?.GetOption("VolumeLabel", "") ?? "";
    if (!string.IsNullOrEmpty(label)) writer.SetLabel(label);
    return writer;
  }

  private static void WriteVolume(BcacheFsWriter writer, Stream output) {
    if (!output.CanWrite)
      throw new ArgumentException("Writing a bcachefs volume needs a writable stream.", nameof(output));
    if (output.CanSeek) {
      writer.WriteTo(output);
      return;
    }

    var path = Path.GetTempFileName();
    try {
      using var scratch = new FileStream(path, FileMode.Create, FileAccess.ReadWrite,
        FileShare.None, 64 * 1024, FileOptions.SequentialScan);
      writer.WriteTo(scratch);
      scratch.Position = 0;
      scratch.CopyTo(output);
      output.Flush();
    } finally {
      try { File.Delete(path); } catch { }
    }
  }

  private static void WriteIfMatch(string outputDir, string name, byte[] data, string[]? filter) {
    if (filter is { Length: > 0 } && !MatchesFilter(name, filter)) return;
    WriteFile(outputDir, name, data);
  }

  private static byte[] BuildMetadata(BcacheFsSuperblock sb) {
    var builder = new StringBuilder();
    builder.Append(CultureInfo.InvariantCulture, $"parse_status={(sb.Valid ? "ok" : "partial")}\n");
    builder.Append(CultureInfo.InvariantCulture, $"uuid={sb.Uuid}\n");
    builder.Append(CultureInfo.InvariantCulture, $"user_uuid={sb.UserUuid}\n");
    builder.Append(CultureInfo.InvariantCulture, $"label={sb.Label}\n");
    builder.Append(CultureInfo.InvariantCulture, $"version={sb.FormatVersion()}\n");
    builder.Append(CultureInfo.InvariantCulture, $"version_raw={sb.Version}\n");
    builder.Append(CultureInfo.InvariantCulture, $"version_min_raw={sb.VersionMin}\n");
    builder.Append(CultureInfo.InvariantCulture, $"block_size={sb.BlockSize}\n");
    builder.Append(CultureInfo.InvariantCulture, $"dev_idx={sb.DevIdx}\n");
    builder.Append(CultureInfo.InvariantCulture, $"nr_devices={sb.NrDevices}\n");
    builder.Append(CultureInfo.InvariantCulture, $"u64s={sb.U64s}\n");
    builder.Append(CultureInfo.InvariantCulture, $"offset={sb.Offset}\n");
    builder.Append(CultureInfo.InvariantCulture, $"seq={sb.Seq}\n");
    return Encoding.UTF8.GetBytes(builder.ToString());
  }

  private const int HeaderReadCap = 64 * 1024;

  private static byte[] ReadHeader(Stream stream) {
    if (stream.CanSeek) stream.Position = 0;
    using var buffer = new MemoryStream();
    var chunk = new byte[8192];
    int read;
    while (buffer.Length < HeaderReadCap
        && (read = stream.Read(chunk, 0, chunk.Length)) > 0)
      buffer.Write(chunk, 0, read);
    return buffer.ToArray();
  }

  private static long ZeroNonZeroRange(Stream stream, long offset, long length, byte[] zeros) {
    if (length <= 0) return 0;
    var readBuffer = new byte[zeros.Length];
    var remaining = length;
    var changed = 0L;
    stream.Position = offset;
    while (remaining > 0) {
      var chunk = (int)Math.Min(readBuffer.Length, remaining);
      var read = stream.Read(readBuffer, 0, chunk);
      if (read == 0) break;
      var nonZero = false;
      for (var i = 0; i < read; ++i)
        if (readBuffer[i] != 0) { nonZero = true; break; }
      if (nonZero) {
        stream.Position -= read;
        stream.Write(zeros, 0, read);
        changed += read;
      }
      remaining -= read;
    }
    return changed;
  }
}
