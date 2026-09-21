using Compression.Registry;

namespace Compression.Sfx;

/// <summary>
/// Decides which reader extracts the payload. The two tiers answer this very differently, which is
/// the whole reason the tiers exist.
/// </summary>
/// <remarks>
/// <para>
/// <b>Carved</b> knows its one format at compile time and names the descriptor directly, so the
/// linker can drop every other format in the reference closure. Measured: a zip stub costs about
/// 20 KB more than an empty native binary.
/// </para>
/// <para>
/// <b>Universal</b> carries every archive descriptor and matches magic bytes at run time. Measured
/// at roughly 10 MB for 237 formats, against 33 MB if it went through <c>Compression.Lib</c> and
/// dragged in the filesystem and audio descriptors too — neither of which can ever be a payload.
/// </para>
/// </remarks>
public static class SfxFormatResolver {
#if SFX_TIER_CARVED

  /// <summary>Names the single format this stub was built for.</summary>
  public static string FormatName =>
#if SFX_FORMAT_ZIP
    "Zip";
#elif SFX_FORMAT_SEVENZIP
    "7-Zip";
#elif SFX_FORMAT_TAR
    "Tar";
#elif SFX_FORMAT_RAR
    "RAR";
#elif SFX_FORMAT_CAB
    "CAB";
#elif SFX_FORMAT_TARGZ
    "tar.gz";
#elif SFX_FORMAT_TARXZ
    "tar.xz";
#elif SFX_FORMAT_TARZST
    "tar.zst";
#else
    "unknown";
#endif

  /// <summary>Returns the one reader this stub was carved around.</summary>
  /// <param name="payload">The bounded payload; unused here, since the format is already known.</param>
  public static IArchiveFormatOperations? Resolve(Stream payload) {
    _ = payload;
    return CreateCarvedOperations();
  }

  private static IArchiveFormatOperations CreateCarvedOperations() =>
#if SFX_FORMAT_ZIP
    new FileFormat.Zip.ZipFormatDescriptor();
#elif SFX_FORMAT_SEVENZIP
    new FileFormat.SevenZip.SevenZipFormatDescriptor();
#elif SFX_FORMAT_TAR
    new FileFormat.Tar.TarFormatDescriptor();
#elif SFX_FORMAT_RAR
    new FileFormat.Rar.RarFormatDescriptor();
#elif SFX_FORMAT_CAB
    new FileFormat.Cab.CabFormatDescriptor();
#elif SFX_FORMAT_TARGZ
    CompoundTar("Gzip");
#elif SFX_FORMAT_TARXZ
    CompoundTar("Xz");
#elif SFX_FORMAT_TARZST
    CompoundTar("Zstd");
#else
    throw new InvalidOperationException("No format selected.");
#endif

#if SFX_FORMAT_TARGZ || SFX_FORMAT_TARXZ || SFX_FORMAT_TARZST
  /// <summary>
  /// Compound tar is the one family that cannot simply be named. <c>CompoundTarDescriptor</c>
  /// resolves its two halves through string keys against the registry, which a carved stub does not
  /// populate — that would trim to a null dereference at extraction time rather than a link error.
  /// So the stub registers exactly the two descriptors it needs and nothing else.
  /// </summary>
  private static IArchiveFormatOperations CompoundTar(string streamFormatId) {
    FormatRegistry.Register(new FileFormat.Tar.TarFormatDescriptor());
    FormatRegistry.Register(StreamDescriptor());
    FormatRegistry.Initialize();

    return new CompoundTarDescriptor(
      "Tar" + streamFormatId, "tar." + streamFormatId.ToLowerInvariant(), streamFormatId,
      ".tar." + streamFormatId.ToLowerInvariant(), [".tar." + streamFormatId.ToLowerInvariant()]);
  }

  private static IFormatDescriptor StreamDescriptor() =>
#if SFX_FORMAT_TARGZ
    new FileFormat.Gzip.GzipFormatDescriptor();
#elif SFX_FORMAT_TARXZ
    new FileFormat.Xz.XzFormatDescriptor();
#else
    new FileFormat.Zstd.ZstdFormatDescriptor();
#endif
#endif

#else

  /// <summary>Set once the payload has been identified, for display.</summary>
  public static string FormatName { get; private set; } = "unknown";

  /// <summary>
  /// Identifies the payload by magic bytes across every registered archive descriptor, then returns
  /// its reader. Extension-based detection is deliberately absent: an SFX has no payload filename.
  /// </summary>
  /// <param name="payload">The bounded payload, which is rewound before returning.</param>
  public static IArchiveFormatOperations? Resolve(Stream payload) {
    Compression.Lib.FormatRegistration.EnsureInitialized();

    var header = new byte[512];
    payload.Position = 0;
    var read = payload.Read(header, 0, header.Length);
    payload.Position = 0;
    if (read <= 0) return null;

    IFormatDescriptor? best = null;
    var bestConfidence = 0.0;

    foreach (var descriptor in FormatRegistry.All) {
      foreach (var signature in descriptor.MagicSignatures) {
        if (!Matches(header.AsSpan(0, read), signature)) continue;
        if (signature.Confidence <= bestConfidence) continue;

        best = descriptor;
        bestConfidence = signature.Confidence;
      }
    }

    if (best is null) return null;

    FormatName = best.DisplayName;
    return FormatRegistry.GetArchiveOps(best.Id);
  }

  private static bool Matches(ReadOnlySpan<byte> header, MagicSignature signature) {
    var bytes = signature.Bytes;
    if (bytes.Length == 0 || signature.Offset < 0) return false;
    if (signature.Offset + bytes.Length > header.Length) return false;

    var window = header.Slice(signature.Offset, bytes.Length);
    var mask = signature.Mask;

    for (var i = 0; i < bytes.Length; ++i) {
      var actual = mask is null ? window[i] : (byte)(window[i] & mask[i]);
      var expected = mask is null ? bytes[i] : (byte)(bytes[i] & mask[i]);
      if (actual != expected) return false;
    }

    return true;
  }

#endif
}
