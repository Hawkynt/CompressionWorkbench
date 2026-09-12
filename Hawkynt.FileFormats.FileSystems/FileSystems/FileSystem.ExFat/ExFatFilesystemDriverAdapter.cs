#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Registry;

namespace FileSystem.ExFat;

/// <summary>
/// Native mounted exFAT read path. The existing offline modifier remains separate:
/// it has proven root-level in-place mutations, but does not yet implement the
/// complete nested namespace and ordered dirty/FAT/bitmap/directory publication
/// required for a writable kernel-facing filesystem session.
/// </summary>
public sealed class ExFatFilesystemDriverAdapter :
  IFilesystemDriverAdapter,
  IBlockDeviceFilesystemDriverProvider {

  public string FormatId => "ExFat";

  public FilesystemDriverProfile ProbeFilesystem(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    var original = image.CanSeek ? image.Position : 0;
    try {
      var native = ExFatNativeProbe.Validate(image);
      return BuildNativeProfile(native);
    } catch (NotSupportedException e) {
      if (image.CanSeek) image.Position = original;
      var fallback = FilesystemDriverDerivation.Probe(new ExFatFormatDescriptor(), image);
      return fallback with {
        ProfileName = "derived exFAT compatibility view",
        Limitations = [FirstLine(e.Message), .. fallback.Limitations],
      };
    } catch (Exception e) when (e is InvalidDataException or IOException or ArgumentException or OverflowException) {
      return new FilesystemDriverProfile(
        FormatId,
        "unsupported or damaged exFAT profile",
        FilesystemDriverCapabilities.None,
        FilesystemMutationModel.None,
        CanMount: false,
        CanMountWritable: false,
        [FirstLine(e.Message)]);
    } finally {
      if (image.CanSeek) image.Position = original;
    }
  }

  public IFilesystemSession OpenFilesystem(Stream image, FilesystemOpenOptions options) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(options);
    if (!options.ReadOnly)
      throw new NotSupportedException(
        "exFAT mounted writes are not enabled yet: the offline modifier does not provide complete nested-directory mutation plus the specification-required ordered VolumeDirty/FAT/allocation-bitmap/directory publication boundary.");

    var original = image.CanSeek ? image.Position : 0;
    try {
      var native = ExFatNativeProbe.Validate(image);
      return new ExFatFilesystemSession(image, BuildNativeProfile(native), native.VolumeSerial, options.LeaveOpen);
    } catch (NotSupportedException) {
      if (image.CanSeek) image.Position = original;
      return FilesystemDriverDerivation.Open(new ExFatFormatDescriptor(), image, options);
    }
  }

  public FilesystemDriverProfile ProbeFilesystem(IRandomAccessBlockDevice device) {
    ArgumentNullException.ThrowIfNull(device);
    using var stream = new BlockDeviceStream(device, leaveOpen: true);
    return ProbeFilesystem(stream);
  }

  public IFilesystemSession OpenFilesystem(IRandomAccessBlockDevice device, FilesystemOpenOptions options) {
    ArgumentNullException.ThrowIfNull(device);
    ArgumentNullException.ThrowIfNull(options);
    var stream = new BlockDeviceStream(device, leaveOpen: false);
    try {
      return OpenFilesystem(stream, options with { LeaveOpen = false });
    } catch {
      stream.Dispose();
      throw;
    }
  }

  public FilesystemDriverReadinessReport DescribeFilesystemDriverReadiness(
      Stream image,
      FilesystemDriverTarget target) {
    ArgumentNullException.ThrowIfNull(image);
    var original = image.CanSeek ? image.Position : 0;
    try {
      var native = ExFatNativeProbe.Validate(image);
      var readRequired =
        FilesystemDriverReadinessLayer.ImageValidation |
        FilesystemDriverReadinessLayer.Namespace |
        FilesystemDriverReadinessLayer.SessionStableNodeIds |
        FilesystemDriverReadinessLayer.ReadData |
        FilesystemDriverReadinessLayer.RandomAccessRead;
      var writeRequired = readRequired |
        FilesystemDriverReadinessLayer.AllocationMap |
        FilesystemDriverReadinessLayer.WriteData |
        FilesystemDriverReadinessLayer.Truncate |
        FilesystemDriverReadinessLayer.NamespaceMutation |
        FilesystemDriverReadinessLayer.Flush |
        FilesystemDriverReadinessLayer.DurabilityModel |
        FilesystemDriverReadinessLayer.Recovery |
        FilesystemDriverReadinessLayer.Concurrency;

      var available = readRequired |
        FilesystemDriverReadinessLayer.AllocationMap |
        FilesystemDriverReadinessLayer.ValidationCorpus;
      var blockers = new List<string>(native.Limitations);
      if (target == FilesystemDriverTarget.ReadWrite) {
        blockers.Add("Generalize ExFatModifier from root-only file mutation to create/unlink/mkdir/rmdir/rename in arbitrary directories, including directory growth and entry-set relocation.");
        blockers.Add("Use the volume up-case table for name hashing/collision checks on every namespace mutation instead of invariant-culture casing shortcuts.");
        blockers.Add("Stage mounted writes in the exFAT-specified order: set VolumeDirty, publish allocation/FAT and directory changes in operation-specific order, flush, then clear VolumeDirty in both boot regions.");
        blockers.Add("Model recovery after an interrupted dirty update and TexFAT active-FAT/bitmap switching before enabling those profiles for mutation.");
        blockers.Add("Define cache invalidation and locking for simultaneous open handles while allocation and directory entry sets change.");
      }
      var required = target == FilesystemDriverTarget.ReadOnly ? readRequired : writeRequired;
      return new FilesystemDriverReadinessReport(
        FormatId,
        target,
        available,
        required,
        (available & required) == required,
        UsesNativeProvider: true,
        blockers.Distinct(StringComparer.Ordinal).ToArray());
    } catch (NotSupportedException e) {
      if (image.CanSeek) image.Position = original;
      var fallback = FilesystemDriverDerivation.Assess(new ExFatFormatDescriptor(), image, target);
      return fallback with { Blockers = [FirstLine(e.Message), .. fallback.Blockers] };
    } catch (Exception e) when (e is InvalidDataException or IOException or ArgumentException or OverflowException) {
      var required = target == FilesystemDriverTarget.ReadOnly
        ? FilesystemDriverReadinessLayer.ImageValidation |
          FilesystemDriverReadinessLayer.Namespace |
          FilesystemDriverReadinessLayer.SessionStableNodeIds |
          FilesystemDriverReadinessLayer.ReadData |
          FilesystemDriverReadinessLayer.RandomAccessRead
        : FilesystemDriverReadinessLayer.ImageValidation |
          FilesystemDriverReadinessLayer.Namespace |
          FilesystemDriverReadinessLayer.SessionStableNodeIds |
          FilesystemDriverReadinessLayer.ReadData |
          FilesystemDriverReadinessLayer.RandomAccessRead |
          FilesystemDriverReadinessLayer.AllocationMap |
          FilesystemDriverReadinessLayer.WriteData |
          FilesystemDriverReadinessLayer.Truncate |
          FilesystemDriverReadinessLayer.NamespaceMutation |
          FilesystemDriverReadinessLayer.Flush |
          FilesystemDriverReadinessLayer.DurabilityModel |
          FilesystemDriverReadinessLayer.Recovery |
          FilesystemDriverReadinessLayer.Concurrency;
      return new FilesystemDriverReadinessReport(
        FormatId, target, FilesystemDriverReadinessLayer.None, required,
        Derivable: false, UsesNativeProvider: true, [FirstLine(e.Message)]);
    } finally {
      if (image.CanSeek) image.Position = original;
    }
  }

  private static FilesystemDriverProfile BuildNativeProfile(ExFatNativeProbe.Result probe) {
    var limitations = new List<string>(probe.Limitations) {
      "Mounted writes remain disabled until complete nested namespace mutation and ordered crash-consistent publication share one native mutation core.",
      "The current native case-insensitive session is intentionally restricted to ASCII live names; other name profiles fall back to the conservative archive projection until the volume up-case table drives lookup directly.",
    };
    return new FilesystemDriverProfile(
      "ExFat",
      "exFAT 1.x native read profile",
      FilesystemDriverCapabilities.EnumerateDirectories |
      FilesystemDriverCapabilities.ReadData |
      FilesystemDriverCapabilities.RandomAccess |
      FilesystemDriverCapabilities.StableNodeIds |
      FilesystemDriverCapabilities.CasePreservingNames,
      FilesystemMutationModel.None,
      CanMount: true,
      CanMountWritable: false,
      limitations.Distinct(StringComparer.Ordinal).ToArray());
  }

  private static string FirstLine(string message) {
    var p = message.IndexOfAny(['\r', '\n']);
    return p < 0 ? message : message[..p];
  }
}

