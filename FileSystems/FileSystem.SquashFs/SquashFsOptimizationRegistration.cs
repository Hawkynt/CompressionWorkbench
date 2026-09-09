using Compression.Registry;

namespace FileSystem.SquashFs;

/// <summary>Registers writer-backed optimization capabilities for SquashFS.</summary>
internal static class SquashFsOptimizationRegistration {
  internal static void Register()
    => FilesystemOptimizationAdapters.RegisterCompression<SquashFsFormatDescriptor>(
      transparentCompression: true,
      new FilesystemCompressionParameter("BlockSize"));
}
