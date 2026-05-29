#pragma warning disable CS1591
using Compression.Registry;

namespace FileFormat.Vhd;

/// <summary>
/// Delegates to the shared <see cref="Compression.Registry.InnerFsDetector"/>
/// for backward compatibility. New code should use the shared version directly.
/// </summary>
internal static class InnerFsDetector {

  /// <inheritdoc cref="Compression.Registry.InnerFsDetector.Detect"/>
  public static IFormatDescriptor? Detect(Stream virtualDisk)
    => Compression.Registry.InnerFsDetector.Detect(virtualDisk);
}
