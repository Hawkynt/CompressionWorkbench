#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Vmdk;

/// <summary>
/// VMware VMDK virtual disk (sparse extents with grain directories/tables).
///
/// References:
/// <list type="bullet">
///   <item><description>VMware, "Virtual Disk Format 5.0" technical note — the vendor VMDK specification</description></item>
///   <item><description><c>https://github.com/libyal/libvmdk</c> — libvmdk — open implementation with format documentation</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/VMDK</c> — Wikipedia overview</description></item>
/// </list>
/// </summary>
public sealed class VmdkFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveModifiable, IArchiveDefragmentable, IArchiveLayoutMap, IFilesystemExtentMap, IPartitionEditable {
  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "Vmdk";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "VMDK";
  /// <summary>
  /// Gets the category.
  /// </summary>
  public FormatCategory Category => FormatCategory.Archive;
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.CanTest | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify |
    FormatCapabilities.SupportsMultipleEntries;
  /// <summary>
  /// Gets the default extension.
  /// </summary>
  public string DefaultExtension => ".vmdk";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".vmdk"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures =>
    [new([0x4B, 0x44, 0x4D, 0x56], Offset: 0, Confidence: 0.95)];
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
  public string Description => "VMware virtual disk";

  // ── IArchiveFormatOperations ──────────────────────────────────────

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    if (VmdkStream.TryOpen(stream) is { } vmdkStream) {
      using (vmdkStream) {
        vmdkStream.Position = 0;
        if (Compression.Core.DiskImage.PartitionedDiskLister.List(vmdkStream, password) is { } partitioned)
          return partitioned;

        var inner = InnerFsDetector.Detect(vmdkStream);
        if (inner is IArchiveFormatOperations ops) {
          try {
            vmdkStream.Position = 0;
            return ops.List(vmdkStream, password);
          } catch {
            // fall through to raw listing
          }
        }
      }
    }

