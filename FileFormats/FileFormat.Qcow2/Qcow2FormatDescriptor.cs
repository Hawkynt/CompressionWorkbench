#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Qcow2;

/// <summary>
/// QEMU Copy-On-Write v2/v3 (qcow2) disk image — two-level L1/L2 cluster-mapped sparse virtual disk.
///
/// References:
/// <list type="bullet">
///   <item><description><c>docs/interop/qcow2.rst</c> in the QEMU source tree — the authoritative on-disk specification</description></item>
///   <item><description><c>https://gitlab.com/qemu-project/qemu</c> — canonical QEMU repository</description></item>
///   <item><description><c>https://en.wikipedia.org/wiki/Qcow</c> — Wikipedia overview</description></item>
/// </list>
/// </summary>
public sealed class Qcow2FormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveModifiable, IArchiveDefragmentable, IArchiveLayoutMap, IFilesystemExtentMap, IPartitionEditable {
  public string Id => "Qcow2";
  public string DisplayName => "QCOW2";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest | FormatCapabilities.CanModify |
    FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".qcow2";
  public IReadOnlyList<string> Extensions => [".qcow2", ".qcow"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures =>
    [new([0x51, 0x46, 0x49, 0xFB], Confidence: 0.95)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("qcow2", "QCOW2")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "QEMU Copy-On-Write disk image";

  // ── IArchiveFormatOperations ──────────────────────────────────────

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    if (Qcow2Stream.TryOpen(stream) is { } qStream) {
      using (qStream) {
        qStream.Position = 0;
        if (Compression.Core.DiskImage.PartitionedDiskLister.List(qStream, password) is { } partitioned)
          return partitioned;

        var inner = InnerFsDetector.Detect(qStream);
        if (inner is IArchiveFormatOperations ops) {
          try {
            qStream.Position = 0;
            return ops.List(qStream, password);
          } catch {
            // fall through to raw listing
          }
        }
      }
    }

    stream.Position = 0;
    var r = new Qcow2Reader(stream);
    return [new ArchiveEntryInfo(0, "disk.img", r.VirtualSize, stream.Length, "QCOW2", false, false, null)];
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    if (Qcow2Stream.TryOpen(stream) is { } qStream) {
      using (qStream) {
        qStream.Position = 0;
        if (Compression.Core.DiskImage.PartitionedDiskLister.Extract(qStream, outputDir, password, files))
          return;

        var inner = InnerFsDetector.Detect(qStream);
        if (inner is IArchiveFormatOperations ops) {
          try {
            qStream.Position = 0;
            ops.Extract(qStream, outputDir, password, files);
            return;
          } catch {
            // fall through to raw extraction
          }
        }
      }
    }

    stream.Position = 0;
    var r = new Qcow2Reader(stream);
    WriteFile(outputDir, "disk.img", r.ExtractDisk());
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var fatImage = FileSystem.Fat.FatWriter.BuildFromFiles(FlatFiles(inputs));
    var w = new Qcow2Writer();
    w.SetDiskImage(fatImage);
    w.WriteTo(output);
  }

  // ── IArchiveLayoutMap ───────────────────────────────────────────────

  /// <inheritdoc />
  public IEnumerable<DefragBlockInfo> EnumerateLayout(Stream archive) => Qcow2LayoutMap.Enumerate(archive);

  // ── IFilesystemExtentMap ────────────────────────────────────────────

  /// <inheritdoc />
  public IEnumerable<DefragBlockInfo> EnumerateExtents(Stream image) {
    if (Qcow2Stream.TryOpen(image) is { } qStream) {
      using (qStream) {
        var inner = InnerFsDetector.Detect(qStream);
        if (inner is IFilesystemExtentMap extentMap) {
          qStream.Position = 0;
          return extentMap.EnumerateExtents(qStream).ToList();
        }
      }
    }

    return Qcow2LayoutMap.Enumerate(image);
  }

  // ── IArchiveModifiable (inner-FS-aware) ────────────────────────────

  /// <inheritdoc />
  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    if (Qcow2Stream.TryOpen(archive) is { } guestForPart) {
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

    if (TryDelegateModifiable(archive, out var qStream, out var modifiable) && qStream is not null && modifiable is not null) {
      using (qStream) {
        try {
          qStream.Position = 0;
          modifiable.Add(qStream, inputs);
          qStream.Flush();
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
    if (Qcow2Stream.TryOpen(archive) is { } guestForPart) {
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

    if (TryDelegateModifiable(archive, out var qStream, out var modifiable) && qStream is not null && modifiable is not null) {
      using (qStream) {
        try {
          qStream.Position = 0;
          modifiable.Remove(qStream, entryNames);
          qStream.Flush();
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
    if (Qcow2Stream.TryOpen(archive) is { } qStream) {
      using (qStream) {
        var inner = InnerFsDetector.Detect(qStream);
        if (inner is IArchiveDefragmentable defrag) {
          try {
            qStream.Position = 0;
            defrag.Defragment(qStream, options);
            qStream.Flush();
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

  private static bool TryDelegateModifiable(Stream archive, out Qcow2Stream? qStream, out IArchiveModifiable? modifiable) {
    qStream = null;
    modifiable = null;
    var qs = Qcow2Stream.TryOpen(archive);
    if (qs == null) return false;

    var inner = InnerFsDetector.Detect(qs);
    if (inner is IArchiveModifiable mod) {
      qStream = qs;
      modifiable = mod;
      return true;
    }

    qs.Dispose();
    return false;
  }

  // ── Rebuild-path delegates (fallback) ──────────────────────────────

  private static IEnumerable<(string Name, byte[] Data)> ReadDiskEntries(Stream stream) {
    stream.Position = 0;
    var r = new Qcow2Reader(stream);
    yield return ("disk.img", r.ExtractDisk());
  }

  private static byte[] BuildImage(IReadOnlyList<(string Name, byte[] Data)> files) {
    var diskData = files.Count > 0 ? files[0].Data : [];
    var w = new Qcow2Writer();
    w.SetDiskImage(diskData);
    using var ms = new MemoryStream();
    w.WriteTo(ms);
    return ms.ToArray();
  }

  // ── IPartitionEditable ─────────────────────────────────────────────

  /// <inheritdoc />
  /// <remarks>
  /// QCOW2 uses a 2-level L1/L2 cluster table; partition-editor writes will
  /// allocate new clusters on demand via <see cref="Qcow2Stream"/>. Note
  /// that QCOW2 snapshot chains, encrypted images, and backing files are
  /// <em>not</em> handled here — only flat images writable through the
  /// stream wrapper. Throws <see cref="NotSupportedException"/> if the
  /// stream is read-only or if the QCOW2 layout cannot be opened.
  /// </remarks>
  public Stream OpenGuestDiskStream(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    if (!image.CanWrite)
      throw new NotSupportedException("Partition editing requires a writable QCOW2 stream.");
    return Qcow2Stream.TryOpen(image)
      ?? throw new InvalidDataException("Stream is not a valid QCOW2 image.");
  }
}
