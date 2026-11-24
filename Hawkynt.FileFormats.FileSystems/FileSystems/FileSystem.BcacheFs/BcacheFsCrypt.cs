#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Core.Crypto;

namespace FileSystem.BcacheFs;

/// <summary>
/// The 16-byte ChaCha20 starting block bcachefs derives for each thing it encrypts: word 0 is
/// the block counter, words 1–3 the 96-bit nonce (docs/BCACHEFS-ON-DISK.md, "Encryption").
/// </summary>
internal readonly record struct BcacheFsNonce(uint D0, uint D1, uint D2, uint D3) {
  /// <summary>Marks the nonce a Poly1305 key is generated from (word 3, bit 31).</summary>
  internal const uint Poly = 1u << 31;
  internal const uint Extent = 1u << 28;
  internal const uint Btree = 2u << 28;
  internal const uint Journal = 3u << 28;

  /// <summary>The nonce <paramref name="bytes"/> further on: the counter advances one per 64-byte block.</summary>
  internal BcacheFsNonce Add(int bytes) {
    if ((bytes & 63) != 0) throw new ArgumentException("bcachefs: a nonce advances in whole ChaCha20 blocks.", nameof(bytes));
    return this with { D0 = unchecked(this.D0 + (uint)(bytes / 64)) };
  }

  internal void Write(Span<byte> destination) {
    BinaryPrimitives.WriteUInt32LittleEndian(destination, this.D0);
    BinaryPrimitives.WriteUInt32LittleEndian(destination[4..], this.D1);
    BinaryPrimitives.WriteUInt32LittleEndian(destination[8..], this.D2);
    BinaryPrimitives.WriteUInt32LittleEndian(destination[12..], this.D3);
  }

  /// <summary>A b-tree bset's nonce: the byte offset of its entry in the node, its sequence, its journal sequence.</summary>
  internal static BcacheFsNonce ForBset(ulong sequence, ulong journalSequence, int entryOffset)
    => new((uint)entryOffset, (uint)sequence, (uint)(sequence >> 32), (uint)journalSequence ^ Btree);

  /// <summary>A journal set's nonce: its sequence number.</summary>
  internal static BcacheFsNonce ForJournal(ulong sequence)
    => new(0, (uint)sequence, (uint)(sequence >> 32), Journal);

  /// <summary>
  /// An extent's nonce: the key's version, the compression type and decoded size of a
  /// compressed region, advanced by the entry's nonce (in sectors).
  /// </summary>
  internal static BcacheFsNonce ForExtent(BcacheFsKeyVersion version, BcacheFsExtentCrc crc) {
    var compression = crc.Compressed ? (uint)crc.CompressionType : 0;
    var size = compression != 0 ? crc.UncompressedSize : 0;
    var nonce = new BcacheFsNonce(
      unchecked(size << 22),
      (uint)version.Lo,
      (uint)(version.Lo >> 32),
      (version.Hi | (compression << 24)) ^ Extent);
    return nonce with { D0 = unchecked(nonce.D0 + crc.Nonce * 8) };
  }
}

/// <summary>
/// A bcachefs volume's encryption: the 256-bit master key from the superblock's crypt field
/// (unwrapped with the passphrase when it is wrapped), ChaCha20 for the data and Poly1305 MACs
/// as checksums.
/// </summary>
/// <remarks>
/// <c>docs/BCACHEFS-ON-DISK.md</c>, "Encryption", is the specification this follows.
/// </remarks>
internal sealed class BcacheFsCrypt {
  /// <summary><c>"bch**key"</c> as a little-endian word: what an unwrapped key's magic reads.</summary>
  internal const ulong KeyMagic = 0x79656B2A2A686362UL;

  /// <summary>The scrypt salt: <c>"bcache"</c> and its terminating zero.</summary>
  private static ReadOnlySpan<byte> Salt => "bcache\0"u8;

  /// <summary>Offsets inside the crypt field, header included.</summary>
  private const int FieldFlags = 8, FieldKdfFlags = 16, FieldKey = 24, FieldBytes = 64;

