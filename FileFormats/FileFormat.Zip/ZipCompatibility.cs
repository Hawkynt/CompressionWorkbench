namespace FileFormat.Zip;

/// <summary>
/// Maximum ZIP feature level that a writer may require from an extractor.
/// Values are the PKWARE "version needed to extract" numbers.
/// </summary>
public enum ZipCompatibilityProfile : ushort {
  /// <summary>ZIP 1.0: store and the legacy Shrink/Reduce/Implode methods.</summary>
  Zip10 = 10,

  /// <summary>ZIP 2.0: directories, Deflate, and traditional PKZIP encryption.</summary>
  Zip20 = 20,

  /// <summary>ZIP 2.1: adds Deflate64.</summary>
  Zip21 = 21,

  /// <summary>ZIP 4.5: adds ZIP64.</summary>
  Zip45 = 45,

  /// <summary>ZIP 4.6: adds BZip2.</summary>
  Zip46 = 46,

  /// <summary>ZIP 5.1: adds AES encryption.</summary>
  Zip51 = 51,

  /// <summary>ZIP 6.3: adds the modern LZMA, PPMd and Zstandard method family used here.</summary>
  Zip63 = 63,
}

/// <summary>
/// Resolves ZIP feature requirements and enforces a writer compatibility ceiling.
/// </summary>
public static class ZipCompatibility {
  /// <summary>
  /// Returns the minimum PKWARE ZIP version required to extract an entry using
  /// the supplied features. When several features apply, the highest minimum
  /// wins, as required by APPNOTE.TXT section 4.4.3.2.
  /// </summary>
  public static ushort GetVersionNeeded(
      ZipCompressionMethod method,
      ZipEncryptionMethod encryption = ZipEncryptionMethod.None,
      bool zip64 = false,
      bool isDirectory = false) {
    var result = GetMethodVersion(method);

    if (isDirectory)
      result = Max(result, ZipConstants.VersionNeeded20);

    result = encryption switch {
      ZipEncryptionMethod.None => result,
      ZipEncryptionMethod.PkzipTraditional => Max(result, ZipConstants.VersionNeeded20),
      ZipEncryptionMethod.Aes256 => Max(result, ZipConstants.VersionNeeded51),
      _ => throw new ArgumentOutOfRangeException(nameof(encryption), encryption, "Unknown ZIP encryption method."),
    };

    if (zip64)
      result = Max(result, ZipConstants.VersionNeeded45);

    return result;
  }

  /// <summary>Returns whether all requested features fit within <paramref name="profile"/>.</summary>
  public static bool IsSupported(
      ZipCompatibilityProfile profile,
      ZipCompressionMethod method,
      ZipEncryptionMethod encryption = ZipEncryptionMethod.None,
      bool zip64 = false,
      bool isDirectory = false)
    => GetVersionNeeded(method, encryption, zip64, isDirectory) <= (ushort)profile;

  /// <summary>Throws when the requested features would exceed <paramref name="profile"/>.</summary>
  public static void EnsureSupported(
      ZipCompatibilityProfile profile,
      ZipCompressionMethod method,
      ZipEncryptionMethod encryption = ZipEncryptionMethod.None,
      bool zip64 = false,
      bool isDirectory = false) {
    var required = GetVersionNeeded(method, encryption, zip64, isDirectory);
    if (required <= (ushort)profile)
      return;

    throw new NotSupportedException(
      $"ZIP compatibility profile {profile} permits version-needed <= {(ushort)profile / 10.0:0.0}, " +
      $"but the selected features require ZIP {required / 10.0:0.0}.");
  }

  internal static ushort GetVersionNeeded(ZipEntry entry) {
    var method = entry.WrappedCompressionMethod ?? entry.CompressionMethod;
    var encryption = entry.CompressionMethod == ZipCompressionMethod.WinZipAes
      ? ZipEncryptionMethod.Aes256
      : entry.IsEncrypted ? ZipEncryptionMethod.PkzipTraditional : ZipEncryptionMethod.None;

    return GetVersionNeeded(method, encryption, entry.IsZip64, entry.IsDirectory);
  }

  private static ushort GetMethodVersion(ZipCompressionMethod method) => method switch {
    ZipCompressionMethod.Store or
    ZipCompressionMethod.Shrink or
    ZipCompressionMethod.Reduce1 or
    ZipCompressionMethod.Reduce2 or
    ZipCompressionMethod.Reduce3 or
    ZipCompressionMethod.Reduce4 or
    ZipCompressionMethod.Implode => ZipConstants.VersionNeeded10,
    ZipCompressionMethod.Deflate => ZipConstants.VersionNeeded20,
    ZipCompressionMethod.Deflate64 => ZipConstants.VersionNeeded21,
    ZipCompressionMethod.BZip2 => ZipConstants.VersionNeeded46,
    ZipCompressionMethod.Lzma or
    ZipCompressionMethod.Zstd or
    ZipCompressionMethod.Ppmd => ZipConstants.VersionNeeded63,
    ZipCompressionMethod.WinZipAes => ZipConstants.VersionNeeded51,
    _ => throw new NotSupportedException($"ZIP compression method {method} has no known compatibility profile."),
  };

  private static ushort Max(ushort left, ushort right) => left >= right ? left : right;
}
