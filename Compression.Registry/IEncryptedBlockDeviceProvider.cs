#pragma warning disable CS1591
namespace Compression.Registry;

/// <summary>
/// What unlocks an encrypted volume: a passphrase and, for formats that need more than one, named
/// parameters (dm-crypt plain cipher/hash/offset, a VeraCrypt PIM, a hidden-volume password to
/// protect, …). Parameter names are format-specific and case-insensitive.
/// </summary>
public sealed record EncryptedVolumeCredentials(string? Password, IReadOnlyDictionary<string, string>? Parameters = null) {
  /// <summary>Returns the named parameter, or <see langword="null"/> when it was not given.</summary>
  public string? Get(string name) {
    if (this.Parameters is null) return null;
    foreach (var (key, value) in this.Parameters)
      if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
        return value;
    return null;
  }
}

/// <summary>
/// Optional descriptor capability for encrypted containers (LUKS, dm-crypt, VeraCrypt,
/// BitLocker, …): exposes the decrypted volume as a random-access block device, decrypting each
/// block as it is read and encrypting it as it is written, so a filesystem inside can be mounted
/// without ever materialising the plaintext.
/// </summary>
public interface IEncryptedBlockDeviceProvider {
  /// <summary>Opens the decrypted view of <paramref name="image"/>.</summary>
  /// <exception cref="ArchivePasswordException">The credentials are missing or do not unlock the volume.</exception>
  /// <exception cref="NotSupportedException">The volume uses a key derivation or cipher that is not implemented.</exception>
  IRandomAccessBlockDevice OpenBlockDevice(Stream image, EncryptedVolumeCredentials credentials, bool writable, bool leaveOpen = true);
}
