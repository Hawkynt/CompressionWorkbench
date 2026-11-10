#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace FileFormat.BitLocker;

/// <summary>
/// BitLocker's sector transforms, each sector keyed by its IV sector number <c>s</c> (in units of
/// the sector size, as dm-crypt counts with <c>iv_large_sectors</c>):
/// <list type="bullet">
/// <item>AES-XTS: tweak = little-endian <c>s</c> (IEEE 1619, plain64).</item>
/// <item>AES-CBC: IV = AES(K, e(s)), e(s) = byte offset <c>s·sectorSize</c> little-endian in 16 bytes.</item>
/// <item>AES-CBC + Elephant: the same CBC layer over the diffusers and the sector key of
/// Ferguson's "AES-CBC + Elephant diffuser" (2006), sections 4.2–4.4.</item>
/// </list>
/// Block work runs through cached ECB/CBC transforms over whole buffers, never one call per block.
/// </summary>
public sealed class BitLockerSectorCipher : IDisposable {
  private const int Block = 16;

  // Ferguson 2006, section 4.4: rotation constants and cycle counts of the two diffusers.
  private static readonly int[] RotationsA = [9, 0, 13, 0];
  private static readonly int[] RotationsB = [0, 10, 0, 25];
  private const int CyclesA = 5;
  private const int CyclesB = 3;

  private readonly BitLockerCipherMode _mode;
  private readonly int _sectorSize;
  private readonly Aes _dataAes;
  private readonly Aes _tweakAes;   // XTS: second key; CBC: the data key again (IV); Elephant: the sector-key key
  private readonly ICryptoTransform _ecbEncrypt, _ecbDecrypt, _cbcEncrypt, _tweakEncrypt;
  private readonly ICryptoTransform? _ivEncrypt;   // Elephant: IVs come from the CBC key, sector keys from the diffuser key
  private readonly Aes? _ivAes;
  private readonly byte[] _cbcState = new byte[Block];
  private byte[] _scratch = [];
  private byte[] _tweaks = [];

  /// <param name="mode">The transform.</param>
  /// <param name="key">The FVEK as dm-crypt takes it: XTS 32/64 bytes (data ‖ tweak key), CBC 16/32, Elephant 32/64 (CBC ‖ sector-key key).</param>
  /// <param name="sectorSize">512 or 4096.</param>
  public BitLockerSectorCipher(BitLockerCipherMode mode, ReadOnlySpan<byte> key, int sectorSize) {
    if (sectorSize < 512 || sectorSize > 8192 || (sectorSize & (sectorSize - 1)) != 0)
      throw new ArgumentOutOfRangeException(nameof(sectorSize), "BitLocker sectors are 512 to 8192 bytes.");
    this._mode = mode;
    this._sectorSize = sectorSize;
    ReadOnlySpan<byte> dataKey, tweakKey;
    switch (mode) {
      case BitLockerCipherMode.XtsPlain64 or BitLockerCipherMode.CbcElephant:
        if (key.Length is not (32 or 64)) throw new ArgumentException($"{mode} needs a 256- or 512-bit key.", nameof(key));
        dataKey = key[..(key.Length / 2)];
        tweakKey = key[(key.Length / 2)..];
        break;
      default:
        if (key.Length is not (16 or 32)) throw new ArgumentException("AES-CBC needs a 128- or 256-bit key.", nameof(key));
        dataKey = key;
        tweakKey = key;
        break;
    }
    this._dataAes = NewAes(dataKey);
    this._tweakAes = NewAes(tweakKey);
    this._ecbEncrypt = this._dataAes.CreateEncryptor();
    this._ecbDecrypt = this._dataAes.CreateDecryptor();
    this._tweakEncrypt = this._tweakAes.CreateEncryptor();
    // A CBC encryptor whose chaining value we steer: its IV is zero, and each sector's first
    // block is pre-XORed with (its IV ⊕ the transform's previous ciphertext block).
    using (var cbc = Aes.Create()) {
      cbc.Key = dataKey.ToArray();
      cbc.Mode = CipherMode.CBC;
      cbc.Padding = PaddingMode.None;
      cbc.IV = new byte[Block];
      this._cbcEncrypt = cbc.CreateEncryptor();
    }
    if (mode == BitLockerCipherMode.CbcElephant) {
      this._ivAes = NewAes(dataKey);
      this._ivEncrypt = this._ivAes.CreateEncryptor();
    }
  }

  public BitLockerCipherMode Mode => this._mode;
  public int SectorSize => this._sectorSize;

