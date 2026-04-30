namespace FileFormat.Sarc;

/// <summary>
/// SARC filename hash. The SARC SFAT stores entries sorted by this hash so that
/// the Switch SDK can perform a binary search by name without parsing strings.
/// </summary>
public static class SarcHash {

  /// <summary>
  /// Computes the SARC name hash. Each character is treated as a SIGNED byte
  /// before being added — high-bit characters (e.g. UTF-8 continuation bytes
  /// in non-ASCII paths) sign-extend to 0xFFFFFFxx, which is how the official
  /// hash differs from a plain unsigned-byte rolling hash.
  /// </summary>
  /// <param name="name">The filename / archive path to hash.</param>
  /// <param name="hashKey">The polynomial multiplier (0x65 / 101 by convention).</param>
  /// <returns>A 32-bit hash matching the Switch SDK lookup convention.</returns>
  public static uint Hash(string name, uint hashKey) {
    ArgumentNullException.ThrowIfNull(name);

    var result = 0u;
    var bytes = System.Text.Encoding.UTF8.GetBytes(name);
    foreach (var b in bytes)
      result = result * hashKey + (uint)(sbyte)b;
    return result;
  }
}
