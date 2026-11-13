using Compression.Registry;

namespace FileSystem.Stacker;

/// <summary>
/// Stacker rebuilds compress every cluster; the <c>Method</c> option (stored, DS, SD-3, SD-4, Auto)
/// is the probe axis.
/// </summary>
internal static class StackerOptimizationRegistration {
  internal static void Register()
    => FilesystemOptimizationAdapters.RegisterCompression<StackerFormatDescriptor>(
      transparentCompression: true,
      new FilesystemCompressionParameter("Method"));
}
