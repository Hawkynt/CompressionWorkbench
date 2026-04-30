namespace FileFormat.Ypf;

/// <summary>
/// 32-bit name hash used inside YPF v480 entry records. Real YukaScript readers don't
/// strictly validate this hash, so any deterministic hash works for round-trip self-consistency.
/// We use a simple <c>h * 0x1003F + lower(c)</c> rolling hash — case-insensitive so writers
/// don't need to normalize file names.
/// </summary>
public static class YpfHash {

  /// <summary>Computes the 32-bit YPF name hash for <paramref name="name"/>.</summary>
  public static uint Hash(string name) {
    ArgumentNullException.ThrowIfNull(name);
    var h = 0u;
    foreach (var c in name)
      h = (h * 0x1003Fu) + char.ToLowerInvariant(c);
    return h;
  }
}
