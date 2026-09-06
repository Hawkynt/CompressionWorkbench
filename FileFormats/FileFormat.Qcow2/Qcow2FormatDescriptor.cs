#pragma warning disable CS1591
using System.Buffers.Binary;
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
public sealed class Qcow2FormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IArchiveModifiable, IArchiveDefragmentable, IArchiveShrinkable, IArchiveLayoutMap, IFilesystemExtentMap, IPartitionEditable {
  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "Qcow2";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "QCOW2";
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
  public string DefaultExtension => ".qcow2";
  /// <summary>
  /// Gets the extensions.
  /// </summary>
  public IReadOnlyList<string> Extensions => [".qcow2", ".qcow"];
  /// <summary>
  /// Gets the compound extensions.
  /// </summary>
  public IReadOnlyList<string> CompoundExtensions => [];
  /// <summary>
  /// Gets the magic signatures.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures =>
    [new([0x51, 0x46, 0x49, 0xFB], Confidence: 0.95)];
  /// <summary>
  /// Gets the methods.
  /// </summary>
  public IReadOnlyList<FormatMethodInfo> Methods => [new("qcow2", "QCOW2")];
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
  public string Description => "QEMU Copy-On-Write disk image";

  // ── IArchiveFormatOperations ──────────────────────────────────────

  /// <summary>
  /// Lists the entries in the supplied container.
  /// </summary>
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

  /// <summary>
  /// Decodes the supplied input.
  /// </summary>
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

  /// <summary>
  /// Performs the create operation.
  /// </summary>
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

  // ── IArchiveShrinkable ─────────────────────────────────────────────

  /// <summary>
  /// Rebuilds a supported flat QCOW2 v2 image from its raw guest-disk bytes so
  /// zero guest clusters become unallocated and stale physical allocations are
  /// discarded. The rebuilt guest disk is compared byte-for-byte before it can
  /// replace the input. Unsupported profiles are copied through unchanged.
  /// </summary>
  public void Shrink(Stream input, Stream output) {
    ArgumentNullException.ThrowIfNull(input);
    ArgumentNullException.ThrowIfNull(output);
    if (!input.CanRead || !input.CanSeek)
      throw new ArgumentException("QCOW2 shrink requires a readable, seekable input.", nameof(input));
    if (!output.CanWrite || !output.CanSeek)
      throw new ArgumentException("QCOW2 shrink requires a writable, seekable output.", nameof(output));

    if (!CanCanonicalizeFlatV2(input)) {
      CopyUnchanged(input, output);
      return;
    }

    try {
      input.Position = 0;
      var reader = new Qcow2Reader(input);
      var rawDisk = reader.ExtractDisk();

      using var staged = CreateScratchStream();
      var writer = new Qcow2Writer();
      writer.SetDiskImage(rawDisk);
      writer.WriteTo(staged);

      staged.Position = 0;
      var verifyReader = new Qcow2Reader(staged);
      var verifiedDisk = verifyReader.ExtractDisk();
      if (!verifiedDisk.AsSpan().SequenceEqual(rawDisk)) {
        CopyUnchanged(input, output);
        return;
      }

      if (staged.Length >= input.Length) {
        CopyUnchanged(input, output);
        return;
      }

      output.Position = 0;
      output.SetLength(0);
      staged.Position = 0;
      staged.CopyTo(output);
      output.Position = 0;
    } catch {
      CopyUnchanged(input, output);
    }
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

  private static bool CanCanonicalizeFlatV2(Stream image) {
    if (image.Length < 72)
      return false;
    image.Position = 0;
    Span<byte> header = stackalloc byte[72];
    image.ReadExactly(header);
    if (!header[..4].SequenceEqual(new byte[] { 0x51, 0x46, 0x49, 0xFB }))
      return false;

    var version = BinaryPrimitives.ReadUInt32BigEndian(header[4..]);
    var backingFileOffset = BinaryPrimitives.ReadUInt64BigEndian(header[8..]);
    var backingFileSize = BinaryPrimitives.ReadUInt32BigEndian(header[16..]);
    var cryptMethod = BinaryPrimitives.ReadUInt32BigEndian(header[32..]);
    var snapshots = BinaryPrimitives.ReadUInt32BigEndian(header[60..]);
    var snapshotsOffset = BinaryPrimitives.ReadUInt64BigEndian(header[64..]);
    return version == 2
        && backingFileOffset == 0
        && backingFileSize == 0
        && cryptMethod == 0
        && snapshots == 0
        && snapshotsOffset == 0;
  }

  private static void CopyUnchanged(Stream input, Stream output) {
    if (ReferenceEquals(input, output)) {
      input.Position = 0;
      return;
    }
    input.Position = 0;
    output.Position = 0;
    output.SetLength(0);
    input.CopyTo(output);
    output.Position = 0;
  }

  private static FileStream CreateScratchStream()
    => new(Path.Combine(Path.GetTempPath(), "cwb_qcow2_" + Guid.NewGuid().ToString("N") + ".tmp"),
      FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 64 * 1024, FileOptions.DeleteOnClose);

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
  /// <summary>
  /// Performs the open guest disk stream operation.
  /// </summary>
  public Stream OpenGuestDiskStream(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    if (!image.CanWrite)
      throw new NotSupportedException("Partition editing requires a writable QCOW2 stream.");
    return Qcow2Stream.TryOpen(image)
      ?? throw new InvalidDataException("Stream is not a valid QCOW2 image.");
  }
}