  private static Aes NewAes(ReadOnlySpan<byte> key) {
    var aes = Aes.Create();
    aes.Key = key.ToArray();
    aes.Mode = CipherMode.ECB;
    aes.Padding = PaddingMode.None;
    return aes;
  }

  /// <summary>Decrypts whole sectors in place; <paramref name="firstSector"/> is the IV sector number of the first.</summary>
  public void Decrypt(Span<byte> data, long firstSector) {
    this.CheckLength(data);
    switch (this._mode) {
      case BitLockerCipherMode.XtsPlain64:
        this.Xts(data, firstSector, encrypt: false);
        break;
      case BitLockerCipherMode.CbcEboiv:
        this.CbcDecrypt(data, firstSector, this._tweakEncrypt);
        break;
      default:
        this.CbcDecrypt(data, firstSector, this._ivEncrypt!);
        for (var off = 0; off < data.Length; off += this._sectorSize) {
          var words = Words(data.Slice(off, this._sectorSize));
          DiffuserBDecrypt(words);
          DiffuserADecrypt(words);
          Words(words);
        }
        this.XorSectorKeys(data, firstSector);
        break;
    }
  }

  /// <summary>Encrypts whole sectors in place; <paramref name="firstSector"/> is the IV sector number of the first.</summary>
  public void Encrypt(Span<byte> data, long firstSector) {
    this.CheckLength(data);
    switch (this._mode) {
      case BitLockerCipherMode.XtsPlain64:
        this.Xts(data, firstSector, encrypt: true);
        break;
      case BitLockerCipherMode.CbcEboiv:
        this.CbcEncrypt(data, firstSector, this._tweakEncrypt);
        break;
      default:
        this.XorSectorKeys(data, firstSector);
        for (var off = 0; off < data.Length; off += this._sectorSize) {
          var words = Words(data.Slice(off, this._sectorSize));
          DiffuserAEncrypt(words);
          DiffuserBEncrypt(words);
          Words(words);
        }
        this.CbcEncrypt(data, firstSector, this._ivEncrypt!);
        break;
    }
  }

  private void CheckLength(ReadOnlySpan<byte> data) {
    if (data.Length % this._sectorSize != 0)
      throw new ArgumentException("BitLocker data must be whole sectors.", nameof(data));
  }

  private Span<byte> Scratch(int length) {
    if (this._scratch.Length < length) {
      CryptographicOperations.ZeroMemory(this._scratch);
      this._scratch = new byte[length];
    }
    return this._scratch.AsSpan(0, length);
  }

  private Span<byte> Tweaks(int length) {
    if (this._tweaks.Length < length) this._tweaks = new byte[length];
    return this._tweaks.AsSpan(0, length);
  }

  private static void Transform(ICryptoTransform transform, byte[] buffer, int length)
    => transform.TransformBlock(buffer, 0, length, buffer, 0);

  // ── XTS ─────────────────────────────────────────────────────────────────

  /// <summary>
  /// XTS over every sector of <paramref name="data"/>: the initial tweaks of all sectors are made
  /// in one ECB pass, expanded by repeated multiplication by α, XORed around one bulk ECB pass.
  /// </summary>
  private void Xts(Span<byte> data, long firstSector, bool encrypt) {
    var sectors = data.Length / this._sectorSize;
    var tweaks = this.Tweaks(data.Length);
    var seeds = this.Scratch(sectors * Block);
    seeds.Clear();
    for (var i = 0; i < sectors; ++i)
      BinaryPrimitives.WriteInt64LittleEndian(seeds[(i * Block)..], firstSector + i);
    Transform(this._tweakEncrypt, this._scratch, sectors * Block);
    var tweakWords = MemoryMarshal.Cast<byte, ulong>(tweaks);
    var seedWords = MemoryMarshal.Cast<byte, ulong>(seeds);
    var perSector = this._sectorSize / Block;
    for (var s = 0; s < sectors; ++s) {
      var lo = seedWords[2 * s];
      var hi = seedWords[2 * s + 1];
      if (!BitConverter.IsLittleEndian) { lo = BinaryPrimitives.ReverseEndianness(lo); hi = BinaryPrimitives.ReverseEndianness(hi); }
      var at = s * perSector * 2;
      for (var b = 0; b < perSector; ++b) {
        tweakWords[at + 2 * b] = BitConverter.IsLittleEndian ? lo : BinaryPrimitives.ReverseEndianness(lo);
        tweakWords[at + 2 * b + 1] = BitConverter.IsLittleEndian ? hi : BinaryPrimitives.ReverseEndianness(hi);
        // tweak ·= α in GF(2^128), little-endian (IEEE 1619).
        var carry = hi >> 63;
        hi = (hi << 1) | (lo >> 63);
        lo = (lo << 1) ^ (carry * 0x87);
      }
    }
    var buffer = this.Scratch(data.Length);
    Xor(data, tweaks, buffer);
    Transform(encrypt ? this._ecbEncrypt : this._ecbDecrypt, this._scratch, data.Length);
    Xor(buffer, tweaks, data);
  }

