using Compression.Core.Checksums;

namespace FileFormat.Uharc;

/// <summary>
/// The two UHARC 0.6 scramblers (docs/UHARC-ON-DISK.md, "Header scrambling" and
/// "Table cipher").
/// </summary>
internal static class UharcCipher {

  /// <summary>Password of every archive created without <c>-pw</c>.</summary>
  public static ReadOnlySpan<byte> DefaultPassword => "UHARC Default Password"u8;

  /// <summary>
  /// Undoes the header scrambling in place: byte <c>4 + i</c> of the 31-byte field block is XORed
  /// with <c>key + i*i</c>, where the key is the low byte of the stored header check.
  /// </summary>
  public static void DescrambleHeader(Span<byte> header, byte key) {
    for (var i = 0; i < UharcHeader.ScrambledLength; ++i)
      header[UharcHeader.ScrambledOffset + i] ^= (byte)(key + i * i);
  }

  /// <summary>Applies the self-inverse table/stream cipher keyed by <paramref name="password"/> in place.</summary>
  public static void Apply(Span<byte> data, ReadOnlySpan<byte> password) {
    var state = KeySchedule(password);
    var position = 0;
    var mix = 0xFE;
    foreach (ref var b in data) {
      var previous = state[position];
      position = (position + 1) & 0xFF;
      var current = state[position];
      mix = (mix + 1) & 0xFF;
      var key = (byte)(current + previous);
      state[position] = key;
      b ^= key;
      state[mix] -= current;
    }
  }

  private static byte[] KeySchedule(ReadOnlySpan<byte> password) {
    if (password.IsEmpty)
      throw new ArgumentException("UHARC: empty password.", nameof(password));

    var state = new byte[256];
    state[0] = (byte)password.Length;
    for (var i = 1; i < 256; ++i)
      state[i] = password[(i - 1) % password.Length];

    for (var offset = 0; offset < 256; offset += 8) {
      // The schedule uses the CRC register before its final complement.
      var register = ~Crc32.Compute(state);
      for (var k = 0; k < 4; ++k) {
        state[offset + k] ^= (byte)(register >> (8 * k));
        state[offset + 4 + k] ^= (byte)(~register >> (8 * k));
      }
    }

    return state;
  }
}
