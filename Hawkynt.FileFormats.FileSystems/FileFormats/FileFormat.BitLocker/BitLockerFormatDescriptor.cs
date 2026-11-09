#pragma warning disable CS1591
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.BitLocker;

/// <summary>
/// BitLocker (BDE) volume, including BitLocker To Go. Lists the FVE metadata as
/// <c>metadata.ini</c> and the decrypted volume as <c>volume.img</c> — exactly cryptsetup's
/// device-mapper view, metadata areas reading as zeros. A user password, a 48-digit recovery
/// password, a startup key (.BEK) or a clear key (suspended protection) opens it; AES-XTS,
/// AES-CBC and AES-CBC with the Elephant diffuser, 512- and 4096-byte sectors. With the
/// credentials the filesystem inside mounts read-only or read-write. Creates AES-XTS volumes
/// protected by a password and a recovery password that cryptsetup opens.
/// </summary>
/// <remarks>Specification: <c>docs/BITLOCKER-ON-DISK.md</c>.</remarks>
public sealed class BitLockerFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, ISyntheticEntryNames, IFormatOptionsSchema, IEncryptedBlockDeviceProvider {
  public const string VolumeName = "volume.img";
  public const string MetadataName = "metadata.ini";

  /// <summary>Mount/extract parameter: a 48-digit recovery password (alternative to the password).</summary>
  public const string RecoveryPasswordName = "RecoveryPassword";

  /// <summary>Mount/extract parameter: path of a startup-key (.BEK) file.</summary>
  public const string StartupKeyName = "StartupKey";

  public const string MethodName = "Method";
  public const string DescriptionName = "Description";

  private static readonly IReadOnlySet<string> Synthetic = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { MetadataName };

  private static readonly (string Name, ushort Code)[] CreateMethods = [
    ("aes-xts-128", 0x8004), ("aes-xts-256", 0x8005),
    ("aes-cbc-128", 0x8002), ("aes-cbc-256", 0x8003),
    ("aes-cbc-elephant-128", 0x8000), ("aes-cbc-elephant-256", 0x8001),
  ];

  public string Id => "BitLocker";
  public string DisplayName => "BitLocker";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsPassword | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".bitlocker";
  public IReadOnlyList<string> Extensions => [".bitlocker", ".bde"];
  public IReadOnlyList<string> CompoundExtensions => [];

  /// <summary>
  /// <c>-FVE-FS-</c> as OEM name; for To Go, <c>MSWIN4.1</c> together with one of the two
  /// BitLocker GUIDs at 424 — the OEM name alone is any FAT volume's.
  /// </summary>
  public IReadOnlyList<MagicSignature> MagicSignatures => [
    new("-FVE-FS-"u8.ToArray(), Offset: 3, Confidence: 0.95),
    ToGoSignature(BitLockerVolume.NormalGuid),
    ToGoSignature(BitLockerVolume.EncryptOnWriteGuid),
  ];

  private static MagicSignature ToGoSignature(byte[] guid) {
    const int start = 3, length = BitLockerVolume.ToGoHeaderGuidOffset + 16 - start;
    var bytes = new byte[length];
    var mask = new byte[length];
    "MSWIN4.1"u8.CopyTo(bytes);
    mask.AsSpan(0, 8).Fill(0xFF);
    guid.CopyTo(bytes, BitLockerVolume.ToGoHeaderGuidOffset - start);
    mask.AsSpan(BitLockerVolume.ToGoHeaderGuidOffset - start, 16).Fill(0xFF);
    return new MagicSignature(bytes, Offset: start, Confidence: 0.96, Mask: mask);
  }

