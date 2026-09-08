#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Registry;

namespace FileSystem.Fat;

/// <summary>
/// Native FAT12/16/32 filesystem sidecar. Clean ordinary FAT profiles support
/// bounded direct writes while retaining stable node identities for the mounted
/// session. Profiles needing TFAT semantics, recovery, or non-mirrored FAT32
/// behavior remain fail-closed for writes.
/// </summary>
public sealed class FatFilesystemDriverAdapter :
  IFilesystemDriverAdapter,
  IBlockDeviceFilesystemDriverProvider {

  public string FormatId => "Fat";

  public FilesystemDriverProfile ProbeFilesystem(Stream image) {
    ArgumentNullException.ThrowIfNull(image);
    var original = image.CanSeek ? image.Position : 0;
    try {
      var geometry = FatDriverGeometry.Parse(image);
      using var reader = new FatReader(image, leaveOpen: true);
      var entries = reader.Entries.ToArray();
      ValidateAllocationGraph(image, geometry, entries);
      var writableBlocker = GetWritableBlocker(image, geometry);
      var canWrite = writableBlocker is null;

      var capabilities =
        FilesystemDriverCapabilities.EnumerateDirectories |
        FilesystemDriverCapabilities.ReadData |
        FilesystemDriverCapabilities.RandomAccess |
        FilesystemDriverCapabilities.StableNodeIds |
        FilesystemDriverCapabilities.CasePreservingNames;
      if (canWrite) {
        capabilities |=
          FilesystemDriverCapabilities.WriteData |
          FilesystemDriverCapabilities.Truncate |
          FilesystemDriverCapabilities.CreateFile |
          FilesystemDriverCapabilities.DeleteFile |
          FilesystemDriverCapabilities.CreateDirectory |
          FilesystemDriverCapabilities.RemoveDirectory |
          FilesystemDriverCapabilities.Rename |
          FilesystemDriverCapabilities.SetMetadata |
          FilesystemDriverCapabilities.Flush;
      }

      var limitations = new List<string> {
        "Native positional reads and mounted writes use FAT chains directly; no whole-file or whole-volume materialization is required.",
        "Node ids are stable for the mounted session. FAT has no inode number; durable identity across remount is not claimed.",
        "Ordinary FAT has no journal: writes use conservative publication ordering and a dirty-volume session boundary, but rename is not crash-atomic.",
        "FAT has no native hard-link or symbolic-link representation and does not advertise those operations.",
      };
      if (writableBlocker is not null) limitations.Add(writableBlocker);

      return new FilesystemDriverProfile(
        FormatId,
        $"FAT{reader.FatType} native chain driver",
        capabilities,
        canWrite ? FilesystemMutationModel.Direct : FilesystemMutationModel.None,
        CanMount: true,
        CanMountWritable: canWrite,
        limitations);
    } catch (Exception e) when (e is InvalidDataException or NotSupportedException or IOException or ArgumentException or OverflowException) {
      return new FilesystemDriverProfile(
        FormatId,
        "unsupported or damaged FAT profile",
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

    var profile = ProbeFilesystem(image);
    if (!profile.CanMount)
      throw new InvalidDataException("FAT image is not mountable: " + string.Join("; ", profile.Limitations));
    if (options.ReadOnly)
      return new FatReadOnlyFilesystemSession(image, profile, options.LeaveOpen);
    if (!image.CanWrite)
      throw new ArgumentException("Writable FAT mounting requires a writable backing stream.", nameof(image));
    if (!profile.CanMountWritable)
      throw new NotSupportedException("FAT image is not safely writable: " + string.Join("; ", profile.Limitations));
    return new FatWritableFilesystemSession(image, profile, options.LeaveOpen);
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
    var profile = ProbeFilesystem(image);
    const FilesystemDriverReadinessLayer readRequired =
      FilesystemDriverReadinessLayer.ImageValidation |
      FilesystemDriverReadinessLayer.Namespace |
      FilesystemDriverReadinessLayer.SessionStableNodeIds |
      FilesystemDriverReadinessLayer.ReadData |
      FilesystemDriverReadinessLayer.RandomAccessRead;
    const FilesystemDriverReadinessLayer writeRequired =
      readRequired |
      FilesystemDriverReadinessLayer.AllocationMap |
      FilesystemDriverReadinessLayer.WriteData |
      FilesystemDriverReadinessLayer.Truncate |
      FilesystemDriverReadinessLayer.NamespaceMutation |
      FilesystemDriverReadinessLayer.MetadataMutation |
      FilesystemDriverReadinessLayer.Flush |
      FilesystemDriverReadinessLayer.DurabilityModel |
      FilesystemDriverReadinessLayer.Concurrency;

    var available = profile.CanMount
      ? readRequired | FilesystemDriverReadinessLayer.AllocationMap
      : FilesystemDriverReadinessLayer.None;
    if (profile.CanMountWritable) {
      available |=
        FilesystemDriverReadinessLayer.WriteData |
        FilesystemDriverReadinessLayer.Truncate |
        FilesystemDriverReadinessLayer.NamespaceMutation |
        FilesystemDriverReadinessLayer.MetadataMutation |
        FilesystemDriverReadinessLayer.Flush |
        FilesystemDriverReadinessLayer.DurabilityModel |
        FilesystemDriverReadinessLayer.Concurrency;
    }

    var required = target == FilesystemDriverTarget.ReadOnly ? readRequired : writeRequired;
    var blockers = new List<string>();
    if (!profile.CanMount || target == FilesystemDriverTarget.ReadWrite && !profile.CanMountWritable)
      blockers.AddRange(profile.Limitations);

    return new FilesystemDriverReadinessReport(
      FormatId,
      target,
      available,
      required,
      profile.CanMount && (available & required) == required,
      UsesNativeProvider: true,
      blockers.Distinct(StringComparer.Ordinal).ToArray());
  }

  private static string? GetWritableBlocker(Stream image, FatDriverGeometry geometry) {
    Span<byte> boot = stackalloc byte[512];
    ReadAtPreservingPosition(image, 0, boot);

    var tagOffset = geometry.FatType == 32 ? 82 : 54;
    if (boot[tagOffset] == 'T' && boot[tagOffset + 1] == 'F'
        && boot[tagOffset + 2] == 'A' && boot[tagOffset + 3] == 'T')
      return "TFAT-tagged media must use FileSystem.TFat's active/inactive FAT transaction protocol; ordinary FAT direct writes are refused.";
    var legacyTfatMarkerOffset = geometry.FatType == 32 ? 65 : 37;
    if (boot[legacyTfatMarkerOffset] == 0x01)
      return "The legacy TFAT reserved-byte marker is present; ordinary FAT direct writes are refused.";

    if (geometry.FatType == 32) {
      var extFlags = BinaryPrimitives.ReadUInt16LittleEndian(boot[40..42]);
      if ((extFlags & 0x0080) != 0)
        return $"FAT32 mirroring is disabled and active FAT {extFlags & 0x000F} is selected; the writable driver currently requires mirrored FAT copies.";
      var version = BinaryPrimitives.ReadUInt16LittleEndian(boot[42..44]);
      if (version != 0)
        return $"FAT32 filesystem version 0x{version:X4} is not the FATGEN 1.0 profile supported for mounted writes.";
    }

    if (geometry.FatType is not (16 or 32)) return null;

    var reserved = ReadReservedFatEntry(image, geometry, copy: 0, cluster: 1);
    for (var copy = 1; copy < geometry.FatCount; ++copy) {
      var mirror = ReadReservedFatEntry(image, geometry, copy, cluster: 1);
      if (mirror != reserved)
        return $"FAT copies disagree in reserved entry 1: primary=0x{reserved:X}, copy {copy}=0x{mirror:X}.";
    }

    var cleanMask = geometry.FatType == 16 ? 0x8000 : 0x08000000;
    var hardErrorMask = geometry.FatType == 16 ? 0x4000 : 0x04000000;
    if ((reserved & cleanMask) == 0)
      return "The FAT volume is marked dirty; run a filesystem check/recovery before opening it writable.";
    if ((reserved & hardErrorMask) == 0)
      return "The FAT volume records a prior hard I/O error; writable mounting is refused until it has been checked.";
    return null;
  }

  private static int ReadReservedFatEntry(Stream image, FatDriverGeometry geometry, int copy, int cluster) {
    var fatStart = checked(((long)geometry.ReservedSectors + (long)copy * geometry.FatSize) * geometry.BytesPerSector);
    Span<byte> bytes = stackalloc byte[4];
    var offset = geometry.FatType switch {
      12 => fatStart + cluster + cluster / 2,
      16 => fatStart + cluster * 2L,
      _ => fatStart + cluster * 4L,
    };
    var count = geometry.FatType == 32 ? 4 : 2;
    ReadAtPreservingPosition(image, offset, bytes[..count]);
    if (geometry.FatType == 12) {
      var raw = BinaryPrimitives.ReadUInt16LittleEndian(bytes[..2]);
      return (cluster & 1) == 0 ? raw & 0x0FFF : raw >> 4;
    }
    if (geometry.FatType == 16) return BinaryPrimitives.ReadUInt16LittleEndian(bytes[..2]);
    return (int)(BinaryPrimitives.ReadUInt32LittleEndian(bytes) & 0x0FFFFFFF);
  }

  private static void ReadAtPreservingPosition(Stream image, long offset, Span<byte> destination) {
    var original = image.Position;
    try {
      image.Position = offset;
      image.ReadExactly(destination);
    } finally {
      image.Position = original;
    }
  }

  private static void ValidateAllocationGraph(
      Stream image,
      FatDriverGeometry geometry,
      IReadOnlyList<FatEntry> entries) {
    var owners = new Dictionary<int, string>();
    foreach (var entry in entries) {
      if (entry.StartCluster < 2) {
        if (!entry.IsDirectory && entry.Size > 0)
          throw new InvalidDataException($"FAT file '{entry.Name}' has data but no start cluster.");
        continue;
      }

      var chain = geometry.ReadChain(image, entry.StartCluster, entry.Name);
      if (!entry.IsDirectory) {
        var needed = entry.Size == 0 ? 0L : (entry.Size + geometry.ClusterSize - 1) / geometry.ClusterSize;
        if (chain.Count < needed)
          throw new InvalidDataException(
            $"FAT file '{entry.Name}' needs {needed} cluster(s) for {entry.Size} bytes but its chain has {chain.Count}.");
      }

      foreach (var cluster in chain) {
        if (owners.TryGetValue(cluster, out var previous))
          throw new InvalidDataException(
            $"FAT cluster {cluster} is cross-linked by '{previous}' and '{entry.Name}'.");
        owners[cluster] = entry.Name;
      }
    }
  }

  private static string FirstLine(string message) {
    var p = message.IndexOfAny(['\r', '\n']);
    return p < 0 ? message : message[..p];
  }
}
