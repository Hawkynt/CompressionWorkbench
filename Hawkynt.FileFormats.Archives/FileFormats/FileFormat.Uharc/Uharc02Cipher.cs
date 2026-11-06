using Compression.Core.Checksums;

namespace FileFormat.Uharc;

/// <summary>
/// UHARC 0.2's table cipher (docs/UHARC-ON-DISK.md, "UHARC 0.2"): a 256-byte state filled with the
/// default password repeated, keyed by CRC rounds like the 0.6 cipher, and a single-index
/// keystream.
/// </summary>
internal static class Uharc02Cipher {

  private static readonly byte[] InitialState = BuildState();

  private static byte[] BuildState() {
    var password = UharcCipher.DefaultPassword;
    var state = new byte[256];
    for (var i = 0; i < state.Length; ++i)
      state[i] = password[i % password.Length];

    for (var offset = 0; offset < 256; offset += 8) {
      var register = ~Crc32.Compute(state);
      for (var k = 0; k < 4; ++k) {
        state[offset + k] ^= (byte)(register >> (8 * k));
        state[offset + 4 + k] ^= (byte)(~register >> (8 * k));
      }
    }

    return state;
  }

  /// <summary>Deciphers (or enciphers) one table in place; every table starts afresh.</summary>
  public static void Apply(Span<byte> data) {
    Span<byte> state = stackalloc byte[256];
    InitialState.CopyTo(state);
    byte position = 0;
    for (var i = 0; i < data.Length; ++i) {
      var a = state[position];
      ++position;
      state[position] += a;
      data[i] ^= state[position];
    }
  }
}
