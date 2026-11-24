using System.Buffers.Binary;

namespace Compression.Core.Crypto;

/// <summary>
/// The Poly1305 one-time authenticator of RFC 8439 §2.5: the message, cut into 16-byte blocks each
/// with a 1 bit appended, is evaluated as a polynomial in the clamped half <c>r</c> of the 32-byte
/// key modulo 2¹³⁰−5, and the other half <c>s</c> is added modulo 2¹²⁸ to give a 16-byte tag.
/// </summary>
/// <remarks>
/// Written from RFC 8439 and checked against its test vectors. The accumulator is held in five
/// 26-bit limbs so every product fits a 64-bit word. A key must authenticate one message only.
/// Data may arrive in pieces of any length (<see cref="Update"/>); the tag is the same as for the
/// concatenation.
/// </remarks>
public sealed class Poly1305 {
  /// <summary>Key length in bytes.</summary>
  public const int KeySize = 32;
  /// <summary>Tag length in bytes.</summary>
  public const int TagSize = 16;

  private const uint Mask26 = 0x3FFFFFF;
  private readonly uint _r0, _r1, _r2, _r3, _r4;
  private readonly uint _s0, _s1, _s2, _s3;
  private uint _h0, _h1, _h2, _h3, _h4;
  private readonly byte[] _pending = new byte[16];
  private int _pendingLength;

  /// <summary>Keys an authenticator; <paramref name="key"/> is <c>r ‖ s</c>, 32 bytes.</summary>
  public Poly1305(ReadOnlySpan<byte> key) {
    if (key.Length != KeySize) throw new ArgumentException($"Poly1305 keys are {KeySize} bytes.", nameof(key));
    var t0 = BinaryPrimitives.ReadUInt32LittleEndian(key);
    var t1 = BinaryPrimitives.ReadUInt32LittleEndian(key[4..]);
    var t2 = BinaryPrimitives.ReadUInt32LittleEndian(key[8..]);
    var t3 = BinaryPrimitives.ReadUInt32LittleEndian(key[12..]);
    // r split into 26-bit limbs with RFC 8439's clamp (r &= 0x0ffffffc0ffffffc0ffffffc0fffffff) folded in.
    this._r0 = t0 & 0x3FFFFFF;
    this._r1 = ((t0 >> 26) | (t1 << 6)) & 0x3FFFF03;
    this._r2 = ((t1 >> 20) | (t2 << 12)) & 0x3FFC0FF;
    this._r3 = ((t2 >> 14) | (t3 << 18)) & 0x3F03FFF;
    this._r4 = (t3 >> 8) & 0x00FFFFF;
    this._s0 = BinaryPrimitives.ReadUInt32LittleEndian(key[16..]);
    this._s1 = BinaryPrimitives.ReadUInt32LittleEndian(key[20..]);
    this._s2 = BinaryPrimitives.ReadUInt32LittleEndian(key[24..]);
    this._s3 = BinaryPrimitives.ReadUInt32LittleEndian(key[28..]);
  }

  /// <summary>Feeds more of the message.</summary>
  public void Update(ReadOnlySpan<byte> data) {
    if (this._pendingLength > 0) {
      var take = Math.Min(16 - this._pendingLength, data.Length);
      data[..take].CopyTo(this._pending.AsSpan(this._pendingLength));
      this._pendingLength += take;
      data = data[take..];
      if (this._pendingLength < 16) return;
      this.Blocks(this._pending, 1u << 24);
      this._pendingLength = 0;
    }
    var whole = data.Length & ~15;
    if (whole > 0) this.Blocks(data[..whole], 1u << 24);
    data[whole..].CopyTo(this._pending);
    this._pendingLength = data.Length - whole;
  }