  public IReadOnlyList<FormatMethodInfo> Methods => [.. CreateMethods.Select(static m => new FormatMethodInfo(m.Name, m.Name.ToUpperInvariant()))];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "BitLocker / BitLocker To Go volume (AES-XTS, AES-CBC, AES-CBC+Elephant; password, recovery password, startup key, clear key); mounts; creates AES-XTS";
  public IReadOnlySet<string> SyntheticEntryNames => Synthetic;

  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    new(MethodName, "Encryption method", FormatOptionKind.Enum, "aes-xts-128", [.. CreateMethods.Select(static m => m.Name)],
      "Sector encryption of the new volume. Windows 10 and later default to AES-XTS-128; the CBC methods are the Windows 8 and Vista/7 ones.", IsOptimizationAxis: false),
    new(RecoveryPasswordName, "Recovery password", FormatOptionKind.String, "", null,
      "The 48-digit recovery password protecting the new volume (8 groups of 6 digits, each divisible by 11). Required: a generated one would have nowhere to go.", IsOptimizationAxis: false),
    new(DescriptionName, "Description", FormatOptionKind.String, "", null,
      "Volume description stored in the FVE metadata.", IsOptimizationAxis: false),
  ];

  /// <summary>Credentials from a password and the mount parameters (<c>RecoveryPassword</c>, <c>StartupKey</c>).</summary>
  public static BitLockerCredentials Credentials(string? password, Func<string, string?> get) {
    ArgumentNullException.ThrowIfNull(get);
    var bek = get(StartupKeyName) is { Length: > 0 } path ? File.ReadAllBytes(path) : null;
    return new BitLockerCredentials(password, get(RecoveryPasswordName), bek);
  }

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    try {
      var v = BitLockerVolume.Read(stream);
      var metadata = Metadata(v, stream.Length);
      var length = PlaintextLength(v, stream.Length);
      return [
        new(0, MetadataName, metadata.Length, metadata.Length, "stored", false, false, null),
        new(1, VolumeName, length, length, $"aes-{v.CipherModeName}", false, true, null),
      ];
    } catch (Exception e) when (e is InvalidDataException or NotSupportedException) {
      var length = stream.CanSeek ? stream.Length : 0;
      return [new(0, "FULL.bitlocker", length, length, "stored", false, false, null)];
    }
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var v = BitLockerVolume.Read(stream);
    if (files is not { Length: > 0 } || MatchesFilter(MetadataName, files))
      WriteFile(outputDir, MetadataName, Metadata(v, stream.Length));
    if (files is { Length: > 0 } && !MatchesFilter(VolumeName, files))
      return;
    using var device = Open(stream, v, new BitLockerCredentials(password), writable: false, leaveOpen: true);
    using var target = CreateEntryFile(outputDir, VolumeName);
    var size = device.Geometry.LogicalBlockSize;
    var perChunk = Math.Max(1, (1 << 20) / size);
    var buffer = new byte[perChunk * size];
    for (long block = 0; block < device.Geometry.BlockCount;) {
      var n = (int)Math.Min(perChunk, device.Geometry.BlockCount - block);
      device.ReadBlocks(block, buffer.AsSpan(0, n * size));
      target.Write(buffer, 0, n * size);
      block += n;
    }
  }

  /// <summary>The decrypted volume as a block device; the filesystem inside mounts through it.</summary>
  public IRandomAccessBlockDevice OpenBlockDevice(Stream image, EncryptedVolumeCredentials credentials, bool writable, bool leaveOpen = true) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(credentials);
    if (writable && !image.CanWrite)
      throw new NotSupportedException("A writable BitLocker block device needs a writable volume stream.");
    var v = BitLockerVolume.Read(image);
    return Open(image, v, Credentials(credentials.Password, credentials.Get), writable, leaveOpen);
  }

  private static BitLockerBlockDevice Open(Stream image, BitLockerVolume v, BitLockerCredentials credentials, bool writable, bool leaveOpen) {
    var fvek = Unlock(v, credentials);
    try {
      return new BitLockerBlockDevice(image, v, fvek, writable, leaveOpen);
    } finally {
      CryptographicOperations.ZeroMemory(fvek);
    }
  }

  /// <summary>The FVEK, or <see cref="ArchivePasswordException"/> when nothing given (or no clear key) opens a protector.</summary>
  public static byte[] Unlock(BitLockerVolume volume, BitLockerCredentials credentials) {
    ArgumentNullException.ThrowIfNull(volume);
    ArgumentNullException.ThrowIfNull(credentials);
    if (credentials.IsEmpty && !volume.HasClearKey)
      throw new ArchivePasswordException("BitLocker: the volume is encrypted; a password, recovery password or startup key is required.", passwordWasSupplied: false);
    return volume.TryUnlock(credentials, out _)
      ?? (credentials.IsEmpty
        ? throw new ArchivePasswordException("BitLocker: the clear key does not open the volume; a password, recovery password or startup key is required.", passwordWasSupplied: false)
        : throw new ArchivePasswordException("BitLocker: no key protector opens with these credentials.", passwordWasSupplied: true));
  }

  /// <summary>Encrypts the single input volume image (whole 512-byte sectors) into a new BitLocker volume.</summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(options);
    if (string.IsNullOrEmpty(options.Password))
      throw new ArgumentException("BitLocker creation needs a password.", nameof(options));
    var recovery = options.GetString(RecoveryPasswordName);
    if (string.IsNullOrWhiteSpace(recovery))
      throw new ArgumentException($"BitLocker creation needs the {RecoveryPasswordName} option (48 digits in 8 groups, each divisible by 11).", nameof(options));
    byte[]? payload = null;
    foreach (var input in inputs) {
      if (input.IsDirectory || Path.GetFileName(input.ArchiveName).Equals(MetadataName, StringComparison.OrdinalIgnoreCase)) continue;
      if (payload is not null) throw new ArgumentException("A BitLocker volume holds exactly one volume image.", nameof(inputs));
      payload = input.ReadContent();
    }
    if (payload is null) throw new ArgumentException("BitLocker needs the volume image to encrypt.", nameof(inputs));
    var methodName = options.GetString(MethodName) ?? "aes-xts-128";
    var method = CreateMethods.FirstOrDefault(m => m.Name.Equals(methodName, StringComparison.OrdinalIgnoreCase));
    if (method.Name is null)
      throw new ArgumentException($"Unknown BitLocker method '{methodName}' ({string.Join(", ", CreateMethods.Select(static m => m.Name))}).", nameof(options));
    BitLockerWriter.Write(output, payload, new BitLockerCreateOptions {
      Password = options.Password,
      RecoveryPassword = recovery.Trim(),
      EncryptionMethod = method.Code,
      Description = options.GetString(DescriptionName) is { Length: > 0 } d ? d : null,
    });
  }

  /// <summary>Length of the decrypted view: the device, in whole sectors.</summary>
  public static long PlaintextLength(BitLockerVolume v, long deviceLength) {
    ArgumentNullException.ThrowIfNull(v);
    return deviceLength / BitLockerLayout.Unit * BitLockerLayout.Unit / v.SectorSize * v.SectorSize;
  }

  private static byte[] Metadata(BitLockerVolume v, long deviceLength) {
    var sb = new StringBuilder();
    var inv = CultureInfo.InvariantCulture;
    sb.Append(inv, $"[bitlocker]\nversion={v.MetadataVersion}\nto_go={(v.IsToGo ? "yes" : "no")}\nguid={v.VolumeGuid}\n");
    sb.Append(inv, $"type={v.EncryptionType}\nstate=0x{v.CurrentState:X4}/0x{v.NextState:X4}\n");
    sb.Append(inv, $"sector_size={v.SectorSize}\nvolume_size={v.VolumeSize}\ndevice_size={deviceLength}\n");
    sb.Append(inv, $"created={v.CreationTime:yyyy-MM-ddTHH:mm:ssZ}\ndescription={v.Description}\n");
    sb.Append(inv, $"cipher=aes-{v.CipherModeName}\nkey_bits={v.KeyBits}\nencryption_method=0x{v.EncryptionMethod:X4}\n");
    for (var i = 0; i < v.MetadataOffsets.Count; ++i)
      sb.Append(inv, $"metadata_offset_{i}={v.MetadataOffsets[i]}\n");
    sb.Append(inv, $"volume_header_offset={v.VolumeHeaderOffset}\nvolume_header_size={v.VolumeHeaderSize}\n");
    for (var i = 0; i < v.Protectors.Count; ++i) {
      var p = v.Protectors[i];
      sb.Append(inv, $"[protector {i}]\nguid={p.Id}\nprotection={p.Protection}\nraw_protection=0x{p.RawProtection:X4}\n");
      if (p.Name is not null) sb.Append(inv, $"name={p.Name}\n");
      sb.Append(inv, $"salt={Convert.ToHexStringLower(p.Salt)}\n");
    }
    return Encoding.UTF8.GetBytes(sb.ToString());
  }
}
