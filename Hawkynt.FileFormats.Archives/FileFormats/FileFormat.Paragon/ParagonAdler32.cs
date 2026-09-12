#pragma warning disable CS1591
namespace FileFormat.Paragon;

/// <summary>
/// Adler-32 checksum (RFC 1950) — the zlib stream checksum the vendor's
/// PBF reader uses on each chunk ("Chunk is not valid, adler32 checksum is
/// wrong." debug string from Wave-13 reverse-engineering of
/// <c>hdmengine_hdmsdk.dll</c>). Local clean-room implementation kept
/// inside this project so the PBF format doesn't need to pull in
/// <c>Compression.Core</c>.
/// </summary>
internal static class ParagonAdler32 {

  private const uint Mod = 65521;
  private const int Nmax = 5552;

  /// <summary>Computes the Adler-32 of <paramref name="data"/>.</summary>
  public static uint Compute(ReadOnlySpan<byte> data) {
    uint a = 1, b = 0;
    var remaining = data.Length;
    var offset = 0;
    while (remaining > 0) {
      var blockLen = Math.Min(remaining, Nmax);
      for (var i = 0; i < blockLen; i++) {
        a += data[offset + i];
        b += a;
      }
      a %= Mod;
      b %= Mod;
      offset += blockLen;
      remaining -= blockLen;
    }
    return (b << 16) | a;
  }
}
