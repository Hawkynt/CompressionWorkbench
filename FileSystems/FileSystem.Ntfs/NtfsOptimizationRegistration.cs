using System.Runtime.CompilerServices;
using Compression.Registry;

namespace FileSystem.Ntfs;

/// <summary>Registers only compression knobs the NTFS writer actually consumes.</summary>
internal static class NtfsOptimizationRegistration {
  [ModuleInitializer]
  internal static void Register()
    => FilesystemOptimizationAdapters.RegisterCompression<NtfsFormatDescriptor>(
      transparentCompression: true,
      new FilesystemCompressionParameter("Compression"));
}
