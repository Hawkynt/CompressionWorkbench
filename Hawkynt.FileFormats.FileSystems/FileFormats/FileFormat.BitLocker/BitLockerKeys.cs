#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace FileFormat.BitLocker;

/// <summary>
/// BitLocker key handling: the SHA-256 key stretch for user and recovery passwords, the 48-digit
/// recovery password, startup-key (.BEK) files and the AES-CCM wrapped key datums that carry the
/// VMK, the FVEK and the metadata validation hash.
/// </summary>
public static class BitLockerKeys {
  /// <summary>Iterations of the key stretch (fixed; nothing on disk records it).</summary>
  public const int StretchIterations = 0x100000;

  public const int RecoveryPasswordLength = 55;
  public const int BekHeaderLength = 48;
  private const int StartupKeyHeaderLength = 24;

  /// <summary>Key-datum "encryption method" of a VMK (as Windows writes it; nothing checks it).</summary>
  public const uint VmkDatumMethod = 0x2003;

  /// <summary>Key-datum "encryption method" (hash type) of the validation hash.</summary>
  public const uint ValidationDatumMethod = 0x2005;

  /// <summary>Stretched key of a user password: UTF-16LE → SHA-256 → SHA-256 → stretch.</summary>
  public static byte[] StretchPassword(string password, ReadOnlySpan<byte> salt) {
    ArgumentNullException.ThrowIfNull(password);
    var utf16 = Encoding.Unicode.GetBytes(password);
    try {
      var initial = SHA256.HashData(SHA256.HashData(utf16));
      return Stretch(initial, salt);
    } finally {
      CryptographicOperations.ZeroMemory(utf16);
    }
  }

  /// <summary>Stretched key of a recovery password's 16-byte key: SHA-256 → stretch.</summary>
  public static byte[] StretchRecoveryKey(ReadOnlySpan<byte> recoveryKey, ReadOnlySpan<byte> salt)
    => Stretch(SHA256.HashData(recoveryKey), salt);

  /// <summary>
  /// The stretch: a 88-byte state (last hash, initial hash, salt, little-endian counter) is
  /// hashed 0x100000 times, the hash replacing "last" and the counter counting up.
  /// </summary>
  private static byte[] Stretch(byte[] initial, ReadOnlySpan<byte> salt) {
    if (salt.Length != BitLockerVolume.SaltLength) throw new ArgumentException("The salt is 16 bytes.", nameof(salt));
    Span<byte> state = stackalloc byte[88];
    state.Clear();
    initial.CopyTo(state[32..]);
    salt.CopyTo(state[64..]);
    Span<byte> hash = stackalloc byte[32];
    var counter = state[80..];
    for (ulong i = 0; i < StretchIterations; ++i) {
      SHA256.HashData(state, hash);
      hash.CopyTo(state);
      BinaryPrimitives.WriteUInt64LittleEndian(counter, i + 1);
    }
    var result = state[..32].ToArray();
    CryptographicOperations.ZeroMemory(state);
    CryptographicOperations.ZeroMemory(initial);
    return result;
  }

  /// <summary>
  /// Parses a recovery password — 8 groups of 6 digits joined by '-', each group divisible by 11
  /// with a quotient below 65536 — into its 16-byte key (the quotients, little-endian). A trailing
  /// newline is tolerated, as cryptsetup tolerates one from a key file.
  /// </summary>
  public static byte[]? ParseRecoveryPassword(string? text) {
    if (text is null) return null;
    if (text.Length == RecoveryPasswordLength + 1 && text[^1] == '\n') text = text[..^1];
    if (text.Length != RecoveryPasswordLength) return null;
    var key = new byte[16];
    for (var group = 0; group < 8; ++group) {
      var at = group * 7;
      if (group > 0 && text[at - 1] != '-') return null;
      var digits = text.AsSpan(at, 6);
      foreach (var c in digits)
        if (c is < '0' or > '9') return null;
      var value = int.Parse(digits, NumberStyles.None, CultureInfo.InvariantCulture);
      if (value % 11 != 0 || value / 11 > ushort.MaxValue) return null;
      BinaryPrimitives.WriteUInt16LittleEndian(key.AsSpan(group * 2), (ushort)(value / 11));
    }
    return key;
  }

