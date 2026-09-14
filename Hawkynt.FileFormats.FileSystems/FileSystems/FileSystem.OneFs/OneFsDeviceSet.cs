#pragma warning disable CS1591
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
/// Non-destructive inventory of a candidate set of Dell PowerScale / Isilon
/// OneFS data-device images.
/// </summary>
/// <remarks>
/// <para>
/// OneFS is cluster-wide: LIN and protection metadata can address blocks on
/// other nodes and drives. A one-stream abstraction therefore cannot represent
/// enough media for future offline namespace reconstruction. This type is the
/// multi-device bootstrap surface: it keeps the supplied streams together and
/// records their documented physical geometry without interpreting proprietary
/// bytes.
/// </para>
/// <para>
/// The supplied streams remain owned by the caller. <see cref="Open(IEnumerable{Stream})"/>
/// never reads them, changes their positions, or disposes them. Membership in a
/// common OneFS cluster is intentionally <b>not</b> asserted: that requires the
/// still-unverified raw superblock/device identity serialization.
/// </para>
/// </remarks>
public sealed class OneFsDeviceSet {
  private readonly Stream[] _streams;
  private readonly OneFsDeviceGeometry[] _devices;

  private OneFsDeviceSet(Stream[] streams, OneFsDeviceGeometry[] devices,
      long totalImageSize, long totalBlockCount, long totalCylinderGroupCount) {
    this._streams = streams;
    this._devices = devices;
    this.TotalImageSize = totalImageSize;
    this.TotalCompleteBlockCount = totalBlockCount;
    this.TotalCompleteCylinderGroupCount = totalCylinderGroupCount;
  }

  /// <summary>Gets the candidate devices in caller-supplied order.</summary>
  public IReadOnlyList<OneFsDeviceGeometry> Devices => this._devices;

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
  /// <returns>An immutable geometry snapshot retaining the supplied streams for future format-local parsers.</returns>
  /// <exception cref="ArgumentNullException"><paramref name="devices"/> is null.</exception>
  /// <exception cref="ArgumentException">No devices were supplied, or a stream is null, unreadable, or unseekable.</exception>
  /// <exception cref="InvalidDataException">A supplied candidate device is empty.</exception>
  /// <exception cref="OverflowException">Aggregate geometry cannot be represented by signed 64-bit counters.</exception>
  public static OneFsDeviceSet Open(IEnumerable<Stream> devices) {
    ArgumentNullException.ThrowIfNull(devices);

    var streams = devices.ToArray();
    if (streams.Length == 0)
      throw new ArgumentException("At least one candidate OneFS data device is required.", nameof(devices));

    var geometry = new OneFsDeviceGeometry[streams.Length];
    long totalBytes = 0;
    long totalBlocks = 0;
    long totalCylinderGroups = 0;

    for (var index = 0; index < streams.Length; ++index) {
      var stream = streams[index]
        ?? throw new ArgumentException($"Candidate OneFS device {index} is null.", nameof(devices));
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

    return new OneFsDeviceSet(streams, geometry, totalBytes, totalBlocks, totalCylinderGroups);
  }

  /// <summary>
  /// Gets a candidate device stream for future OneFS format-local parsers.
  /// </summary>
  /// <remarks>
  /// Internal on purpose: public consumers receive geometry only until a verified
  /// raw OneFS structure parser exists. The stream remains caller-owned.
  /// </remarks>
  internal Stream GetDeviceStream(int index) => this._streams[index];
}
