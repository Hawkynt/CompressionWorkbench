using Compression.Core.DiskImage;
using Compression.Registry;

namespace Compression.Mounting;

/// <summary>
/// Result of resolving an arbitrary registered source into the one neutral
/// namespace contract consumed by Dokan/FUSE/etc. Layers are diagnostic only;
/// the backend never receives container-specific objects.
/// </summary>
public sealed record MountNamespaceProbe(
  FilesystemDriverProfile Profile,
  IReadOnlyList<string> Layers
);

/// <summary>
/// Resolves filesystems, archives and disk-image containers entirely through
/// CompressionWorkbench's managed parsers. Host mount facilities are never used
/// to interpret an inner layer: the OS adapter sees only the resulting
/// <see cref="IFilesystemSession"/>.
/// </summary>
public static class MountNamespaceResolver {

  public static MountNamespaceProbe Probe(
      string formatId,
      Stream source,
      string? password = null) {
    ArgumentException.ThrowIfNullOrWhiteSpace(formatId);
    ArgumentNullException.ThrowIfNull(source);
    var descriptor = FormatRegistry.GetById(formatId)
      ?? throw new KeyNotFoundException($"Unknown format id '{formatId}'.");

    var savedPosition = source.CanSeek ? source.Position : 0;
    try {
      if (IsFilesystem(formatId)) {
        if (source.CanSeek) source.Position = 0;
        var profile = FormatRegistry.ProbeFilesystem(formatId, source, password);
        return new(profile, [$"filesystem:{descriptor.Id}"]);
      }

      if (descriptor is IRandomAccessBlockDeviceProvider blockProvider
          && TryProbeBlockContainer(descriptor, blockProvider, source, password) is { } nested)
        return nested;

      if (source.CanSeek) source.Position = 0;
      var archiveProfile = FilesystemDriverDerivation.Probe(descriptor, source, password);
      return new(
        archiveProfile,
        [$"archive:{descriptor.Id}", "namespace:derived-read-only"]);
    } finally {
      if (source.CanSeek)
        source.Position = savedPosition;
    }
  }

  public static IFilesystemSession Open(
      string formatId,
      Stream source,
      FilesystemOpenOptions options,
      string? password = null) {
    ArgumentException.ThrowIfNullOrWhiteSpace(formatId);
    ArgumentNullException.ThrowIfNull(source);
    ArgumentNullException.ThrowIfNull(options);
    var descriptor = FormatRegistry.GetById(formatId)
      ?? throw new KeyNotFoundException($"Unknown format id '{formatId}'.");

    IFilesystemSession session;
    if (IsFilesystem(formatId)) {
      if (source.CanSeek) source.Position = 0;
      session = FormatRegistry.OpenFilesystem(
        formatId,
        source,
        options with { LeaveOpen = true },
        password);
    } else if (descriptor is IRandomAccessBlockDeviceProvider blockProvider
               && TryOpenBlockContainer(descriptor, blockProvider, source, options, password) is { } nested) {
      session = nested;
    } else {
      if (source.CanSeek) source.Position = 0;
      session = FilesystemDriverDerivation.Open(
        descriptor,
        source,
        options with { LeaveOpen = true },
        password);
    }

    return options.LeaveOpen ? session : new OwnedFilesystemSession(session, source);
  }

