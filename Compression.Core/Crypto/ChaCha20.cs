using System.Buffers.Binary;
using System.Numerics;

namespace Compression.Core.Crypto;

/// <summary>
/// The ChaCha20 stream cipher of RFC 8439 §2.1–2.4: a 256-bit key, a 32-bit block counter and a
/// 96-bit nonce make a 16-word state; twenty rounds (ten column/diagonal double rounds) plus the
/// input state give a 64-byte keystream block, XOR-ed onto the data. Encryption and decryption are
/// the same operation.
/// </summary>
/// <remarks>
/// Written from RFC 8439 and checked against its test vectors. The state is laid out as the RFC
/// says: the four constant words, eight key words, the counter word and three nonce words. Some
/// formats (bcachefs among them) hand the cipher a 16-byte block of "counter ‖ nonce" instead;
/// <see cref="Transform(ReadOnlySpan{byte}, ReadOnlySpan{byte}, Span{byte})"/> takes that form.
/// The counter wraps after 2³² blocks, as in the RFC's single-counter variant.
/// </remarks>
public static class ChaCha20 {
  /// <summary>Key length in bytes.</summary>
  public const int KeySize = 32;
  /// <summary>Nonce length in bytes (RFC 8439's 96-bit nonce).</summary>
  public const int NonceSize = 12;
  /// <summary>Keystream block length in bytes.</summary>
  public const int BlockSize = 64;

  // "expand 32-byte k" as four little-endian words (RFC 8439 §2.3).
  private const uint Sigma0 = 0x61707865, Sigma1 = 0x3320646E, Sigma2 = 0x79622D32, Sigma3 = 0x6B206574;

  /// <summary>Computes one 64-byte keystream block (the RFC's <c>chacha20_block</c>).</summary>
  public static void Block(ReadOnlySpan<byte> key, uint counter, ReadOnlySpan<byte> nonce, Span<byte> output) {
    CheckKey(key);
    if (nonce.Length != NonceSize) throw new ArgumentException($"ChaCha20 nonces are {NonceSize} bytes.", nameof(nonce));
    if (output.Length < BlockSize) throw new ArgumentException($"A ChaCha20 block is {BlockSize} bytes.", nameof(output));
    Span<uint> state = stackalloc uint[16];
    Initialise(state, key, counter, nonce);
    Span<uint> working = stackalloc uint[16];
    Core(state, working);
    for (var i = 0; i < 16; ++i)
      BinaryPrimitives.WriteUInt32LittleEndian(output[(i * 4)..], working[i]);
  }

  /// <summary>
  /// XORs the keystream that starts at block <paramref name="counter"/> onto <paramref name="data"/>
  /// in place (the RFC's <c>chacha20_encrypt</c>).
  /// </summary>
  public static void Transform(ReadOnlySpan<byte> key, uint counter, ReadOnlySpan<byte> nonce, Span<byte> data) {
    CheckKey(key);
    if (nonce.Length != NonceSize) throw new ArgumentException($"ChaCha20 nonces are {NonceSize} bytes.", nameof(nonce));
    Span<uint> state = stackalloc uint[16];
    Initialise(state, key, counter, nonce);
    Apply(state, data);
  }

  /// <summary>
  /// XORs the keystream onto <paramref name="data"/>, taking the 16-byte initial block of
  /// <paramref name="iv"/> as "counter (little-endian word) ‖ 96-bit nonce".
  /// </summary>
  public static void Transform(ReadOnlySpan<byte> key, ReadOnlySpan<byte> iv, Span<byte> data) {
    if (iv.Length != 16) throw new ArgumentException("The ChaCha20 counter-and-nonce block is 16 bytes.", nameof(iv));
    Transform(key, BinaryPrimitives.ReadUInt32LittleEndian(iv), iv[4..], data);
  }

  private static void CheckKey(ReadOnlySpan<byte> key) {
    if (key.Length != KeySize) throw new ArgumentException($"ChaCha20 keys are {KeySize} bytes.", nameof(key));
  }

  private static void Initialise(Span<uint> state, ReadOnlySpan<byte> key, uint counter, ReadOnlySpan<byte> nonce) {
    state[0] = Sigma0; state[1] = Sigma1; state[2] = Sigma2; state[3] = Sigma3;
    for (var i = 0; i < 8; ++i)
      state[4 + i] = BinaryPrimitives.ReadUInt32LittleEndian(key[(i * 4)..]);
    state[12] = counter;
    for (var i = 0; i < 3; ++i)
      state[13 + i] = BinaryPrimitives.ReadUInt32LittleEndian(nonce[(i * 4)..]);
  }

  private static void Apply(Span<uint> state, Span<byte> data) {
    Span<uint> working = stackalloc uint[16];
    Span<byte> stream = stackalloc byte[BlockSize];
    for (var at = 0; at < data.Length; at += BlockSize) {
      Core(state, working);
      for (var i = 0; i < 16; ++i)
        BinaryPrimitives.WriteUInt32LittleEndian(stream[(i * 4)..], working[i]);
      var chunk = data.Slice(at, Math.Min(BlockSize, data.Length - at));
      for (var i = 0; i < chunk.Length; ++i)
        chunk[i] ^= stream[i];
      unchecked { ++state[12]; }
    }
  }

  /// <summary>Twenty rounds over a copy of <paramref name="state"/>, plus the state itself.</summary>
  private static void Core(ReadOnlySpan<uint> state, Span<uint> x) {
    state.CopyTo(x);
    for (var round = 0; round < 10; ++round) {
      QuarterRound(x, 0, 4, 8, 12);
      QuarterRound(x, 1, 5, 9, 13);
      QuarterRound(x, 2, 6, 10, 14);
      QuarterRound(x, 3, 7, 11, 15);
      QuarterRound(x, 0, 5, 10, 15);
      QuarterRound(x, 1, 6, 11, 12);
      QuarterRound(x, 2, 7, 8, 13);
      QuarterRound(x, 3, 4, 9, 14);
    }
    for (var i = 0; i < 16; ++i)
      x[i] = unchecked(x[i] + state[i]);
  }

  private static void QuarterRound(Span<uint> x, int a, int b, int c, int d) {
    unchecked {
      x[a] += x[b]; x[d] = BitOperations.RotateLeft(x[d] ^ x[a], 16);
      x[c] += x[d]; x[b] = BitOperations.RotateLeft(x[b] ^ x[c], 12);
      x[a] += x[b]; x[d] = BitOperations.RotateLeft(x[d] ^ x[a], 8);
      x[c] += x[d]; x[b] = BitOperations.RotateLeft(x[b] ^ x[c], 7);
    }
  }
}
