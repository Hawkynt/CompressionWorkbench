#pragma warning disable CS1591
using System.Collections.ObjectModel;

namespace FileSystem.OneFs;

/// <summary>
/// Documented physical geometry for one candidate OneFS data-device image.
/// </summary>
/// <remarks>
/// The record describes only facts derivable from the supplied stream length and
/// Dell's published fixed 8 KiB block / 32 MiB cylinder-group geometry. It does
/// not claim that the stream contains OneFS, that it belongs to a particular
/// cluster, or that a trailing partial block/group is unused.
/// </remarks>
public sealed record OneFsDeviceGeometry(
  int Index,
  long ImageSize,
  long CompleteBlockCount,
  int PartialBlockBytes,
  long CompleteCylinderGroupCount,
  int CylinderGroupTailBytes
) {
  /// <summary>Gets whether the image length is an exact multiple of the documented 8 KiB block size.</summary>
  public bool IsBlockAligned => this.PartialBlockBytes == 0;

  /// <summary>Gets whether the image length is an exact multiple of the documented 32 MiB cylinder-group size.</summary>
  public bool IsCylinderGroupAligned => this.CylinderGroupTailBytes == 0;
}

/// <summary>
/// Non-destructive inventory and bounded raw-access surface for a candidate set
/// of Dell PowerScale / Isilon OneFS data-device images.
/// </summary>
/// <remarks>
/// <para>
/// OneFS is cluster-wide: LIN and protection metadata can address bytes on other
/// nodes and drives. A one-stream abstraction therefore cannot represent enough
/// media for future offline namespace reconstruction. This type is the
/// multi-device bootstrap surface: it keeps supplied streams separate, records
/// documented physical geometry, and permits explicit per-device reads without
/// interpreting proprietary bytes.
/// </para>
/// <para>
/// The supplied streams remain owned by the caller. <see cref="Open(IEnumerable{Stream})"/>
/// never reads them, changes their positions, or disposes them. Explicit reads
/// restore the selected stream position. Membership in a common OneFS cluster is
/// intentionally <b>not</b> asserted: that requires the still-unverified raw
/// superblock/device identity serialization.
/// </para>
/// </remarks>
public sealed class OneFsDeviceSet {
  private readonly Stream[] _streams;
  private readonly object[] _streamLocks;
  private readonly IReadOnlyList<OneFsDeviceGeometry> _devices;
  private readonly IReadOnlyDictionary<OneFsDeviceIdentity, int> _identifiedDevices;

  private OneFsDeviceSet(
      Stream[] streams,
      object[] streamLocks,
      OneFsDeviceGeometry[] devices,
      IReadOnlyDictionary<OneFsDeviceIdentity, int> identifiedDevices,
      long totalImageSize,
      long totalBlockCount,
      long totalCylinderGroupCount) {
    this._streams = streams;
    this._streamLocks = streamLocks;
    this._devices = Array.AsReadOnly(devices);
    this._identifiedDevices = identifiedDevices;
    this.TotalImageSize = totalImageSize;
    this.TotalCompleteBlockCount = totalBlockCount;
    this.TotalCompleteCylinderGroupCount = totalCylinderGroupCount;
  }

  /// <summary>Gets the candidate devices in caller-supplied order.</summary>
  public IReadOnlyList<OneFsDeviceGeometry> Devices => this._devices;

  /// <summary>
  /// Gets caller-supplied diagnostic <c>(devid,Lnum)</c> identities mapped to
  /// candidate-device indices.
  /// </summary>
  /// <remarks>
  /// The map is empty after <see cref="Open(IEnumerable{Stream})"/>. Identities
  /// appear only after an explicit <see cref="WithDeviceIdentities"/> call; they
  /// are never inferred from undocumented raw bytes.
  /// </remarks>
  public IReadOnlyDictionary<OneFsDeviceIdentity, int> IdentifiedDevices => this._identifiedDevices;

