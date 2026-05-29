#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Vmdk;

public sealed class VmdkFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveModifiable, IArchiveDefragmentable, IArchiveLayoutMap, IFilesystemExtentMap, IPartitionEditable {
  public string Id => "Vmdk";
  public string DisplayName => "VMDK";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract |
    FormatCapabilities.CanTest | FormatCapabilities.CanCreate |
    FormatCapabilities.CanModify |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".vmdk";
  public IReadOnlyList<string> Extensions => [".vmdk"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures =>
    [new([0x4B, 0x44, 0x4D, 0x56], Offset: 0, Confidence: 0.95)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("stored", "Stored")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "VMware virtual disk";

  // ── IArchiveFormatOperations ──────────────────────────────────────

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    if (VmdkStream.TryOpen(stream) is { } vmdkStream) {
      using (vmdkStream) {
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

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var fatImage = FileSystem.Fat.FatWriter.BuildFromFiles(FlatFiles(inputs));
    var w = new VmdkWriter();
    w.SetDiskData(fatImage);
    output.Write(w.Build());
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    if (VmdkStream.TryOpen(stream) is { } vmdkStream) {
      using (vmdkStream) {
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
  public IEnumerable<DefragBlockInfo> EnumerateLayout(Stream archive) => VmdkLayoutMap.Enumerate(archive);

  // ── IFilesystemExtentMap ────────────────────────────────────────────

  /// <inheritdoc />
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
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
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
  public void Remove(Stream archive, string[] entryNames) {
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
  public void Defragment(Stream archive)
    => Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  /// <inheritdoc />
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
  public Stream OpenGuestDiskStream(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    if (!image.CanWrite)
      throw new NotSupportedException("Partition editing requires a writable VMDK stream.");
    return VmdkStream.TryOpen(image)
      ?? throw new InvalidDataException("Stream is not a valid VMDK image.");
  }
}
