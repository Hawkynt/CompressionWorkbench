namespace FileSystem.HfsPlus;

/// <summary>
/// Converts between the Unicode name an HFS+ catalog key stores and the POSIX name the rest of
/// this library uses, where '/' separates path components.
/// </summary>
/// <remarks>
/// The catalog stores names as the Finder shows them, so a name may contain '/' and never a
/// path separator of its own; the POSIX view shows that '/' as ':', and a ':' typed in a POSIX
/// shell is stored as '/'. U+0000, which the metadata directory's name starts with, is shown as
/// U+2400 SYMBOL FOR NULL the same way. Sources: Apple TN1150 "HFS Plus Volume Format" (catalog
/// key names, metadata directory) and the libyal libfshfs format documentation ("The characters
/// ':' and U+2400 are stored as '/' and U+0 respectively"); libfshfs 20260922 lists the keramics
/// volume's "forward:slash" and "␀␀␀␀HFS+ Private Data" that way.
/// </remarks>
internal static class HfsPlusName {

  /// <summary>The POSIX form of a catalog name.</summary>
  public static string FromCatalog(string catalogName)
    => catalogName.AsSpan().IndexOfAny('/', '\0') < 0
      ? catalogName
      : string.Create(catalogName.Length, catalogName, static (span, source) => {
        for (var i = 0; i < source.Length; ++i)
          span[i] = source[i] switch { '/' => ':', '\0' => '␀', var c => c };
      });

  /// <summary>The catalog form of a POSIX name.</summary>
  public static string ToCatalog(string posixName)
    => posixName.AsSpan().IndexOfAny(':', '␀') < 0
      ? posixName
      : string.Create(posixName.Length, posixName, static (span, source) => {
        for (var i = 0; i < source.Length; ++i)
          span[i] = source[i] switch { ':' => '/', '␀' => '\0', var c => c };
      });
}
