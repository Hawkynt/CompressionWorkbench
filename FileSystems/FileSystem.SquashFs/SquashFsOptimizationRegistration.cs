using System.Runtime.CompilerServices;
using Compression.Registry;

namespace FileSystem.SquashFs;

/// <summary>Registers writer-backed optimization capabilities for SquashFS.</summary>
internal static class SquashFsOptimizationRegistration {
  [ModuleInitializer]
  internal static void Register()
    => FilesystemOptimizationAdapters.RegisterTransparentCompression<SquashFsFormatDescriptor>();
}