  /// <summary>Formats a 16-byte recovery key as the 48-digit recovery password.</summary>
  public static string FormatRecoveryPassword(ReadOnlySpan<byte> key) {
    if (key.Length != 16) throw new ArgumentException("A recovery key is 16 bytes.", nameof(key));
    var sb = new StringBuilder(RecoveryPasswordLength);
    for (var group = 0; group < 8; ++group) {
      if (group > 0) sb.Append('-');
      sb.Append((BinaryPrimitives.ReadUInt16LittleEndian(key[(group * 2)..]) * 11).ToString("D6", CultureInfo.InvariantCulture));
    }
    return sb.ToString();
  }

  /// <summary>A fresh random recovery password.</summary>
  public static string NewRecoveryPassword() {
    Span<byte> key = stackalloc byte[16];
    RandomNumberGenerator.Fill(key);
    var text = FormatRecoveryPassword(key);
    key.Clear();
    return text;
  }

  /// <summary>
  /// Unwraps an AES-CCM key datum (AES-256 key, 12-byte nonce, 16-byte tag, no associated data)
  /// and returns the key bytes after its 12-byte header, or <see langword="null"/> when the tag
  /// does not verify or the datum's own size field disagrees with its length.
  /// </summary>
  public static byte[]? Unwrap(BitLockerWrappedKey wrapped, ReadOnlySpan<byte> key) {
    ArgumentNullException.ThrowIfNull(wrapped);
    var plain = UnwrapRaw(wrapped, key);
    if (plain is null) return null;
    try {
      if (plain.Length < BitLockerVolume.KeyDatumHeaderLength || BinaryPrimitives.ReadUInt16LittleEndian(plain) != plain.Length)
        return null;
      return plain[BitLockerVolume.KeyDatumHeaderLength..];
    } finally {
      CryptographicOperations.ZeroMemory(plain);
    }
  }

  /// <summary>Decrypts and authenticates an AES-CCM datum; <see langword="null"/> when the tag fails.</summary>
  public static byte[]? UnwrapRaw(BitLockerWrappedKey wrapped, ReadOnlySpan<byte> key) {
    ArgumentNullException.ThrowIfNull(wrapped);
    var plain = new byte[wrapped.Ciphertext.Length];
    using var ccm = new AesCcm(key);
    try {
      ccm.Decrypt(wrapped.Nonce, wrapped.Ciphertext, wrapped.Tag, plain);
      return plain;
    } catch (AuthenticationTagMismatchException) {
      return null;
    }
  }

  /// <summary>Wraps a key datum (12-byte header carrying <paramref name="method"/>, then the key) with AES-CCM.</summary>
  public static BitLockerWrappedKey Wrap(ReadOnlySpan<byte> payload, uint method, ReadOnlySpan<byte> key, ReadOnlySpan<byte> nonce) {
    var plain = new byte[BitLockerVolume.KeyDatumHeaderLength + payload.Length];
    BinaryPrimitives.WriteUInt16LittleEndian(plain, (ushort)plain.Length);
    BinaryPrimitives.WriteUInt16LittleEndian(plain.AsSpan(2), BitLockerVolume.EntryProperty);
    BinaryPrimitives.WriteUInt16LittleEndian(plain.AsSpan(4), BitLockerVolume.ValueKey);
    BinaryPrimitives.WriteUInt32LittleEndian(plain.AsSpan(8), method);   // version (offset 6) stays 0, as Windows writes it
    payload.CopyTo(plain.AsSpan(BitLockerVolume.KeyDatumHeaderLength));
    var cipher = new byte[plain.Length];
    var tag = new byte[BitLockerVolume.TagLength];
    using (var ccm = new AesCcm(key))
      ccm.Encrypt(nonce, plain, cipher, tag);
    CryptographicOperations.ZeroMemory(plain);
    return new BitLockerWrappedKey(nonce.ToArray(), tag, cipher);
  }