  /// <summary>Gets the total byte length of all supplied candidate devices.</summary>
  public long TotalImageSize { get; }

  /// <summary>Gets the total number of complete documented 8 KiB blocks across the device set.</summary>
  public long TotalCompleteBlockCount { get; }

  /// <summary>Gets the total number of complete documented 32 MiB cylinder groups across the device set.</summary>
  public long TotalCompleteCylinderGroupCount { get; }

  /// <summary>Gets whether every supplied device ends on an 8 KiB block boundary.</summary>
  public bool AllDevicesBlockAligned => this._devices.All(static device => device.IsBlockAligned);

  /// <summary>Gets whether every supplied device ends on a 32 MiB cylinder-group boundary.</summary>
  public bool AllDevicesCylinderGroupAligned => this._devices.All(static device => device.IsCylinderGroupAligned);

  /// <summary>
  /// Inventories a candidate set of OneFS data-device images without reading any
  /// payload bytes.
  /// </summary>
  /// <param name="devices">Readable, seekable candidate raw-device streams.</param>
  /// <returns>An immutable geometry snapshot retaining the supplied streams for bounded member reads and future format-local parsers.</returns>
  /// <exception cref="ArgumentNullException"><paramref name="devices"/> is null.</exception>
  /// <exception cref="ArgumentException">No devices were supplied; a stream is null, duplicated, unreadable, or unseekable.</exception>
  /// <exception cref="InvalidDataException">A supplied candidate device is empty.</exception>
  /// <exception cref="OverflowException">Aggregate geometry cannot be represented by signed 64-bit counters.</exception>
  public static OneFsDeviceSet Open(IEnumerable<Stream> devices) {
    ArgumentNullException.ThrowIfNull(devices);

    var streams = devices.ToArray();
    if (streams.Length == 0)
      throw new ArgumentException("At least one candidate OneFS data device is required.", nameof(devices));

    var geometry = new OneFsDeviceGeometry[streams.Length];
    var uniqueStreams = new HashSet<Stream>(ReferenceEqualityComparer.Instance);
    long totalBytes = 0;
    long totalBlocks = 0;
    long totalCylinderGroups = 0;

    for (var index = 0; index < streams.Length; ++index) {
      var stream = streams[index]
        ?? throw new ArgumentException($"Candidate OneFS device {index} is null.", nameof(devices));
      if (!uniqueStreams.Add(stream))
        throw new ArgumentException(
          $"Candidate OneFS device {index} reuses a stream already supplied for another member.",
          nameof(devices));
      if (!stream.CanRead)
        throw new ArgumentException($"Candidate OneFS device {index} must be readable.", nameof(devices));
      if (!stream.CanSeek)
        throw new ArgumentException($"Candidate OneFS device {index} must be seekable.", nameof(devices));

      var length = stream.Length;
      if (length <= 0)
        throw new InvalidDataException($"Candidate OneFS device {index} is empty.");

      var blocks = length / OneFsReader.PhysicalBlockSize;
      var partialBlockBytes = checked((int)(length % OneFsReader.PhysicalBlockSize));
      var cylinderGroups = length / OneFsReader.CylinderGroupSize;
      var cylinderGroupTailBytes = checked((int)(length % OneFsReader.CylinderGroupSize));

      geometry[index] = new OneFsDeviceGeometry(
        index,
        length,
        blocks,
        partialBlockBytes,
        cylinderGroups,
        cylinderGroupTailBytes);

      totalBytes = checked(totalBytes + length);
      totalBlocks = checked(totalBlocks + blocks);
      totalCylinderGroups = checked(totalCylinderGroups + cylinderGroups);
    }

    var emptyIdentityMap = new ReadOnlyDictionary<OneFsDeviceIdentity, int>(
      new Dictionary<OneFsDeviceIdentity, int>());
    return new OneFsDeviceSet(
      streams,
      Enumerable.Range(0, streams.Length).Select(static _ => new object()).ToArray(),
      geometry,
      emptyIdentityMap,
      totalBytes,
      totalBlocks,
      totalCylinderGroups);
  }

