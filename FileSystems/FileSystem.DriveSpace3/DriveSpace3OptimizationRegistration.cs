using System.Runtime.CompilerServices;
using Compression.Registry;

namespace FileSystem.DriveSpace3;

/// <summary>
/// DriveSpace 3 Extended rebuilds transparently compress file clusters. Its public
/// Method/Level schema belongs to the Genuine compatibility path, so the generic
/// filesystem rebuild does not advertise those values as probe axes.
/// </summary>
internal static class DriveSpace3OptimizationRegistration {
  [ModuleInitializer]
  internal static void Register()
    => FilesystemOptimizationAdapters.RegisterCompression<DriveSpace3FormatDescriptor>(
      transparentCompression: true);
}