  private readonly byte[] _key;

  internal BcacheFsCrypt(byte[] masterKey) {
    if (masterKey.Length != ChaCha20.KeySize) throw new ArgumentException("bcachefs master keys are 32 bytes.", nameof(masterKey));
    this._key = masterKey;
  }

  /// <summary>The master key (for writing it back wrapped).</summary>
  internal ReadOnlySpan<byte> MasterKey => this._key;

  /// <summary>Whether the superblock carries a crypt field, i.e. the volume is encrypted.</summary>
  internal static bool IsEncrypted(BcacheFsSuperblockRecord superblock)
    => superblock.FieldsOf(BcacheFsSuperblockFieldType.Crypt).Any();

  /// <summary>Whether the volume's master key is wrapped by a passphrase.</summary>
  internal static bool NeedsPassphrase(BcacheFsSuperblockRecord superblock)
    => superblock.FieldsOf(BcacheFsSuperblockFieldType.Crypt).FirstOrDefault() is { } field
       && field.RawBytes.Length >= FieldBytes
       && BinaryPrimitives.ReadUInt64LittleEndian(field.RawBytes.AsSpan(FieldKey)) != KeyMagic;

  /// <summary>
  /// The volume's encryption, unlocked: null for a volume without a crypt field.
  /// </summary>
  /// <exception cref="UnauthorizedAccessException">The key is wrapped and the passphrase is missing or wrong.</exception>
  /// <exception cref="InvalidDataException">The crypt field is malformed.</exception>
  /// <exception cref="NotSupportedException">The key derivation is not scrypt.</exception>
  internal static BcacheFsCrypt? Unlock(BcacheFsSuperblockRecord superblock, string? passphrase) {
    var field = superblock.FieldsOf(BcacheFsSuperblockFieldType.Crypt).FirstOrDefault();
    if (field == null) return null;
    var bytes = field.RawBytes;
    if (bytes.Length < FieldBytes)
      throw new InvalidDataException($"bcachefs: the crypt field is {bytes.Length} bytes, not {FieldBytes}.");

    var wrapped = bytes.AsSpan(FieldKey, 40).ToArray();
    if (BinaryPrimitives.ReadUInt64LittleEndian(wrapped) == KeyMagic)
      return new BcacheFsCrypt(wrapped.AsSpan(8, 32).ToArray());

    if (passphrase == null)
      throw new UnauthorizedAccessException("bcachefs: the volume is encrypted; its passphrase is required.");
    var userKey = DeriveUserKey(bytes, passphrase);
    ChaCha20.Transform(userKey, KeyNonce(superblock), wrapped);
    if (BinaryPrimitives.ReadUInt64LittleEndian(wrapped) != KeyMagic)
      throw new UnauthorizedAccessException("bcachefs: the passphrase does not unlock the volume's key.");
    return new BcacheFsCrypt(wrapped.AsSpan(8, 32).ToArray());
  }

  /// <summary>
  /// The crypt field (header included) for <paramref name="masterKey"/>: stored in the clear
  /// when <paramref name="passphrase"/> is null, otherwise wrapped by the scrypt-derived key
  /// with the parameters <c>bcachefs format</c> uses (N 2^14, r 2^3, p 2^4).
  /// </summary>
  internal static byte[] ComposeField(ReadOnlySpan<byte> masterKey, string? passphrase, ulong filesystemMagic,
      int log2N = 14, int log2R = 3, int log2P = 4) {
    var field = new byte[FieldBytes];
    BinaryPrimitives.WriteUInt32LittleEndian(field, FieldBytes / 8);
    BinaryPrimitives.WriteUInt32LittleEndian(field.AsSpan(4), (uint)BcacheFsSuperblockFieldType.Crypt);
    BinaryPrimitives.WriteUInt64LittleEndian(field.AsSpan(FieldKey), KeyMagic);
    masterKey.CopyTo(field.AsSpan(FieldKey + 8));
    if (passphrase == null) return field;

    // KDF type 0 (scrypt) in flags; the costs as base-2 logarithms in kdf_flags.
    BinaryPrimitives.WriteUInt64LittleEndian(field.AsSpan(FieldKdfFlags),
      (ulong)(uint)log2N | ((ulong)(uint)log2R << 16) | ((ulong)(uint)log2P << 32));
    var userKey = DeriveUserKey(field, passphrase);
    Span<byte> iv = stackalloc byte[16];
    KeyNonce(filesystemMagic).Write(iv);
    ChaCha20.Transform(userKey, iv, field.AsSpan(FieldKey, 40));
    return field;
  }

