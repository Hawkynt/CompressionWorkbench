using Compression.Registry;

namespace FileSystem.DoubleSpace;

/// <summary>
/// Explicit CVF compression capabilities. DoubleSpace's generic Extended rebuild
/// always uses compressed runs but does not consume its Genuine-only Method schema;
/// DriveSpace 6.22 does consume its Method key on the normal rebuild path and can
/// therefore safely probe it.
/// </summary>
internal static class CvfOptimizationRegistration {
  internal static void RegisterDoubleSpace()
    => FilesystemOptimizationAdapters.RegisterCompression<DoubleSpaceFormatDescriptor>(
      transparentCompression: true);

  internal static void RegisterDriveSpace()
    => FilesystemOptimizationAdapters.RegisterCompression<DriveSpaceFormatDescriptor>(
      transparentCompression: true,
      new FilesystemCompressionParameter("Method"));
}