  /// <summary>Finishes the message and writes the 16-byte tag.</summary>
  public void Final(Span<byte> tag) {
    if (tag.Length < TagSize) throw new ArgumentException($"A Poly1305 tag is {TagSize} bytes.", nameof(tag));
    if (this._pendingLength > 0) {
      // The last, short block carries its 1 bit right after its bytes rather than at 2^128.
      this._pending[this._pendingLength] = 1;
      this._pending.AsSpan(this._pendingLength + 1).Clear();
      this.Blocks(this._pending, 0);
      this._pendingLength = 0;
    }

    uint h0 = this._h0, h1 = this._h1, h2 = this._h2, h3 = this._h3, h4 = this._h4;
    // Carry fully.
    var c = h1 >> 26; h1 &= Mask26; h2 += c;
    c = h2 >> 26; h2 &= Mask26; h3 += c;
    c = h3 >> 26; h3 &= Mask26; h4 += c;
    c = h4 >> 26; h4 &= Mask26; h0 += c * 5;
    c = h0 >> 26; h0 &= Mask26; h1 += c;

    // h − p = h + 5 − 2^130; keep it when it does not go negative.
    var g0 = h0 + 5; c = g0 >> 26; g0 &= Mask26;
    var g1 = h1 + c; c = g1 >> 26; g1 &= Mask26;
    var g2 = h2 + c; c = g2 >> 26; g2 &= Mask26;
    var g3 = h3 + c; c = g3 >> 26; g3 &= Mask26;
    var g4 = unchecked(h4 + c - (1u << 26));
    var keepG = unchecked((g4 >> 31) - 1);
    h0 = (h0 & ~keepG) | (g0 & keepG);
    h1 = (h1 & ~keepG) | (g1 & keepG);
    h2 = (h2 & ~keepG) | (g2 & keepG);
    h3 = (h3 & ~keepG) | (g3 & keepG);
    h4 = (h4 & ~keepG) | (g4 & keepG);

    // To four 32-bit words, then + s modulo 2^128.
    var w0 = h0 | (h1 << 26);
    var w1 = (h1 >> 6) | (h2 << 20);
    var w2 = (h2 >> 12) | (h3 << 14);
    var w3 = (h3 >> 18) | (h4 << 8);
    ulong f = (ulong)w0 + this._s0; BinaryPrimitives.WriteUInt32LittleEndian(tag, (uint)f);
    f = (ulong)w1 + this._s1 + (f >> 32); BinaryPrimitives.WriteUInt32LittleEndian(tag[4..], (uint)f);
    f = (ulong)w2 + this._s2 + (f >> 32); BinaryPrimitives.WriteUInt32LittleEndian(tag[8..], (uint)f);
    f = (ulong)w3 + this._s3 + (f >> 32); BinaryPrimitives.WriteUInt32LittleEndian(tag[12..], (uint)f);
  }

  /// <summary>The tag of <paramref name="message"/> under <paramref name="key"/>.</summary>
  public static byte[] ComputeTag(ReadOnlySpan<byte> key, ReadOnlySpan<byte> message) {
    var mac = new Poly1305(key);
    mac.Update(message);
    var tag = new byte[TagSize];
    mac.Final(tag);
    return tag;
  }

  /// <summary>Absorbs whole 16-byte blocks; <paramref name="highBit"/> is the 2^128 bit in limb 4.</summary>
  private void Blocks(ReadOnlySpan<byte> data, uint highBit) {
    uint r0 = this._r0, r1 = this._r1, r2 = this._r2, r3 = this._r3, r4 = this._r4;
    uint s1 = r1 * 5, s2 = r2 * 5, s3 = r3 * 5, s4 = r4 * 5;
    uint h0 = this._h0, h1 = this._h1, h2 = this._h2, h3 = this._h3, h4 = this._h4;
    for (var at = 0; at + 16 <= data.Length; at += 16) {
      var t0 = BinaryPrimitives.ReadUInt32LittleEndian(data[at..]);
      var t1 = BinaryPrimitives.ReadUInt32LittleEndian(data[(at + 4)..]);
      var t2 = BinaryPrimitives.ReadUInt32LittleEndian(data[(at + 8)..]);
      var t3 = BinaryPrimitives.ReadUInt32LittleEndian(data[(at + 12)..]);
      h0 += t0 & Mask26;
      h1 += ((t0 >> 26) | (t1 << 6)) & Mask26;
      h2 += ((t1 >> 20) | (t2 << 12)) & Mask26;
      h3 += ((t2 >> 14) | (t3 << 18)) & Mask26;
      h4 += (t3 >> 8) | highBit;

      // h *= r (mod 2^130 - 5): limbs above 2^130 come back multiplied by 5.
      var d0 = (ulong)h0 * r0 + (ulong)h1 * s4 + (ulong)h2 * s3 + (ulong)h3 * s2 + (ulong)h4 * s1;
      var d1 = (ulong)h0 * r1 + (ulong)h1 * r0 + (ulong)h2 * s4 + (ulong)h3 * s3 + (ulong)h4 * s2;
      var d2 = (ulong)h0 * r2 + (ulong)h1 * r1 + (ulong)h2 * r0 + (ulong)h3 * s4 + (ulong)h4 * s3;
      var d3 = (ulong)h0 * r3 + (ulong)h1 * r2 + (ulong)h2 * r1 + (ulong)h3 * r0 + (ulong)h4 * s4;
      var d4 = (ulong)h0 * r4 + (ulong)h1 * r3 + (ulong)h2 * r2 + (ulong)h3 * r1 + (ulong)h4 * r0;

      var c = d0 >> 26; h0 = (uint)d0 & Mask26;
      d1 += c; c = d1 >> 26; h1 = (uint)d1 & Mask26;
      d2 += c; c = d2 >> 26; h2 = (uint)d2 & Mask26;
      d3 += c; c = d3 >> 26; h3 = (uint)d3 & Mask26;
      d4 += c; c = d4 >> 26; h4 = (uint)d4 & Mask26;
      var folded = h0 + c * 5;
      h0 = (uint)folded & Mask26;
      h1 += (uint)(folded >> 26);
    }
    this._h0 = h0; this._h1 = h1; this._h2 = h2; this._h3 = h3; this._h4 = h4;
  }
}
