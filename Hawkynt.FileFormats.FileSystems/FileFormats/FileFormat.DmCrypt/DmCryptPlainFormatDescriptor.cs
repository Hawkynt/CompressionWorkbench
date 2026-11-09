#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.DmCrypt;

/// <summary>
/// dm-crypt plain mode: a headerless encrypted volume. Nothing on disk identifies it, so it is
/// never detected — it is chosen by id, with the cipher, hash, key size, offset and skip that
/// <c>cryptsetup open --type plain</c> would be given (cryptsetup's defaults otherwise). Lists the
/// decrypted data as <c>volume.img</c>; mounts the filesystem inside read-only or read-write
/// with the passphrase; creates plain volumes cryptsetup opens.
/// </summary>
/// <remarks>Specification: <c>docs/DMCRYPT-PLAIN-ON-DISK.md</c>.</remarks>
public sealed class DmCryptPlainFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IFormatOptionsSchema, IEncryptedBlockDeviceProvider {
  public const string VolumeName = "volume.img";

  public string Id => "DmCryptPlain";
  public string DisplayName => "dm-crypt (plain)";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.SupportsPassword;
  public string DefaultExtension => ".img";
  public IReadOnlyList<string> Extensions => [];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("aes-xts-plain64", "AES-XTS (plain64)"), new("aes-cbc-essiv:sha256", "AES-CBC (ESSIV)")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "dm-crypt plain (headerless) encrypted volume; chosen explicitly with cryptsetup's plain-mode parameters; mounts and creates";

  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    new(DmCryptPlainParameters.CipherName, "Cipher", FormatOptionKind.String, "aes-xts-plain64", null,
      "cipher-mode-iv as cryptsetup spells it: aes-xts-plain64, aes-xts-plain, aes-cbc-essiv:sha256, aes-cbc-plain64, aes-cbc-plain.", IsOptimizationAxis: false),
    new(DmCryptPlainParameters.HashName, "Passphrase hash", FormatOptionKind.String, "sha256", null,
      "sha1, sha256, sha384, sha512, ripemd160, whirlpool or plain (passphrase used as the key), optionally hash:bytes.", IsOptimizationAxis: false),
    new(DmCryptPlainParameters.KeySizeName, "Key size (bits)", FormatOptionKind.Integer, "256", null,
      "Volume key size in bits (XTS takes two keys: 256 or 512 for AES).", IsOptimizationAxis: false),
    new(DmCryptPlainParameters.OffsetName, "Offset (sectors)", FormatOptionKind.Integer, "0", null,
      "Start of the encrypted data, in 512-byte sectors.", IsOptimizationAxis: false),
    new(DmCryptPlainParameters.SkipName, "Skip (sectors)", FormatOptionKind.Integer, "0", null,
      "IV offset: the IV sector number of the first data sector.", IsOptimizationAxis: false),
  ];

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var length = stream.CanSeek ? stream.Length / DmCryptPlainVolume.SectorSize * DmCryptPlainVolume.SectorSize : 0;
    return [new(0, VolumeName, length, length, "aes-xts-plain64", false, true, null)];
  }

  /// <summary>Decrypts the whole device with cryptsetup's plain-mode defaults; other parameters need the block-device API.</summary>
  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    if (files is { Length: > 0 } && !MatchesFilter(VolumeName, files))
      return;
    if (password is null)
      throw new ArchivePasswordException("dm-crypt plain: the volume is encrypted; a passphrase is required.", passwordWasSupplied: false);
    using var device = DmCryptPlainVolume.OpenBlockDevice(stream, password, new(), writable: false);
    using var target = CreateEntryFile(outputDir, VolumeName);
    var buffer = new byte[256 * DmCryptPlainVolume.SectorSize];
    for (long block = 0; block < device.Geometry.BlockCount;) {
      var n = (int)Math.Min(256, device.Geometry.BlockCount - block);
      device.ReadBlocks(block, buffer.AsSpan(0, n * DmCryptPlainVolume.SectorSize));
      target.Write(buffer, 0, n * DmCryptPlainVolume.SectorSize);
      block += n;
    }
  }

  /// <summary>
  /// The decrypted mapping. A plain volume cannot tell a wrong passphrase from a right one; a
  /// wrong one yields noise in which no filesystem is found.
  /// </summary>
  public IRandomAccessBlockDevice OpenBlockDevice(Stream image, EncryptedVolumeCredentials credentials, bool writable, bool leaveOpen = true) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(credentials);
    if (credentials.Password is null)
      throw new ArchivePasswordException("dm-crypt plain: the volume is encrypted; a passphrase is required.", passwordWasSupplied: false);
    if (writable && !image.CanWrite)
      throw new NotSupportedException("A writable dm-crypt mapping needs a writable volume stream.");
    return DmCryptPlainVolume.OpenBlockDevice(image, credentials.Password, DmCryptPlainParameters.From(credentials.Get), writable, leaveOpen);
  }

  /// <summary>Encrypts the single input (whole 512-byte sectors) as a plain mapping.</summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(options);
    if (string.IsNullOrEmpty(options.Password))
      throw new ArgumentException("dm-crypt plain creation needs a passphrase.", nameof(options));
    byte[]? payload = null;
    foreach (var input in inputs) {
      if (input.IsDirectory) continue;
      if (payload is not null) throw new ArgumentException("A dm-crypt plain volume holds exactly one volume image.", nameof(inputs));
      payload = input.ReadContent();
    }
    if (payload is null) throw new ArgumentException("dm-crypt plain needs the volume image to encrypt.", nameof(inputs));
    DmCryptPlainVolume.Write(output, payload, options.Password, DmCryptPlainParameters.From(options.GetString));
  }
}
