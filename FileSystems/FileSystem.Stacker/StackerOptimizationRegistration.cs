using System.Runtime.CompilerServices;
using Compression.Registry;

namespace FileSystem.Stacker;

/// <summary>
/// The ordinary Extended Stacker rebuild uses <see cref="StackerWriter"/>, whose
/// default is per-cluster LZS compression with stored fallback. Genuine-layout
/// Method/Level knobs are compatibility-path options and therefore are not exposed
/// as generic filesystem compression probe axes here.
/// </summary>
internal static class StackerOptimizationRegistration {
  [ModuleInitializer]
  internal static void Register()
    => FilesystemOptimizationAdapters.RegisterCompression<StackerFormatDescriptor>(
      transparentCompression: true);
}
