using Compression.Registry;

namespace Compression.Lib;

/// <summary>
/// Hosts the registration the source generator emits for a universal stub.
/// </summary>
/// <remarks>
/// <para>
/// <c>Compression.Registry.Generator</c> emits its output into this namespace and type, and emits
/// only the <i>implementing</i> halves of the partial methods — the defining halves normally live in
/// <c>Compression.Lib</c>. A universal stub cannot reference that assembly (doing so would pull in
/// the filesystem and audio descriptors, tripling the binary), so it supplies the defining halves
/// here instead.
/// </para>
/// <para>
/// Because the generator discovers descriptors from the compilation's reference closure, a stub that
/// references only the archive assemblies gets an archive-only registration automatically. Nothing
/// filters the list by hand, so it cannot drift from what the library actually ships.
/// </para>
/// </remarks>
public static partial class FormatRegistration {
  private static bool _initialized;

  /// <summary>Generated: constructs every descriptor in the reference closure.</summary>
  static partial void RegisterFormats();

  /// <summary>Generated: registers raw algorithm primitives.</summary>
  static partial void RegisterBuildingBlocks();

  /// <summary>Generated: registers filesystem driver sidecars. Empty in a stub.</summary>
  static partial void RegisterFilesystemDrivers();

  /// <summary>Generated: registers package-native detection sources.</summary>
  static partial void RegisterDetectionSources();

  /// <summary>Populates the registry once. Safe to call repeatedly.</summary>
  public static void EnsureInitialized() {
    if (_initialized) return;
    _initialized = true;

    RegisterFormats();
    RegisterBuildingBlocks();
    RegisterFilesystemDrivers();
    RegisterDetectionSources();
    RegisterCompoundTar();

    FormatRegistry.Initialize();
  }

  /// <summary>
  /// The compound tar formats have no descriptor type of their own to discover — the library
  /// composes them by hand, and so must a stub that wants .tar.gz and friends to work.
  /// </summary>
  private static void RegisterCompoundTar() {
    foreach (var (id, name, stream, extension, extensions) in new (string, string, string, string, string[])[] {
      ("TarGz", "tar.gz", "Gzip", ".tar.gz", [".tar.gz", ".tgz"]),
      ("TarBz2", "tar.bz2", "Bzip2", ".tar.bz2", [".tar.bz2", ".tbz2"]),
      ("TarXz", "tar.xz", "Xz", ".tar.xz", [".tar.xz", ".txz"]),
      ("TarZst", "tar.zst", "Zstd", ".tar.zst", [".tar.zst"]),
      ("TarLz4", "tar.lz4", "Lz4", ".tar.lz4", [".tar.lz4"]),
      ("TarLzip", "tar.lz", "Lzip", ".tar.lz", [".tar.lz", ".tlz"]),
      ("TarBr", "tar.br", "Brotli", ".tar.br", [".tar.br", ".tbr"]),
    }) {
      // Only register a composite whose halves this stub actually carries.
      if (FormatRegistry.GetById(stream) is null || FormatRegistry.GetById("Tar") is null) continue;

      FormatRegistry.Register(new CompoundTarDescriptor(id, name, stream, extension, extensions));
    }
  }
}