  /// <summary>
  /// The external key a startup-key (.BEK) file carries for the protector <paramref name="protectorId"/>:
  /// <see langword="null"/> when the file belongs to another protector.
  /// </summary>
  /// <exception cref="InvalidDataException">The file is malformed, or names another volume.</exception>
  public static byte[]? StartupKey(ReadOnlySpan<byte> bek, Guid protectorId, Guid volumeGuid) {
    if (bek.Length < BekHeaderLength + 6) return null;
    if (new Guid(bek.Slice(16, 16)) != protectorId) return null;
    if (BinaryPrimitives.ReadUInt32LittleEndian(bek[4..]) != 1)
      throw new NotSupportedException($"BitLocker: unsupported .BEK metadata version {BinaryPrimitives.ReadUInt32LittleEndian(bek[4..])}.");
    if (BinaryPrimitives.ReadUInt32LittleEndian(bek) != bek.Length)
      throw new InvalidDataException("BitLocker: the .BEK metadata size does not match the file length.");
    var size = BinaryPrimitives.ReadUInt16LittleEndian(bek[BekHeaderLength..]);
    if (size < BitLockerVolume.EntryHeaderLength) throw new InvalidDataException("BitLocker: malformed .BEK entry.");
    var type = BinaryPrimitives.ReadUInt16LittleEndian(bek[(BekHeaderLength + 2)..]);
    var value = BinaryPrimitives.ReadUInt16LittleEndian(bek[(BekHeaderLength + 4)..]);
    if (type != BitLockerVolume.EntryStartupKey || value != BitLockerVolume.ValueExternalKey)
      throw new InvalidDataException("BitLocker: the .BEK file holds no external key.");

    for (var start = BekHeaderLength + BitLockerVolume.EntryHeaderLength + StartupKeyHeaderLength; bek.Length - start >= 6;) {
      var entrySize = BinaryPrimitives.ReadUInt16LittleEndian(bek[start..]);
      if (entrySize == 0) break;
      if (entrySize > bek.Length - start) throw new InvalidDataException("BitLocker: a .BEK entry runs past the file.");
      var entryType = BinaryPrimitives.ReadUInt16LittleEndian(bek[(start + 2)..]);
      var entryValue = BinaryPrimitives.ReadUInt16LittleEndian(bek[(start + 4)..]);
      if (entryType != BitLockerVolume.EntryProperty && entryType != BitLockerVolume.EntryVolumeGuid)
        throw new InvalidDataException($"BitLocker: unexpected entry type {entryType} in a .BEK file.");
      switch (entryValue) {
        case BitLockerVolume.ValueKey:
          if (entrySize < BitLockerVolume.EntryHeaderLength + 4) throw new InvalidDataException("BitLocker: the .BEK key is too short.");
          return bek.Slice(start + BitLockerVolume.EntryHeaderLength + 4, entrySize - BitLockerVolume.EntryHeaderLength - 4).ToArray();
        case BitLockerVolume.ValueString:
          break;
        case BitLockerVolume.ValueGuid:
          if (bek.Length - start < BitLockerVolume.EntryHeaderLength + 16) throw new InvalidDataException("BitLocker: the .BEK volume GUID is too short.");
          var guid = new Guid(bek.Slice(start + BitLockerVolume.EntryHeaderLength, 16));
          if (guid != volumeGuid) throw new InvalidDataException($"BitLocker: the .BEK file belongs to volume {guid}, not {volumeGuid}.");
          break;
        default:
          throw new InvalidDataException($"BitLocker: unexpected entry value {entryValue} in a .BEK file.");
      }
      start += entrySize;
    }
    throw new InvalidDataException("BitLocker: the .BEK file holds no key.");
  }
}
