#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Vdi;

/// <summary>
/// VirtualBox VDI virtual disk image — block-mapped sparse/fixed disk container.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://www.virtualbox.org/</c> — VirtualBox — the VDI layout is defined by its open-source Storage/VDI code</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/VDI_(file_format)</c> — Wikipedia overview</description></item>
/// </list>
/// </summary>
public sealed class VdiFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveModifiable, IArchiveDefragmentable, IArchiveLayoutMap, IFilesystemExtentMap, IPartitionEditable {
  /// <summary>
  /// Gets the id.
  /// </summary>
public string Id => "Vdi";
  /// <summary>
  /// Gets the display name.
  /// </summary>
public string DisplayName => "VDI";
  /// <summary>
  /// Gets the category.
  /// </summary>
public FormatCategory Category => FormatCategory.Archive;
  /// <summary>
  /// Gets the capabilities.
  /// </summary>
public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest | FormatCapabilities.CanModify |
    FormatCapabilities.SupportsMultipleEntries;
  /// <summary>
  /// Gets the default extension.
  /// </summary>
public string DefaultExtension => ".vdi";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
public IReadOnlyList<string> Extensions => [".vdi"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
public IReadOnlyList<MagicSignature> MagicSignatures => [
    new([0x7F, 0x10, 0xDA, 0xBE], Offset: 64, Confidence: 0.95)
  ];
  /// <summary>
  /// Gets the methods.
  /// </summary>
public IReadOnlyList<FormatMethodInfo> Methods => [new("vdi", "VDI")];
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
public string Description => "VirtualBox disk image";

  // ── IArchiveFormatOperations ──────────────────────────────────────

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    if (VdiStream.TryOpen(stream) is { } vdiStream) {
      using (vdiStream) {
        vdiStream.Position = 0;
        if (Compression.Core.DiskImage.PartitionedDiskLister.List(vdiStream, password) is { } partitioned)
          return partitioned;

        var inner = InnerFsDetector.Detect(vdiStream);
        if (inner is IArchiveFormatOperations ops) {
          try {
            vdiStream.Position = 0;
            return ops.List(vdiStream, password);
          } catch {
            // fall through to raw listing
          }
        }
      }
    }

    stream.Position = 0;
    var r = new VdiReader(stream);
    return [new ArchiveEntryInfo(0, "disk.img", r.VirtualSize, stream.Length, "VDI", false, false, null)];
  }

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    if (VdiStream.TryOpen(stream) is { } vdiStream) {
      using (vdiStream) {
        vdiStream.Position = 0;
        if (Compression.Core.DiskImage.PartitionedDiskLister.Extract(vdiStream, outputDir, password, files))
          return;

        var inner = InnerFsDetector.Detect(vdiStream);
        if (inner is IArchiveFormatOperations ops) {
          try {
            vdiStream.Position = 0;
            ops.Extract(vdiStream, outputDir, password, files);
            return;
          } catch {
            // fall through to raw extraction
          }
        }
      }
    }

    stream.Position = 0;
    var r = new VdiReader(stream);
    WriteFile(outputDir, "disk.img", r.ExtractDisk());
  }

  /// <summary>
  /// Performs the create operation.
  /// </summary>
public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var fatImage = FileSystem.Fat.FatWriter.BuildFromFiles(FlatFiles(inputs));
    using var w = new VdiWriter(output, leaveOpen: true, virtualSize: fatImage.Length);
    w.Write(fatImage);
  }

  // ── IArchiveLayoutMap ───────────────────────────────────────────────

  /// <inheritdoc />
  /// <summary>
  /// Enumerates the layout.
  /// </summary>
public IEnumerable<DefragBlockInfo> EnumerateLayout(Stream archive) => VdiLayoutMap.Enumerate(archive);

  // ── IFilesystemExtentMap ────────────────────────────────────────────

  /// <inheritdoc />
  /// <summary>
  /// Enumerates the extents.
  /// </summary>
public IEnumerable<DefragBlockInfo> EnumerateExtents(Stream image) {
    if (VdiStream.TryOpen(image) is { } vdiStream) {
      using (vdiStream) {
        var inner = InnerFsDetector.Detect(vdiStream);
        if (inner is IFilesystemExtentMap extentMap) {
          vdiStream.Position = 0;
          return extentMap.EnumerateExtents(vdiStream).ToList();
        }
      }
    }

    return VdiLayoutMap.Enumerate(image);
  }

  // ── IArchiveModifiable (inner-FS-aware) ────────────────────────────

  /// <inheritdoc />
  /// <summary>
  /// Adds the supplied entry to the target container.
  /// </summary>
public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    if (VdiStream.TryOpen(archive) is { } guestForPart) {
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

    if (TryDelegateModifiable(archive, out var vdiStream, out var modifiable) && vdiStream is not null && modifiable is not null) {
      using (vdiStream) {
        try {
          vdiStream.Position = 0;
          modifiable.Add(vdiStream, inputs);
          vdiStream.Flush();
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
    if (VdiStream.TryOpen(archive) is { } guestForPart) {
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

    if (TryDelegateModifiable(archive, out var vdiStream, out var modifiable) && vdiStream is not null && modifiable is not null) {
      using (vdiStream) {
        try {
          vdiStream.Position = 0;
          modifiable.Remove(vdiStream, entryNames);
          vdiStream.Flush();
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
    if (VdiStream.TryOpen(archive) is { } vdiStream) {
      using (vdiStream) {
        var inner = InnerFsDetector.Detect(vdiStream);
        if (inner is IArchiveDefragmentable defrag) {
          try {
            vdiStream.Position = 0;
            defrag.Defragment(vdiStream, options);
            vdiStream.Flush();
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

  private static bool TryDelegateModifiable(Stream archive, out VdiStream? vdiStream, out IArchiveModifiable? modifiable) {
    vdiStream = null;
    modifiable = null;
    var vs = VdiStream.TryOpen(archive);
    if (vs == null) return false;

    var inner = InnerFsDetector.Detect(vs);
    if (inner is IArchiveModifiable mod) {
      vdiStream = vs;
      modifiable = mod;
      return true;
    }

    vs.Dispose();
    return false;
  }

  // ── Rebuild-path delegates (fallback) ──────────────────────────────

  private static IEnumerable<(string Name, byte[] Data)> ReadDiskEntries(Stream stream) {
    stream.Position = 0;
    var r = new VdiReader(stream);
    yield return ("disk.img", r.ExtractDisk());
  }

  private static byte[] BuildImage(IReadOnlyList<(string Name, byte[] Data)> files) {
    var diskData = files.Count > 0 ? files[0].Data : [];
    using var ms = new MemoryStream();
    using var w = new VdiWriter(ms, leaveOpen: true, virtualSize: diskData.Length);
    w.Write(diskData);
    return ms.ToArray();
  }

  // ── IPartitionEditable ─────────────────────────────────────────────

  /// <inheritdoc />
  /// <remarks>
  /// VDI uses a block allocation table at <c>blocks_offset</c> with sparse
  /// growth. Partition edits within already-allocated blocks pass through
  /// to the backing stream; edits in sparse holes allocate new blocks via
  /// <see cref="VdiStream"/>.
  /// </remarks>
  /// <summary>
  /// Performs the open guest disk stream operation.
  /// </summary>
public Stream OpenGuestDiskStream(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    if (!image.CanWrite)
      throw new NotSupportedException("Partition editing requires a writable VDI stream.");
    return VdiStream.TryOpen(image)
      ?? throw new InvalidDataException("Stream is not a valid VDI image.");
  }
}
