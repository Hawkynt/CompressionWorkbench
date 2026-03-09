namespace Compression.Core.Checksums;

/// <summary>
/// Common interface for checksum algorithms.
/// </summary>
public interface IChecksum {
  /// <summary>
  /// Gets the current checksum value.
  /// </summary>
  uint Value { get; }

  /// <summary>
  /// Resets the checksum to its initial state.
  /// </summary>
  void Reset();

  /// <summary>
  /// Updates the checksum with a single byte.
  /// </summary>
  /// <param name="b">The byte to include in the checksum.</param>
  void Update(byte b);

  /// <summary>
  /// Updates the checksum with a span of bytes.
  /// </summary>
  /// <param name="data">The bytes to include in the checksum.</param>
  void Update(ReadOnlySpan<byte> data);
}