  private static byte[] DeriveUserKey(byte[] field, string passphrase) {
    var kdf = BinaryPrimitives.ReadUInt64LittleEndian(field.AsSpan(FieldFlags)) & 0xF;
    if (kdf != 0) throw new NotSupportedException($"bcachefs: key derivation type {kdf} is not scrypt.");
    var flags = BinaryPrimitives.ReadUInt64LittleEndian(field.AsSpan(FieldKdfFlags));
    var log2N = (int)(flags & 0xFFFF);
    var log2R = (int)((flags >> 16) & 0xFFFF);
    var log2P = (int)((flags >> 32) & 0xFFFF);
    if (log2N is < 1 or > 24 || log2R > 8 || log2P > 8)
      throw new InvalidDataException($"bcachefs: scrypt costs 2^{log2N}, 2^{log2R}, 2^{log2P} are out of range.");
    return Scrypt.Derive(Encoding.UTF8.GetBytes(passphrase), Salt, 1 << log2N, 1 << log2R, 1 << log2P, ChaCha20.KeySize);
  }

  private static byte[] KeyNonce(BcacheFsSuperblockRecord superblock) {
    var iv = new byte[16];
    KeyNonce(superblock.FilesystemMagic).Write(iv);
    return iv;
  }

  /// <summary>The key-wrapping nonce: zero counter, then the superblock magic word in words 2 and 3.</summary>
  private static BcacheFsNonce KeyNonce(ulong magic) => new(0, 0, (uint)magic, (uint)(magic >> 32));

  /// <summary>Encrypts or decrypts <paramref name="data"/> in place, its keystream starting at <paramref name="nonce"/>.</summary>
  internal void Apply(BcacheFsNonce nonce, Span<byte> data) {
    Span<byte> iv = stackalloc byte[16];
    nonce.Write(iv);
    ChaCha20.Transform(this._key, iv, data);
  }

  /// <summary>
  /// The Poly1305 MAC of <paramref name="data"/> as a bcachefs checksum: keyed by the first 32
  /// keystream bytes of <paramref name="nonce"/> with the poly bit set, cut to 10 bytes for the
  /// 80-bit type.
  /// </summary>
  internal BcacheFsChecksum Mac(BcacheFsChecksumType type, BcacheFsNonce nonce, ReadOnlySpan<byte> data) {
    Span<byte> polyKey = stackalloc byte[32];
    polyKey.Clear();
    this.Apply(nonce with { D3 = nonce.D3 ^ BcacheFsNonce.Poly }, polyKey);
    var mac = new Poly1305(polyKey);
    mac.Update(data);
    Span<byte> tag = stackalloc byte[16];
    mac.Final(tag);
    var lo = BinaryPrimitives.ReadUInt64LittleEndian(tag);
    var hi = BinaryPrimitives.ReadUInt64LittleEndian(tag[8..]);
    return type switch {
      BcacheFsChecksumType.ChaCha20Poly1305_80 => new BcacheFsChecksum(lo, hi & 0xFFFF),
      BcacheFsChecksumType.ChaCha20Poly1305_128 => new BcacheFsChecksum(lo, hi),
      _ => throw new ArgumentOutOfRangeException(nameof(type), $"{type} is not a MAC type."),
    };
  }

  internal static bool IsMacType(BcacheFsChecksumType type)
    => type is BcacheFsChecksumType.ChaCha20Poly1305_80 or BcacheFsChecksumType.ChaCha20Poly1305_128;
}