internal static class ExFatNativeProbe {
  internal sealed record Result(uint VolumeSerial, IReadOnlyList<string> Limitations);

  public static Result Validate(Stream image) {
    if (!image.CanRead || !image.CanSeek)
      throw new ArgumentException("Native exFAT mounting requires a readable, seekable stream.", nameof(image));
    if (image.Length < 24L * 512)
      throw new InvalidDataException("exFAT image is too small for the mandatory main and backup boot regions.");

    Span<byte> boot = stackalloc byte[512];
    ReadExactlyAt(image, 0, boot);
    if (!boot.Slice(3, 8).SequenceEqual("EXFAT   "u8))
      throw new InvalidDataException("exFAT filesystem name signature is invalid.");
    if (BinaryPrimitives.ReadUInt16LittleEndian(boot[510..]) != 0xAA55)
      throw new InvalidDataException("exFAT main boot signature is invalid.");
    if (boot[11..64].IndexOfAnyExcept((byte)0) >= 0)
      throw new InvalidDataException("exFAT Main Boot Sector MustBeZero region is non-zero.");

    var bytesPerSectorShift = boot[108];
    var sectorsPerClusterShift = boot[109];
    if (bytesPerSectorShift is < 9 or > 12)
      throw new InvalidDataException($"exFAT BytesPerSectorShift {bytesPerSectorShift} is outside 9..12.");
    if (sectorsPerClusterShift > 25 - bytesPerSectorShift)
      throw new InvalidDataException($"exFAT SectorsPerClusterShift {sectorsPerClusterShift} exceeds the format limit for sector shift {bytesPerSectorShift}.");
    var bytesPerSector = 1 << bytesPerSectorShift;
    var sectorsPerCluster = 1 << sectorsPerClusterShift;

    var volumeLength = BinaryPrimitives.ReadUInt64LittleEndian(boot[72..]);
    var fatOffset = BinaryPrimitives.ReadUInt32LittleEndian(boot[80..]);
    var fatLength = BinaryPrimitives.ReadUInt32LittleEndian(boot[84..]);
    var clusterHeapOffset = BinaryPrimitives.ReadUInt32LittleEndian(boot[88..]);
    var clusterCount = BinaryPrimitives.ReadUInt32LittleEndian(boot[92..]);
    var rootCluster = BinaryPrimitives.ReadUInt32LittleEndian(boot[96..]);
    var serial = BinaryPrimitives.ReadUInt32LittleEndian(boot[100..]);
    var revision = BinaryPrimitives.ReadUInt16LittleEndian(boot[104..]);
    var flags = BinaryPrimitives.ReadUInt16LittleEndian(boot[106..]);
    var numberOfFats = boot[110];
    var percentInUse = boot[112];

    if ((revision >> 8) != 1)
      throw new NotSupportedException($"exFAT revision {revision >> 8}.{revision & 0xFF} is not the 1.x format family implemented by the native reader.");
    if (numberOfFats is not (1 or 2))
      throw new InvalidDataException($"exFAT NumberOfFats {numberOfFats} is invalid.");
    if (numberOfFats == 2)
      throw new NotSupportedException("TexFAT/two-FAT volumes remain on the compatibility read path until active FAT and dual allocation-bitmap semantics are modeled natively.");
    if ((flags & 0xFFF0) != 0 || (flags & 0x0001) != 0)
      throw new InvalidDataException($"exFAT VolumeFlags 0x{flags:X4} contains reserved or impossible single-FAT bits.");
    if (percentInUse > 100)
      throw new InvalidDataException($"exFAT PercentInUse {percentInUse} is outside 0..100.");
    if (volumeLength == 0 || volumeLength > (ulong)(long.MaxValue / bytesPerSector))
      throw new InvalidDataException("exFAT VolumeLength is invalid or exceeds the addressable stream range.");
    var volumeBytes = checked((long)volumeLength * bytesPerSector);
    if (volumeBytes > image.Length)
      throw new InvalidDataException($"exFAT declares {volumeBytes:N0} bytes but the image contains only {image.Length:N0} bytes.");
    if (fatOffset < 24 || fatLength == 0 || clusterCount == 0)
      throw new InvalidDataException("exFAT FAT/cluster geometry contains zero or pre-boot-region values.");
    if ((ulong)clusterHeapOffset < (ulong)fatOffset + fatLength)
      throw new InvalidDataException("exFAT cluster heap overlaps the active FAT.");
    if (rootCluster < 2 || rootCluster > clusterCount + 1)
      throw new InvalidDataException($"exFAT root directory cluster {rootCluster} lies outside the cluster heap.");
    var heapEnd = checked((ulong)clusterHeapOffset + (ulong)clusterCount * (uint)sectorsPerCluster);
    if (heapEnd > volumeLength)
      throw new InvalidDataException("exFAT cluster heap extends beyond VolumeLength.");

    ValidateBootChecksum(image, 0, bytesPerSector, "main");
    ValidateBootChecksum(image, 12L * bytesPerSector, bytesPerSector, "backup");

    Span<byte> backup = stackalloc byte[512];
    ReadExactlyAt(image, 12L * bytesPerSector, backup);
    if (!backup.Slice(3, 8).SequenceEqual("EXFAT   "u8) ||
        BinaryPrimitives.ReadUInt16LittleEndian(backup[510..]) != 0xAA55)
      throw new InvalidDataException("exFAT backup boot sector signature is invalid.");

    var limitations = new List<string>();
    if ((flags & 0x0002) != 0)
      limitations.Add("VolumeDirty is set; native read-only mounting is permitted but mounted mutation is refused until recovery semantics are implemented.");
    if ((flags & 0x0004) != 0)
      limitations.Add("MediaFailure is set in VolumeFlags; data remains readable but the volume reports prior media failure.");

    image.Position = 0;
    using var reader = new ExFatReader(image, leaveOpen: true);
    foreach (var entry in reader.Entries) {
      if (entry.Name.Any(ch => ch > 0x7F))
        throw new NotSupportedException(
          $"Native exFAT lookup currently accepts only live ASCII names; '{entry.Name}' requires volume-upcase-table driven collation and will use the compatibility projection instead.");
      if (!entry.IsDirectory && entry.ValidDataLength > entry.Size)
        throw new InvalidDataException($"exFAT file '{entry.Name}' has ValidDataLength {entry.ValidDataLength:N0} greater than DataLength {entry.Size:N0}.");
      if (!entry.IsDirectory && entry.Size > 0 && entry.FirstCluster < 2)
        throw new InvalidDataException($"exFAT file '{entry.Name}' has non-zero DataLength but no valid first cluster.");
    }

    return new Result(serial, limitations);
  }

  private static void ValidateBootChecksum(Stream image, long regionOffset, int bytesPerSector, string label) {
    var checkedBytes = new byte[11 * bytesPerSector];
    ReadExactlyAt(image, regionOffset, checkedBytes);
    uint checksum = 0;
    for (var i = 0; i < checkedBytes.Length; ++i) {
      if (i is 106 or 107 or 112) continue;
      checksum = ((checksum & 1) != 0 ? 0x80000000u : 0) + (checksum >> 1) + checkedBytes[i];
    }

    var checksumSector = new byte[bytesPerSector];
    ReadExactlyAt(image, regionOffset + 11L * bytesPerSector, checksumSector);
    for (var i = 0; i < checksumSector.Length; i += 4)
      if (BinaryPrimitives.ReadUInt32LittleEndian(checksumSector.AsSpan(i, 4)) != checksum)
        throw new InvalidDataException($"exFAT {label} boot checksum sector is inconsistent at byte {i}.");
  }

  private static void ReadExactlyAt(Stream image, long offset, Span<byte> destination) {
    image.Position = offset;
    image.ReadExactly(destination);
  }
}