  private static MountNamespaceProbe? TryProbeBlockContainer(
      IFormatDescriptor outer,
      IRandomAccessBlockDeviceProvider provider,
      Stream source,
      string? password) {
    IRandomAccessBlockDevice? device = null;
    try {
      device = OpenProbeDevice(provider, source);
      using var disk = new BlockDeviceStream(device, leaveOpen: true);
      var partitionTable = PartitionTableDetector.Detect(disk);

      if (partitionTable.Partitions.Count == 0) {
        if (TryDetectFilesystem(disk) is not { } inner)
          return null;
        disk.Position = 0;
        var profile = FormatRegistry.ProbeFilesystem(inner.Id, disk, password);
        if (!profile.CanMount) return null;
        return new(
          profile with {
            Limitations = profile.Limitations
              .Append($"Backing bytes are translated by the {outer.DisplayName} container driver; no host disk/loop mount is involved.")
              .Distinct(StringComparer.Ordinal)
              .ToArray(),
          },
          [$"container:{outer.Id}", "block-device", $"filesystem:{inner.Id}"]);
      }

      var childProfiles = new List<(PartitionEntry Partition, IFormatDescriptor Descriptor, FilesystemDriverProfile Profile)>();
      foreach (var partition in partitionTable.Partitions) {
        if (!TryGetBlockRange(device, partition, out var firstBlock, out var blockCount))
          return null;

        using var partitionDevice = new PartitionBlockDevice(device, firstBlock, blockCount, leaveOpen: true);
        using var partitionStream = new BlockDeviceStream(partitionDevice, leaveOpen: true);
        if (TryDetectFilesystem(partitionStream) is not { } inner)
          return null;
        partitionStream.Position = 0;
        var profile = FormatRegistry.ProbeFilesystem(inner.Id, partitionStream, password);
        if (!profile.CanMount)
          return null;
        childProfiles.Add((partition, inner, profile));
      }

      var limitations = childProfiles
        .SelectMany(static child => child.Profile.Limitations)
        .Append($"Synthetic {partitionTable.Scheme} partition root assembled in userspace from {childProfiles.Count} filesystem drivers.")
        .Append("Composite partition roots are read-only; a writable mount must target one filesystem with a complete native write profile.")
        .Distinct(StringComparer.Ordinal)
        .ToArray();
      var profileForComposite = new FilesystemDriverProfile(
        outer.Id,
        $"{partitionTable.Scheme} partitioned disk ({childProfiles.Count} filesystems)",
        FilesystemMountCapabilityResolver.CoreReadCapabilities |
        FilesystemDriverCapabilities.CasePreservingNames,
        FilesystemMutationModel.None,
        CanMount: true,
        CanMountWritable: false,
        limitations);
      var layers = new List<string> { $"container:{outer.Id}", "block-device", $"partition-table:{partitionTable.Scheme}" };
      layers.AddRange(childProfiles.Select(child => $"partition{child.Partition.Index + 1}:filesystem:{child.Descriptor.Id}"));
      return new(profileForComposite, layers);
    } catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or IOException or ArgumentException) {
      return null;
    } finally {
      device?.Dispose();
    }
  }

  private static IFilesystemSession? TryOpenBlockContainer(
      IFormatDescriptor outer,
      IRandomAccessBlockDeviceProvider provider,
      Stream source,
      FilesystemOpenOptions options,
      string? password) {
    IRandomAccessBlockDevice? device = null;
    var mounts = new List<PartitionedFilesystemSession.PartitionMount>();
    try {
      device = provider.OpenBlockDevice(
        source,
        writable: !options.ReadOnly,
        leaveOpen: true);
      using var detectionStream = new BlockDeviceStream(device, leaveOpen: true);
      var partitionTable = PartitionTableDetector.Detect(detectionStream);

      if (partitionTable.Partitions.Count == 0) {
        if (TryDetectFilesystem(detectionStream) is not { } inner)
          return null;
        var blockStream = new BlockDeviceStream(device, leaveOpen: true);
        try {
          blockStream.Position = 0;
          var child = FormatRegistry.OpenFilesystem(
            inner.Id,
            blockStream,
            options with { LeaveOpen = true },
            password);
          var result = new OwnedFilesystemSession(child, blockStream, device);
          device = null;
          return result;
        } catch {
          blockStream.Dispose();
          throw;
        }
      }

      if (!options.ReadOnly)
        return null;

      foreach (var partition in partitionTable.Partitions) {
        if (!TryGetBlockRange(device, partition, out var firstBlock, out var blockCount))
          return null;

        var partitionDevice = new PartitionBlockDevice(device, firstBlock, blockCount, leaveOpen: true);
        var partitionStream = new BlockDeviceStream(partitionDevice, leaveOpen: true);
        try {
          if (TryDetectFilesystem(partitionStream) is not { } inner) {
            partitionStream.Dispose();
            partitionDevice.Dispose();
            return null;
          }
          partitionStream.Position = 0;
          var child = FormatRegistry.OpenFilesystem(
            inner.Id,
            partitionStream,
            options with { ReadOnly = true, LeaveOpen = true },
            password);
          var ownedChild = new OwnedFilesystemSession(child, partitionStream, partitionDevice);
          mounts.Add(new(MakePartitionName(partition), ownedChild));
        } catch {
          partitionStream.Dispose();
          partitionDevice.Dispose();
          throw;
        }
      }

      var composite = new PartitionedFilesystemSession(
        outer.Id,
        partitionTable.Scheme,
        mounts,
        device,
        [$"Outer container '{outer.DisplayName}' is decoded by CompressionWorkbench before partition/filesystem traversal."]);
      mounts.Clear(); // ownership moved into the composite
      device = null;
      return composite;
    } catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or IOException or ArgumentException) {
      return null;
    } finally {
      foreach (var mount in mounts)
        mount.Session.Dispose();
      device?.Dispose();
    }
  }

  private static IRandomAccessBlockDevice OpenProbeDevice(
      IRandomAccessBlockDeviceProvider provider,
      Stream source) {
    if (source.CanWrite) {
      try {
        return provider.OpenBlockDevice(source, writable: true, leaveOpen: true);
      } catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or ArgumentException) {
        if (source.CanSeek) source.Position = 0;
      }
    }
    return provider.OpenBlockDevice(source, writable: false, leaveOpen: true);
  }

  private static IFormatDescriptor? TryDetectFilesystem(Stream stream) {
    if (stream.CanSeek) stream.Position = 0;
    var detected = InnerFsDetector.Detect(stream);
    if (detected is null) return null;
    return IsFilesystem(detected.Id) ? detected : null;
  }

  private static bool TryGetBlockRange(
      IRandomAccessBlockDevice device,
      PartitionEntry partition,
      out long firstBlock,
      out long blockCount) {
    var blockSize = device.Geometry.LogicalBlockSize;
    if (partition.StartOffset < 0 || partition.Size <= 0 ||
        partition.StartOffset % blockSize != 0 || partition.Size % blockSize != 0) {
      firstBlock = 0;
      blockCount = 0;
      return false;
    }

    firstBlock = partition.StartOffset / blockSize;
    blockCount = partition.Size / blockSize;
    return firstBlock <= device.Geometry.BlockCount &&
           blockCount <= device.Geometry.BlockCount - firstBlock;
  }

  private static string MakePartitionName(PartitionEntry partition) {
    var type = string.Concat(partition.TypeName.Select(static c =>
      char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));
    if (type.Length == 0) type = "raw";
    return $"Partition{partition.Index + 1}_{type}";
  }

  private static bool IsFilesystem(string formatId)
    => FormatRegistry.FilesystemFormatIds.Contains(formatId, StringComparer.OrdinalIgnoreCase);
}
