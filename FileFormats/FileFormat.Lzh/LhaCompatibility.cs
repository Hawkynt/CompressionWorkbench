namespace FileFormat.Lzh;

/// <summary>
/// Historical archiver family/generation used to constrain which method IDs
/// the writer may emit. This is independent from the physical LHA header level.
/// </summary>
public enum LhaArchiverGeneration {
  /// <summary>Original LArc family: -lz4-, -lzs- and -lz5-.</summary>
  LArc,

  /// <summary>LHarc 1.x family: -lh0- and -lh1-.</summary>
  LhArc1,

  /// <summary>LHa / LHarc 2.x family: -lh0- through the LHa-era LH methods.</summary>
  Lha2,

  /// <summary>PMarc generation 1: -pm0- and -pm1-.</summary>
  PmArc1,

  /// <summary>PMarc generation 2: -pm0-, -pm1- and -pm2-.</summary>
  PmArc2,

  /// <summary>All method IDs implemented by this writer.</summary>
  Extended,
}

/// <summary>Physical LHA header layout, independent of compression-method generation.</summary>
public enum LhaHeaderLevel : byte {
  /// <summary>Traditional LArc/LHarc fixed header with a one-byte size/checksum.</summary>
  Level0 = LhaConstants.HeaderLevel0,

  /// <summary>LHa transitional header with OS id and extended-header chain.</summary>
  Level1 = LhaConstants.HeaderLevel1,

  /// <summary>LHa long-name header with 16-bit total size and UNIX timestamp.</summary>
  Level2 = LhaConstants.HeaderLevel2,
}

/// <summary>Compatibility rules for LHA/LZH method generations.</summary>
public static class LhaCompatibility {
  /// <summary>Returns whether the generation may emit the requested method ID.</summary>
  public static bool IsSupported(LhaArchiverGeneration generation, string method) => generation switch {
    LhaArchiverGeneration.LArc => method is LhaConstants.MethodLz4 or LhaConstants.MethodLzs or LhaConstants.MethodLz5,
    LhaArchiverGeneration.LhArc1 => method is LhaConstants.MethodLh0 or LhaConstants.MethodLh1,
    LhaArchiverGeneration.Lha2 => method is
      LhaConstants.MethodLh0 or
      LhaConstants.MethodLh1 or
      LhaConstants.MethodLh2 or
      LhaConstants.MethodLh3 or
      LhaConstants.MethodLh4 or
      LhaConstants.MethodLh5 or
      LhaConstants.MethodLh6 or
      LhaConstants.MethodLh7,
    LhaArchiverGeneration.PmArc1 => method is LhaConstants.MethodPm0 or LhaConstants.MethodPm1,
    LhaArchiverGeneration.PmArc2 => method is LhaConstants.MethodPm0 or LhaConstants.MethodPm1 or LhaConstants.MethodPm2,
    LhaArchiverGeneration.Extended => IsImplemented(method),
    _ => false,
  };

  /// <summary>Throws when a method is not valid for the selected archiver generation.</summary>
  public static void EnsureSupported(LhaArchiverGeneration generation, string method) {
    ArgumentNullException.ThrowIfNull(method);
    if (IsSupported(generation, method))
      return;

    throw new NotSupportedException($"LHA method '{method}' is not available in archiver generation {generation}.");
  }

  internal static bool IsImplemented(string method) => method is
    LhaConstants.MethodLh0 or
    LhaConstants.MethodLh1 or
    LhaConstants.MethodLh2 or
    LhaConstants.MethodLh3 or
    LhaConstants.MethodLh4 or
    LhaConstants.MethodLh5 or
    LhaConstants.MethodLh6 or
    LhaConstants.MethodLh7 or
    LhaConstants.MethodLz4 or
    LhaConstants.MethodLzs or
    LhaConstants.MethodLz5 or
    LhaConstants.MethodPm0 or
    LhaConstants.MethodPm1 or
    LhaConstants.MethodPm2;
}
