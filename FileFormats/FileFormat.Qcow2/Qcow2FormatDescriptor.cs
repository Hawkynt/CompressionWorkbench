#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Qcow2;

/// <summary>
/// QEMU Copy-On-Write v2/v3 (qcow2) disk image — two-level L1/L2 cluster-mapped sparse virtual disk.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://www.qemu.org/docs/master/interop/qcow2.html</c> — authoritative QCOW2 on-disk specification</description></item>
///   <item><description><c>https://www.qemu.org/docs/master/tools/qemu-img.html</c> — qemu-img maintenance behavior</description></item>
/// </list>
/// </summary>
public sealed class Qcow2FormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveModifiable, IArchiveDefragmentable, IArchiveShrinkable, IArchivePurgeable, IArchiveLayoutMap, IFilesystemExtentMap, IPartitionEditable {
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
    using var reader = new Qcow2Reader(stream);
    return [new ArchiveEntryInfo(0, "disk.img", reader.VirtualSize, stream.Length, "QCOW2", false, false, null)];
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
    using var reader = new Qcow2Reader(stream);
    WriteFile(outputDir, "disk.img", reader.ExtractDisk());
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    var fatImage = FileSystem.Fat.FatWriter.BuildFromFiles(FlatFiles(inputs));
    var writer = new Qcow2Writer();
    writer.SetDiskImage(fatImage);
    writer.WriteTo(output);
  }

  // ── Layout / wipe ─────────────────────────────────────────────────

  public IEnumerable<DefragBlockInfo> EnumerateLayout(Stream archive) => Qcow2LayoutMap.Enumerate(archive);

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

  public void Add(Stream archive, IReadOnlyList<ArchiveInputInfo> inputs) {
    if (Qcow2Stream.TryOpen(archive) is { } guestForPart) {
      using (guestForPart) {
        if (guestForPart.CanWrite) {
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
    }

    if (TryDelegateModifiable(archive, out var qStream, out var modifiable) && qStream is not null && modifiable is not null) {
      using (qStream) {
        try {
          qStream.Position = 0;
          modifiable.Add(qStream, inputs);
          qStream.Flush();
          return;
        } catch {
          // fall through to verified rebuild
        }
      }
    }

    EnsureCanonicalRebuildSafe(archive);
    ModifyRebuilder.Add(archive, inputs, ReadDiskEntries, BuildImage);
  }

  public void Remove(Stream archive, string[] entryNames) {
    if (Qcow2Stream.TryOpen(archive) is { } guestForPart) {
      using (guestForPart) {
        if (guestForPart.CanWrite) {
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
    }

    if (TryDelegateModifiable(archive, out var qStream, out var modifiable) && qStream is not null && modifiable is not null) {
      using (qStream) {
        try {
          qStream.Position = 0;
          modifiable.Remove(qStream, entryNames);
          qStream.Flush();
          return;
        } catch {
          // fall through to verified rebuild
        }
      }
    }

    EnsureCanonicalRebuildSafe(archive);
    ModifyRebuilder.Remove(archive, entryNames, ReadDiskEntries, BuildImage);
  }

  // ── Defrag / shrink / compact ─────────────────────────────────────

  public void Defragment(Stream archive)
    => Defragment(archive, new DefragOptions { Mode = DefragMode.ConsolidateAtStart });

  public void Defragment(Stream archive, DefragOptions options) {
    if (Qcow2Stream.TryOpen(archive) is { } qStream) {
      using (qStream) {
        if (qStream.CanWrite) {
          var inner = InnerFsDetector.Detect(qStream);
          if (inner is IArchiveDefragmentable defrag) {
            try {
              qStream.Position = 0;
              defrag.Defragment(qStream, options);
              qStream.Flush();
              return;
            } catch {
              // fall through to verified rebuild
            }
          }
        }
      }
    }

    EnsureCanonicalRebuildSafe(archive);
    DefragRebuilder.Rebuild(archive, options, ReadDiskEntries, BuildImage);
  }

  public void Shrink(Stream input, Stream output)
    => RawDiskShrinkRebuilder.Shrink(
      input,
      output,
      static stream => {
        using var reader = new Qcow2Reader(stream);
        return reader.ExtractDisk();
      },
      static disk => {
        var writer = new Qcow2Writer();
        writer.SetDiskImage(disk);
        using var compact = new MemoryStream();
        writer.WriteTo(compact);
        return compact.ToArray();
      },
      static stream => IsCanonicalRebuildSafe(stream));

  // IArchivePurgeable uses the repository's transactional default, backed by
  // this descriptor's List + Remove implementation.

  // ── Private helpers ────────────────────────────────────────────────

  private static bool TryDelegateModifiable(Stream archive, out Qcow2Stream? qStream, out IArchiveModifiable? modifiable) {
    qStream = null;
    modifiable = null;
    var candidate = Qcow2Stream.TryOpen(archive);
    if (candidate is null)
      return false;
    if (!candidate.CanWrite) {
      candidate.Dispose();
      return false;
    }

    var inner = InnerFsDetector.Detect(candidate);
    if (inner is IArchiveModifiable mod) {
      qStream = candidate;
      modifiable = mod;
      return true;
    }

    candidate.Dispose();
    return false;
  }

  private static bool IsCanonicalRebuildSafe(Stream image) {
    try {
      var header = Qcow2Structures.ReadHeader(image);
      Qcow2Structures.ValidateReadableProfile(header);
      return header.SnapshotCount == 0
          && header.SnapshotsOffset == 0
          && header.CompatibleFeatures == 0
          && header.AutoclearFeatures == 0
          && header.VirtualSize <= int.MaxValue;
    } catch {
      return false;
    }
  }

  private static void EnsureCanonicalRebuildSafe(Stream image) {
    if (!IsCanonicalRebuildSafe(image))
      throw new NotSupportedException(
        "QCOW2 rebuild maintenance is limited to self-contained images without snapshots, " +
        "compatible/autoclear feature metadata, backing files, encryption, or incompatible v3 features.");
  }

  private static IEnumerable<(string Name, byte[] Data)> ReadDiskEntries(Stream stream) {
    stream.Position = 0;
    using var reader = new Qcow2Reader(stream);
    yield return ("disk.img", reader.ExtractDisk());
  }

  private static byte[] BuildImage(IReadOnlyList<(string Name, byte[] Data)> files) {
    var diskData = files.Count > 0 ? files[0].Data : [];
    var writer = new Qcow2Writer();
    writer.SetDiskImage(diskData);
    using var stream = new MemoryStream();
    writer.WriteTo(stream);
    return stream.ToArray();
  }

  // ── IPartitionEditable ─────────────────────────────────────────────

  public Stream OpenGuestDiskStream(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    if (!image.CanWrite)
      throw new NotSupportedException("Partition editing requires a writable QCOW2 host stream.");
    var guest = Qcow2Stream.TryOpen(image)
      ?? throw new InvalidDataException("Stream is not a supported QCOW2 image.");
    if (!guest.CanWrite) {
      guest.Dispose();
      throw new NotSupportedException(
        "This QCOW2 profile is readable but not safely writable by the current copy-on-write engine.");
    }
    return guest;
  }
}
