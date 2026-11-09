#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace FileFormat.BitLocker;

/// <summary>What may open a BitLocker volume: a user password, a recovery password, a startup key (.BEK contents).</summary>
/// <remarks>
/// <paramref name="Password"/> is also tried as a recovery password when it has that shape, as
/// cryptsetup does. A clear-key (suspended) protector opens without any of them.
/// </remarks>
public sealed record BitLockerCredentials(string? Password, string? RecoveryPassword = null, byte[]? StartupKey = null) {
  /// <summary>Whether nothing at all was supplied.</summary>
  public bool IsEmpty => this.Password is null && this.RecoveryPassword is null && this.StartupKey is null;
}

public sealed partial class BitLockerVolume {
  /// <summary>Whether a protector opens without credentials (a clear key: BitLocker is suspended).</summary>
  public bool HasClearKey => this.Protectors.Any(static p => p.Protection == BitLockerProtection.ClearKey);

  /// <summary>
  /// Opens a VMK with <paramref name="credentials"/>, trying the protectors in metadata order,
  /// authenticates the metadata with it and unwraps the full-volume encryption key. Returns the
  /// FVEK as dm-crypt takes it (for 128-bit Elephant the CBC and diffuser halves are packed
  /// together), or <see langword="null"/> when nothing opens.
  /// </summary>
  /// <exception cref="InvalidDataException">A VMK opened but the metadata fails its validation hash.</exception>
  public byte[]? TryUnlock(BitLockerCredentials credentials, out BitLockerKeyProtector? usedProtector) {
    ArgumentNullException.ThrowIfNull(credentials);
    usedProtector = null;
    if (this.WrappedFvek is null) throw new InvalidDataException("BitLocker: the metadata carries no FVEK.");
    foreach (var protector in this.Protectors) {
      var vmk = this.OpenVmk(protector, credentials);
      if (vmk is null) continue;
      try {
        this.Authenticate(vmk);
        var fvek = BitLockerKeys.Unwrap(this.WrappedFvek, vmk);
        if (fvek is null) continue;
        usedProtector = protector;
        return this.PackFvek(fvek);
      } finally {
        CryptographicOperations.ZeroMemory(vmk);
      }
    }
    return null;
  }

  private byte[]? OpenVmk(BitLockerKeyProtector protector, BitLockerCredentials credentials) {
    if (protector.WrappedVmk is not { } wrapped) return null;
    switch (protector.Protection) {
      case BitLockerProtection.Password when credentials.Password is not null:
        return UnwrapWith(wrapped, BitLockerKeys.StretchPassword(credentials.Password, protector.Salt));
      case BitLockerProtection.RecoveryPassword:
        foreach (var candidate in new[] { credentials.RecoveryPassword, credentials.Password }.Distinct()) {
          var key = BitLockerKeys.ParseRecoveryPassword(candidate);
          if (key is null) continue;
          var stretched = BitLockerKeys.StretchRecoveryKey(key, protector.Salt);
          CryptographicOperations.ZeroMemory(key);
          if (UnwrapWith(wrapped, stretched) is { } vmk) return vmk;
        }
        return null;
      case BitLockerProtection.StartupKey when credentials.StartupKey is not null:
        return BitLockerKeys.StartupKey(credentials.StartupKey, protector.Id, this.VolumeGuid) is { } external
          ? UnwrapWith(wrapped, external)
          : null;
      case BitLockerProtection.ClearKey when protector.ClearKey is not null:
        return BitLockerKeys.Unwrap(wrapped, protector.ClearKey);
      default:
        return null;
    }
  }

  private static byte[]? UnwrapWith(BitLockerWrappedKey wrapped, byte[] key) {
    try {
      return BitLockerKeys.Unwrap(wrapped, key);
    } finally {
      CryptographicOperations.ZeroMemory(key);
    }
  }

  /// <summary>
  /// The validation datum is a key datum (hash type 0x2005) holding SHA-256 of the metadata
  /// block, wrapped with the VMK: it proves both the VMK and the metadata.
  /// </summary>
  private void Authenticate(byte[] vmk) {
    var datum = BitLockerKeys.UnwrapRaw(this.Validation, vmk)
      ?? throw new InvalidDataException("BitLocker: the opened VMK does not decrypt the metadata validation datum.");
    if (datum.Length < BitLockerVolume.KeyDatumHeaderLength + 32 ||
        BinaryPrimitives.ReadUInt16LittleEndian(datum.AsSpan(2)) != EntryProperty ||
        BinaryPrimitives.ReadUInt16LittleEndian(datum.AsSpan(4)) != ValueKey ||
        BinaryPrimitives.ReadUInt16LittleEndian(datum.AsSpan(8)) != BitLockerKeys.ValidationDatumMethod)
      throw new NotSupportedException("BitLocker: unknown metadata validation datum.");
    if (!CryptographicOperations.FixedTimeEquals(datum.AsSpan(KeyDatumHeaderLength, 32), this.MetadataHash))
      throw new InvalidDataException("BitLocker: the FVE metadata does not match its validation hash.");
  }

  /// <summary>
  /// 128-bit Elephant stores 512 bits: CBC key, 16 unused bytes, diffuser key, 16 unused bytes;
  /// dm-crypt (and this reader) take the two keys back to back. Other methods are used as stored.
  /// </summary>
  private byte[] PackFvek(byte[] stored) {
    var expected = this.KeyBits / 8;
    if (this.CipherMode == BitLockerCipherMode.CbcElephant && expected == 32) {
      if (stored.Length < 64) throw new InvalidDataException("BitLocker: the 128-bit Elephant FVEK is too short.");
      var packed = new byte[32];
      stored.AsSpan(0, 16).CopyTo(packed);
      stored.AsSpan(32, 16).CopyTo(packed.AsSpan(16));
      CryptographicOperations.ZeroMemory(stored);
      return packed;
    }
    if (stored.Length != expected) {
      CryptographicOperations.ZeroMemory(stored);
      throw new InvalidDataException($"BitLocker: the FVEK is {stored.Length} bytes; method 0x{this.EncryptionMethod:X4} needs {expected}.");
    }
    return stored;
  }
}