  /// <summary>
  /// Returns a new device-set view with explicit Dell diagnostic identities for
  /// some or all candidate streams.
  /// </summary>
  /// <param name="identities">
  /// One entry per <see cref="Devices"/> item. Null keeps that candidate
  /// unidentified; non-null values are the <c>(devid,Lnum)</c> pair reported by
  /// OneFS diagnostics such as IDI/DSR output or correlated <c>isi devices</c> data.
  /// </param>
  /// <remarks>
  /// This method is deliberately explicit: the current raw-media reader cannot
  /// verify cluster/device identity from disk bytes yet. Duplicate identities are
  /// rejected rather than silently aliasing two supplied images.
  /// </remarks>
  public OneFsDeviceSet WithDeviceIdentities(IReadOnlyList<OneFsDeviceIdentity?> identities) {
    ArgumentNullException.ThrowIfNull(identities);
    if (identities.Count != this._devices.Count)
      throw new ArgumentException(
        $"OneFS identity count {identities.Count} does not match device count {this._devices.Count}.",
        nameof(identities));

    var mapped = new Dictionary<OneFsDeviceIdentity, int>();
    for (var index = 0; index < identities.Count; ++index) {
      var identity = identities[index];
      if (identity is null)
        continue;
      if (!mapped.TryAdd(identity.Value, index))
        throw new ArgumentException($"Duplicate OneFS diagnostic device identity {identity.Value}.", nameof(identities));
    }

    return new OneFsDeviceSet(
      this._streams,
      this._streamLocks,
      this._devices.ToArray(),
      new ReadOnlyDictionary<OneFsDeviceIdentity, int>(mapped),
      this.TotalImageSize,
      this.TotalCompleteBlockCount,
      this.TotalCompleteCylinderGroupCount);
  }

  /// <summary>Tries to resolve an explicit OneFS diagnostic device identity to a supplied candidate index.</summary>
  public bool TryResolveDevice(OneFsDeviceIdentity identity, out int deviceIndex)
    => this._identifiedDevices.TryGetValue(identity, out deviceIndex);

  /// <summary>
  /// Reads one complete documented 8 KiB block from one candidate device while
  /// preserving that stream's caller-visible position.
  /// </summary>
  /// <param name="deviceIndex">Zero-based index in <see cref="Devices"/>.</param>
  /// <param name="blockIndex">Zero-based 8 KiB block index within that device.</param>
  /// <param name="destination">Destination with room for at least one complete OneFS block.</param>
  /// <exception cref="ArgumentOutOfRangeException">The device or block index is outside the inventoried complete-block range.</exception>
  /// <exception cref="ArgumentException"><paramref name="destination"/> is smaller than one OneFS block.</exception>
  /// <remarks>
  /// This is raw forensic access only. A successful read says nothing about the
  /// block's semantic type, allocation state, checksum, cluster membership, or
  /// whether it is a OneFS superblock. Calls through this object are serialized
  /// per member so their own cursor save/restore pairs cannot race. External code
  /// sharing the same caller-owned stream must provide its own synchronization.
  /// </remarks>
  public void ReadBlock(int deviceIndex, long blockIndex, Span<byte> destination) {
    if ((uint)deviceIndex >= (uint)this._streams.Length)
      throw new ArgumentOutOfRangeException(nameof(deviceIndex));
    if (destination.Length < OneFsReader.PhysicalBlockSize)
      throw new ArgumentException(
        $"OneFS block reads require at least {OneFsReader.PhysicalBlockSize} destination bytes.",
        nameof(destination));

    var device = this._devices[deviceIndex];
    if (blockIndex < 0 || blockIndex >= device.CompleteBlockCount)
      throw new ArgumentOutOfRangeException(nameof(blockIndex));

    var byteOffset = checked(blockIndex * (long)OneFsReader.PhysicalBlockSize);
    this.ReadRange(deviceIndex, byteOffset, destination[..OneFsReader.PhysicalBlockSize]);
  }