  // ── CBC with an encrypted-byte-offset IV ────────────────────────────────

  /// <summary>IVs of all sectors: AES(<paramref name="ivTransform"/>, byte offset ‖ 0⁶⁴), in one ECB pass.</summary>
  private Span<byte> Ivs(int sectors, long firstSector, ICryptoTransform ivTransform) {
    var ivs = this.Tweaks(sectors * Block);
    ivs.Clear();
    for (var i = 0; i < sectors; ++i)
      BinaryPrimitives.WriteInt64LittleEndian(ivs[(i * Block)..], (firstSector + i) * this._sectorSize);
    Transform(ivTransform, this._tweaks, sectors * Block);
    return ivs;
  }

  /// <summary>CBC decryption is parallel: one bulk ECB pass, then each block XORed with its predecessor.</summary>
  private void CbcDecrypt(Span<byte> data, long firstSector, ICryptoTransform ivTransform) {
    var sectors = data.Length / this._sectorSize;
    var ivs = this.Ivs(sectors, firstSector, ivTransform);
    var buffer = this.Scratch(data.Length);
    data.CopyTo(buffer);
    Transform(this._ecbDecrypt, this._scratch, data.Length);
    for (var s = 0; s < sectors; ++s) {
      var off = s * this._sectorSize;
      var sector = data.Slice(off, this._sectorSize);
      var plain = buffer.Slice(off, this._sectorSize);
      Xor(plain[Block..], sector[..^Block], plain[Block..]);
      Xor(plain[..Block], ivs.Slice(s * Block, Block), plain[..Block]);
    }
    buffer.CopyTo(data);
  }

  /// <summary>CBC encryption is serial within a sector; one steered CBC transform call per sector.</summary>
  private void CbcEncrypt(Span<byte> data, long firstSector, ICryptoTransform ivTransform) {
    var sectors = data.Length / this._sectorSize;
    var ivs = this.Ivs(sectors, firstSector, ivTransform);
    var buffer = this.Scratch(this._sectorSize);
    for (var s = 0; s < sectors; ++s) {
      var sector = data.Slice(s * this._sectorSize, this._sectorSize);
      sector.CopyTo(buffer);
      Xor(buffer[..Block], ivs.Slice(s * Block, Block), buffer[..Block]);
      Xor(buffer[..Block], this._cbcState, buffer[..Block]);
      Transform(this._cbcEncrypt, this._scratch, this._sectorSize);
      buffer[^Block..].CopyTo(this._cbcState);
      buffer.CopyTo(sector);
    }
  }

  // ── Elephant ────────────────────────────────────────────────────────────

  /// <summary>
  /// XORs each sector with its sector key K_s = AES(K_sec, e(s)) ‖ AES(K_sec, e'(s)), where e'(s)
  /// is e(s) with its last byte set to 128, repeated over the sector (Ferguson 2006, 4.3).
  /// </summary>
  private void XorSectorKeys(Span<byte> data, long firstSector) {
    var sectors = data.Length / this._sectorSize;
    var keys = this.Tweaks(sectors * 2 * Block);
    keys.Clear();
    for (var i = 0; i < sectors; ++i) {
      var offset = (firstSector + i) * this._sectorSize;
      BinaryPrimitives.WriteInt64LittleEndian(keys[(i * 2 * Block)..], offset);
      BinaryPrimitives.WriteInt64LittleEndian(keys[(i * 2 * Block + Block)..], offset);
      keys[i * 2 * Block + 2 * Block - 1] = 128;
    }
    Transform(this._tweakEncrypt, this._tweaks, sectors * 2 * Block);
    for (var s = 0; s < sectors; ++s) {
      var key = keys.Slice(s * 2 * Block, 2 * Block);
      var sector = data.Slice(s * this._sectorSize, this._sectorSize);
      for (var off = 0; off < sector.Length; off += 2 * Block)
        Xor(sector.Slice(off, 2 * Block), key, sector.Slice(off, 2 * Block));
    }
  }

  /// <summary>The sector as little-endian 32-bit words (in place; call again to restore byte order on big-endian hosts).</summary>
  private static Span<uint> Words(Span<byte> sector) {
    var words = MemoryMarshal.Cast<byte, uint>(sector);
    if (!BitConverter.IsLittleEndian) BinaryPrimitives.ReverseEndianness(words, words);
    return words;
  }

