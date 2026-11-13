using Compression.Registry;

namespace FileSystem.DriveSpace3;

/// <summary>
/// DriveSpace 3 rebuilds compress every cluster; the <c>Method</c> option (stored, JM, SQ, DS, Auto)
/// is the probe axis.
/// </summary>
internal static class DriveSpace3OptimizationRegistration {
  internal static void Register()
    => FilesystemOptimizationAdapters.RegisterCompression<DriveSpace3FormatDescriptor>(
      transparentCompression: true,
      new FilesystemCompressionParameter("Method"));
}
