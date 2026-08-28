#pragma warning disable CS1591
namespace Compression.Registry;

/// <summary>
/// Optional descriptor capability for exposing the sector/block device that
/// sits below a filesystem namespace. Container descriptors can implement this
/// without pretending the container itself is a filesystem.
/// </summary>
public interface IRandomAccessBlockDeviceProvider {
  /// <summary>
  /// Opens a random-access block device over <paramref name="image"/>.
  /// Implementations must fail closed when the exact on-disk profile cannot be
  /// projected losslessly/safely at block granularity.
  /// </summary>
  IRandomAccessBlockDevice OpenBlockDevice(Stream image, bool writable, bool leaveOpen = true);
}