  private static void Words(Span<uint> words) {
    if (!BitConverter.IsLittleEndian) BinaryPrimitives.ReverseEndianness(words, words);
  }

  // The loops below split each pass where the neighbour indices wrap around the sector, so
  // the bulk of the words is addressed without a modulo.

  /// <summary>Diffuser A, decryption: for i = 0 … n·5−1: d_i ← d_i + (d_{i−2} ⊕ (d_{i−5} ⋘ R(a)_{i mod 4})).</summary>
  internal static void DiffuserADecrypt(Span<uint> d) {
    var n = d.Length;
    for (var cycle = 0; cycle < CyclesA; ++cycle) {
      for (var i = 0; i < 5; ++i)
        d[i] += d[Wrap(i - 2, n)] ^ BitOperations.RotateLeft(d[Wrap(i - 5, n)], RotationsA[i & 3]);
      for (var i = 5; i < n; ++i)
        d[i] += d[i - 2] ^ BitOperations.RotateLeft(d[i - 5], RotationsA[i & 3]);
    }
  }

  /// <summary>Diffuser A, encryption: for i = n·5−1 … 0: d_i ← d_i − (d_{i−2} ⊕ (d_{i−5} ⋘ R(a)_{i mod 4})).</summary>
  internal static void DiffuserAEncrypt(Span<uint> d) {
    var n = d.Length;
    for (var cycle = 0; cycle < CyclesA; ++cycle) {
      for (var i = n - 1; i >= 5; --i)
        d[i] -= d[i - 2] ^ BitOperations.RotateLeft(d[i - 5], RotationsA[i & 3]);
      for (var i = 4; i >= 0; --i)
        d[i] -= d[Wrap(i - 2, n)] ^ BitOperations.RotateLeft(d[Wrap(i - 5, n)], RotationsA[i & 3]);
    }
  }

  /// <summary>Diffuser B, decryption: for i = 0 … n·3−1: d_i ← d_i + (d_{i+2} ⊕ (d_{i+5} ⋘ R(b)_{i mod 4})).</summary>
  internal static void DiffuserBDecrypt(Span<uint> d) {
    var n = d.Length;
    for (var cycle = 0; cycle < CyclesB; ++cycle) {
      for (var i = 0; i < n - 5; ++i)
        d[i] += d[i + 2] ^ BitOperations.RotateLeft(d[i + 5], RotationsB[i & 3]);
      for (var i = n - 5; i < n; ++i)
        d[i] += d[Wrap(i + 2, n)] ^ BitOperations.RotateLeft(d[Wrap(i + 5, n)], RotationsB[i & 3]);
    }
  }

  /// <summary>Diffuser B, encryption: for i = n·3−1 … 0: d_i ← d_i − (d_{i+2} ⊕ (d_{i+5} ⋘ R(b)_{i mod 4})).</summary>
  internal static void DiffuserBEncrypt(Span<uint> d) {
    var n = d.Length;
    for (var cycle = 0; cycle < CyclesB; ++cycle) {
      for (var i = n - 1; i >= n - 5; --i)
        d[i] -= d[Wrap(i + 2, n)] ^ BitOperations.RotateLeft(d[Wrap(i + 5, n)], RotationsB[i & 3]);
      for (var i = n - 6; i >= 0; --i)
        d[i] -= d[i + 2] ^ BitOperations.RotateLeft(d[i + 5], RotationsB[i & 3]);
    }
  }

  private static int Wrap(int i, int n) => i < 0 ? i + n : i >= n ? i - n : i;

  private static void Xor(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, Span<byte> destination) {
    var i = 0;
    if (Vector.IsHardwareAccelerated) {
      var width = Vector<byte>.Count;
      for (; i <= a.Length - width; i += width)
        (new Vector<byte>(a[i..]) ^ new Vector<byte>(b[i..])).CopyTo(destination[i..]);
    }
    for (; i < a.Length; ++i) destination[i] = (byte)(a[i] ^ b[i]);
  }

  public void Dispose() {
    this._ecbEncrypt.Dispose();
    this._ecbDecrypt.Dispose();
    this._cbcEncrypt.Dispose();
    this._tweakEncrypt.Dispose();
    this._ivEncrypt?.Dispose();
    this._dataAes.Dispose();
    this._tweakAes.Dispose();
    this._ivAes?.Dispose();
    CryptographicOperations.ZeroMemory(this._scratch);
    CryptographicOperations.ZeroMemory(this._tweaks);
  }
}
