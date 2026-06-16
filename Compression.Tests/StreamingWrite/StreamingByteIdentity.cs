namespace Compression.Tests.StreamingWrite;

/// <summary>
/// Byte-identity assertion for filesystem writers whose images embed
/// format-mandated nondeterministic bytes (random UUIDs / wall-clock
/// timestamps). It diffs two classic builds to discover the nondeterministic
/// byte ranges, then asserts the streamed build equals a classic build on every
/// OTHER byte — proving the structural metadata and the file-data placement are
/// byte-identical between the buffered and streaming paths. A short retry loop
/// absorbs the 1-second timestamp drift that can occur when the reference pair
/// and the streamed build straddle a clock-second boundary.
/// </summary>
internal static class StreamingByteIdentity {
  public static void AssertMatchesClassic(Func<byte[]> buildClassic, Func<byte[]> buildStreamed) {
    string? lastFailure = null;
    for (var attempt = 0; attempt < 5; attempt++) {
      // Two classic builds back-to-back → the per-byte mask of nondeterministic
      // bytes (UUID + any timestamp byte that ticked over between them).
      var c1 = buildClassic();
      var c2 = buildClassic();
      if (c1.Length != c2.Length) { lastFailure = "classic builds differ in length"; continue; }

      var mask = new bool[c1.Length];
      for (var i = 0; i < c1.Length; i++)
        if (c1[i] != c2[i]) mask[i] = true;

      var streamed = buildStreamed();
      if (streamed.Length != c1.Length) {
        lastFailure = $"streamed length {streamed.Length} != classic length {c1.Length}";
        continue;
      }

      var ok = true;
      var firstDiff = -1;
      for (var i = 0; i < c1.Length; i++) {
        if (mask[i]) continue;
        if (streamed[i] != c1[i]) { ok = false; firstDiff = i; break; }
      }
      if (ok) return; // byte-identical outside the nondeterministic mask
      lastFailure =
        $"byte {firstDiff} differs outside the nondeterministic mask: " +
        $"streamed=0x{streamed[firstDiff]:X2} classic=0x{c1[firstDiff]:X2}";
    }
    Assert.Fail($"streaming build never matched a classic build outside the nondeterministic mask. Last: {lastFailure}");
  }
}
