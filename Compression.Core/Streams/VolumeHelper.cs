namespace Compression.Core.Streams;

/// <summary>
/// Utility for splitting archive data into fixed-size volumes.
/// </summary>
public static class VolumeHelper {
  /// <summary>
  /// Splits a byte array into volumes of the specified maximum size.
  /// </summary>
  /// <param name="data">The archive data to split.</param>
  /// <param name="maxVolumeSize">The maximum size of each volume in bytes.</param>
  /// <returns>An array of byte arrays, one per volume.</returns>
  public static byte[][] SplitIntoVolumes(byte[] data, long maxVolumeSize) {
    if (maxVolumeSize <= 0)
      throw new ArgumentOutOfRangeException(nameof(maxVolumeSize), "Volume size must be positive.");

    if (data.Length == 0)
      return [[]];

    var volumeCount = (int)((data.Length + maxVolumeSize - 1) / maxVolumeSize);
    var volumes = new byte[volumeCount][];

    for (var i = 0; i < volumeCount; ++i) {
      var offset = i * maxVolumeSize;
      var length = (int)Math.Min(maxVolumeSize, data.Length - offset);
      volumes[i] = new byte[length];
      Array.Copy(data, offset, volumes[i], 0, length);
    }

    return volumes;
  }

  /// <summary>
  /// Writes volumes to a set of streams.
  /// </summary>
  /// <param name="data">The archive data to split.</param>
  /// <param name="maxVolumeSize">The maximum size of each volume.</param>
  /// <param name="volumeStreams">The output streams for each volume. Must have enough streams.</param>
  public static void WriteVolumes(byte[] data, long maxVolumeSize, Stream[] volumeStreams) {
    var volumes = SplitIntoVolumes(data, maxVolumeSize);
    if (volumeStreams.Length < volumes.Length)
      throw new ArgumentException(
        $"Need {volumes.Length} volume streams but only {volumeStreams.Length} provided.",
        nameof(volumeStreams));

    for (var i = 0; i < volumes.Length; ++i)
      volumeStreams[i].Write(volumes[i], 0, volumes[i].Length);
  }
}