  /// <summary>
  /// Resolves and reads the exact byte extent described by a Dell diagnostic
  /// address from its explicitly identified candidate device.
  /// </summary>
  /// <param name="address">Dell <c>devid,Lnum,address:length</c> extent.</param>
  /// <param name="destination">Destination with room for the entire diagnostic extent.</param>
  /// <exception cref="ArgumentException">The extent is too large for a span or <paramref name="destination"/> is too small.</exception>
  /// <exception cref="ArgumentOutOfRangeException">The diagnostic extent lies outside the inventoried candidate image.</exception>
  /// <exception cref="KeyNotFoundException">No candidate was explicitly assigned the address's <c>(devid,Lnum)</c> identity.</exception>
  /// <remarks>
  /// Dell publishes inode addresses with 512-byte lengths as well as 8 KiB data
  /// extents. This method therefore preserves the diagnostic byte range exactly;
  /// it does not round to filesystem-block boundaries or infer semantic type.
  /// </remarks>
  public void ReadDiagnosticExtent(OneFsDiagnosticBlockAddress address, Span<byte> destination) {
    if (!this._identifiedDevices.TryGetValue(address.Device, out var deviceIndex))
      throw new KeyNotFoundException($"No supplied OneFS candidate is mapped to diagnostic device {address.Device}.");
    if (address.Length > int.MaxValue)
      throw new ArgumentException("Diagnostic extent is too large to expose through a single Span<byte> read.", nameof(address));

    var length = checked((int)address.Length);
    if (destination.Length < length)
      throw new ArgumentException(
        $"Diagnostic extent requires {length} destination bytes but only {destination.Length} were supplied.",
        nameof(destination));

    var device = this._devices[deviceIndex];
    if (address.Length > device.ImageSize || address.ByteOffset > device.ImageSize - address.Length)
      throw new ArgumentOutOfRangeException(nameof(address), "Diagnostic extent lies outside the supplied candidate device.");

    this.ReadRange(deviceIndex, address.ByteOffset, destination[..length]);
  }

  /// <summary>
  /// Resolves and reads one 8 KiB Dell diagnostic block address from its explicitly
  /// identified candidate device.
  /// </summary>
  /// <exception cref="ArgumentException">
  /// The diagnostic address is not exactly one aligned OneFS filesystem block.
  /// </exception>
  /// <exception cref="KeyNotFoundException">
  /// No candidate was explicitly assigned the address's <c>(devid,Lnum)</c> identity.
  /// </exception>
  public void ReadDiagnosticBlock(OneFsDiagnosticBlockAddress address, Span<byte> destination) {
    if (!address.IsFilesystemBlockAligned || address.Length != OneFsReader.PhysicalBlockSize)
      throw new ArgumentException(
        $"Diagnostic block reads require one aligned {OneFsReader.PhysicalBlockSize}-byte address extent.",
        nameof(address));

    this.ReadDiagnosticExtent(address, destination);
  }

  /// <summary>
  /// Gets a candidate device stream for future OneFS format-local parsers.
  /// </summary>
  /// <remarks>
  /// Internal on purpose: public consumers use the bounded raw-access methods
  /// until a verified raw OneFS structure parser exists. The stream remains
  /// caller-owned.
  /// </remarks>
  internal Stream GetDeviceStream(int index) => this._streams[index];

  private void ReadRange(int deviceIndex, long byteOffset, Span<byte> destination) {
    var stream = this._streams[deviceIndex];
    lock (this._streamLocks[deviceIndex]) {
      var originalPosition = stream.Position;
      try {
        stream.Position = byteOffset;
        stream.ReadExactly(destination);
      } finally {
        stream.Position = originalPosition;
      }
    }
  }
}
