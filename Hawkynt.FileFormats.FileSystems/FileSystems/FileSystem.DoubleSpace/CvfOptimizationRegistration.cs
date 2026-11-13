using Compression.Registry;

namespace FileSystem.DoubleSpace;

/// <summary>
/// Explicit CVF compression capabilities: DoubleSpace and DriveSpace rebuilds compress every
/// cluster, and the <c>Method</c> option (stored, DS, JM, Auto) is a probe axis for both.
/// </summary>
internal static class CvfOptimizationRegistration {
  internal static void RegisterDoubleSpace()
    => FilesystemOptimizationAdapters.RegisterCompression<DoubleSpaceFormatDescriptor>(
      transparentCompression: true,
      new FilesystemCompressionParameter("Method"));

  internal static void RegisterDriveSpace()
    => FilesystemOptimizationAdapters.RegisterCompression<DriveSpaceFormatDescriptor>(
      transparentCompression: true,
      new FilesystemCompressionParameter("Method"));
}