    stream.Position = 0;
    var r = new VmdkReader(stream);
    return r.Entries.Select((e, i) => new ArchiveEntryInfo(
      i, e.Name, e.Size, e.Size, "Stored", e.IsDirectory, false, null
    )).ToList();
  }

  /// <summary>
  /// Performs the create operation.
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var fatImage = FileSystem.Fat.FatWriter.BuildFromFiles(FlatFiles(inputs));
    var w = new VmdkWriter();
    w.SetDiskData(fatImage);
    output.Write(w.Build());
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    if (VmdkStream.TryOpen(stream) is { } vmdkStream) {
      using (vmdkStream) {
        vmdkStream.Position = 0;
        if (Compression.Core.DiskImage.PartitionedDiskLister.Extract(vmdkStream, outputDir, password, files))
          return;

        var inner = InnerFsDetector.Detect(vmdkStream);
        if (inner is IArchiveFormatOperations ops) {
          try {
            vmdkStream.Position = 0;
            ops.Extract(vmdkStream, outputDir, password, files);
            return;
          } catch {
            // fall through to raw extraction
          }
        }
      }
    }

    stream.Position = 0;
    var r = new VmdkReader(stream);
    foreach (var e in r.Entries) {
      if (files != null && !MatchesFilter(e.Name, files)) continue;
      WriteFile(outputDir, e.Name, r.Extract(e));
    }
  }

  // ── IArchiveLayoutMap ───────────────────────────────────────────────

  /// <inheritdoc />
  /// <summary>
  /// Enumerates the layout.
  /// </summary>
  public IEnumerable<DefragBlockInfo> EnumerateLayout(Stream archive) => VmdkLayoutMap.Enumerate(archive);

  // ── IFilesystemExtentMap ────────────────────────────────────────────

  /// <inheritdoc />
  /// <summary>
  /// Enumerates the extents.
  /// </summary>
  public IEnumerable<DefragBlockInfo> EnumerateExtents(Stream image) {
    if (VmdkStream.TryOpen(image) is { } vmdkStream) {
      using (vmdkStream) {
        var inner = InnerFsDetector.Detect(vmdkStream);
        if (inner is IFilesystemExtentMap extentMap) {
          vmdkStream.Position = 0;
          return extentMap.EnumerateExtents(vmdkStream).ToList();
        }
      }
    }

    return VmdkLayoutMap.Enumerate(image);
  }

  // ── IArchiveModifiable (inner-FS-aware) ────────────────────────────

  /// <inheritdoc />
  /// <summary>
  /// Adds the supplied entry to the target container.
  /// </summary>
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    if (VmdkStream.TryOpen(archive) is { } guestForPart) {
      using (guestForPart) {
        try {
          guestForPart.Position = 0;
          if (Compression.Core.DiskImage.PartitionedDiskLister.TryAdd(guestForPart, inputs)) {
            guestForPart.Flush();
            return;
          }
        } catch (InvalidOperationException) { throw; }
        catch { /* fall through */ }
      }
    }

    if (TryDelegateModifiable(archive, out var vmdkStream, out var modifiable) && vmdkStream is not null && modifiable is not null) {
      using (vmdkStream) {
        try {
          vmdkStream.Position = 0;
          modifiable.Add(vmdkStream, inputs);
          vmdkStream.Flush();
          return;
        } catch {
          // fall through to rebuild
        }
      }
    }

    ModifyRebuilder.Add(archive, inputs, ReadDiskEntries, BuildImage);
  }

  /// <inheritdoc />
  /// <summary>
  /// Removes the specified entry from the target container.
  /// </summary>
  public void Remove(Stream archive, string[] entryNames) {
    if (VmdkStream.TryOpen(archive) is { } guestForPart) {
      using (guestForPart) {
        try {
          guestForPart.Position = 0;
          if (Compression.Core.DiskImage.PartitionedDiskLister.TryRemove(guestForPart, entryNames)) {
            guestForPart.Flush();
            return;
          }
        } catch (InvalidOperationException) { throw; }
        catch { /* fall through */ }
      }
    }

    if (TryDelegateModifiable(archive, out var vmdkStream, out var modifiable) && vmdkStream is not null && modifiable is not null) {
      using (vmdkStream) {
        try {
          vmdkStream.Position = 0;
          modifiable.Remove(vmdkStream, entryNames);
          vmdkStream.Flush();
          return;
        } catch {
          // fall through to rebuild
        }
      }
    }

    ModifyRebuilder.Remove(archive, entryNames, ReadDiskEntries, BuildImage);
  }

  // ── IArchiveDefragmentable (inner-FS-aware) ────────────────────────

  /// <inheritdoc />
  /// <summary>
  /// Performs the defragment operation.
  /// </summary>
  public void Defragment(Stream archive)
    => Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <inheritdoc />
  /// <summary>
  /// Performs the defragment operation.
  /// </summary>
  public void Defragment(Stream archive, DefragOptions options) {
    if (VmdkStream.TryOpen(archive) is { } vmdkStream) {
      using (vmdkStream) {
        var inner = InnerFsDetector.Detect(vmdkStream);
        if (inner is IArchiveDefragmentable defrag) {
          try {
            vmdkStream.Position = 0;
            defrag.Defragment(vmdkStream, options);
            vmdkStream.Flush();
            return;
          } catch {
            // fall through to rebuild
          }
        }
      }
    }

    DefragRebuilder.Rebuild(archive, options, ReadDiskEntries, BuildImage);
  }

  // ── Private helpers ────────────────────────────────────────────────

  private static bool TryDelegateModifiable(Stream archive, out VmdkStream? vmdkStream, out IArchiveModifiable? modifiable) {
    vmdkStream = null;
    modifiable = null;
    var vs = VmdkStream.TryOpen(archive);
    if (vs == null) return false;

    var inner = InnerFsDetector.Detect(vs);
    if (inner is IArchiveModifiable mod) {
      vmdkStream = vs;
      modifiable = mod;
      return true;
    }

    vs.Dispose();
    return false;
  }

  // ── Rebuild-path delegates (fallback) ──────────────────────────────

  private static IEnumerable<(string Name, byte[] Data)> ReadDiskEntries(Stream stream) {
    var r = new VmdkReader(stream);
    foreach (var e in r.Entries) {
      if (e.IsDirectory) continue;
      yield return (e.Name, r.Extract(e));
    }
  }

  private static byte[] BuildImage(IReadOnlyList<(string Name, byte[] Data)> files) {
    var diskData = files.Count > 0 ? files[0].Data : [];
    var w = new VmdkWriter();
    w.SetDiskData(diskData);
    return w.Build();
  }

  // ── IPartitionEditable ─────────────────────────────────────────────

  /// <inheritdoc />
  /// <remarks>
  /// Returns a <see cref="VmdkStream"/> over the monolithic-sparse grain
  /// table. Partition edits within already-allocated grains succeed
  /// directly; edits to unallocated regions allocate new grains on demand.
  /// </remarks>
  /// <summary>
  /// Performs the open guest disk stream operation.
  /// </summary>
  public Stream OpenGuestDiskStream(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    if (!image.CanWrite)
      throw new NotSupportedException("Partition editing requires a writable VMDK stream.");
    return VmdkStream.TryOpen(image)
      ?? throw new InvalidDataException("Stream is not a valid VMDK image.");
  }
}
