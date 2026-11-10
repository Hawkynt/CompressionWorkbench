#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.VeraCrypt;

/// <summary>
/// VeraCrypt and TrueCrypt containers. Nothing in an encrypted header is recognisable, so they are
/// found by extension (<c>.hc</c>, <c>.tc</c>) or chosen by id. With the password, the derivation
/// and cipher chain are found by trial as the tools do: every VeraCrypt PRF (SHA-512, SHA-256,
/// Whirlpool, BLAKE2s, Streebog, RIPEMD-160, Argon2id) and PIM, the TrueCrypt PRFs back to
/// SHA-1, AES/Serpent/Twofish/Camellia/Kuznyechik and every cascade in XTS, and TrueCrypt 4.x's
/// LRW and CBC modes with Blowfish, CAST5 and Triple DES. Lists and extracts the decrypted volume,
/// mounts the filesystem inside read-only or read-write, and creates VeraCrypt or TrueCrypt
/// containers, optionally sparse.
/// </summary>
/// <remarks>Specification: <c>docs/VERACRYPT-ON-DISK.md</c>.</remarks>
public sealed class VeraCryptFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, IFormatOptionsSchema, IEncryptedBlockDeviceProvider {
  public const string VolumeName = "volume.img";
  public const string MetadataName = "metadata.ini";
  public const string PimName = "Pim", PrfName = "Prf", CipherName = "Cipher", HiddenName = "Hidden", KeyfilesName = "Keyfiles";
  public const string FormatName = "Format", SizeName = "Size", SparseName = "Sparse";

  public string Id => "VeraCrypt";
  public string DisplayName => "VeraCrypt / TrueCrypt";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.SupportsPassword | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".hc";
  public IReadOnlyList<string> Extensions => [".hc", ".tc"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("aes-xts", "AES-XTS"), new("serpent-twofish-aes-xts", "AES(Twofish(Serpent))-XTS")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "VeraCrypt/TrueCrypt container (all PRFs, ciphers and cascades; TrueCrypt 4.x LRW/CBC); mounts with the password; creates, optionally sparse";

  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    new(FormatName, "Container format", FormatOptionKind.Enum, "VeraCrypt", ["VeraCrypt", "TrueCrypt"],
      "VeraCrypt (VERA header, PIM-capable) or the TrueCrypt 7.1 format (TRUE header).", IsOptimizationAxis: false),
    new(CipherName, "Cipher", FormatOptionKind.String, "aes", null,
      "Cipher or cascade, as cryptsetup names it (serpent-twofish-aes) or as VeraCrypt does (AES-Twofish-Serpent); XTS mode.", IsOptimizationAxis: false),
    new(PrfName, "PRF", FormatOptionKind.String, "sha512", null,
      "Header-key derivation: sha512, sha256, whirlpool, blake2s-256, stribog512, ripemd160 or argon2id (VeraCrypt); ripemd160, sha512 or whirlpool (TrueCrypt).", IsOptimizationAxis: false),
    new(PimName, "PIM", FormatOptionKind.Integer, "0", null,
      "VeraCrypt Personal Iterations Multiplier; 0 for the default iterations.", IsOptimizationAxis: false),
    new(SizeName, "Volume size (bytes)", FormatOptionKind.Integer, "0", null,
      "Data-area size; 0 for the size of the volume image. Larger leaves room the filesystem can grow into.", IsOptimizationAxis: false),
    new(SparseName, "Sparse", FormatOptionKind.Boolean, "false", null,
      "Leave the unused data area unwritten (a sparse/dynamic container) instead of filling it with random bytes.", IsOptimizationAxis: false),
  ];

  /// <summary>Unlock options from mount parameters (<c>Pim</c>, <c>Prf</c>, <c>Cipher</c>, <c>Hidden</c>, <c>Keyfiles</c>).</summary>
  public static TcUnlockOptions UnlockOptions(Func<string, string?> get) {
    ArgumentNullException.ThrowIfNull(get);
    var keyfiles = get(KeyfilesName) is { Length: > 0 } list
      ? list.Split(['\n', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(File.ReadAllBytes).ToArray()
      : [];
    return new TcUnlockOptions {
      Pim = get(PimName) is { } pim ? int.Parse(pim, NumberStyles.Integer, CultureInfo.InvariantCulture) : 0,
      Prf = get(PrfName),
      Cipher = get(CipherName),
      Header = IsTrue(get(HiddenName)) ? TcHeaderKind.Hidden : TcHeaderKind.Standard,
      Keyfiles = keyfiles,
    };
  }

  private static bool IsTrue(string? value) => value is not null && (value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase) || value.Equals("yes", StringComparison.OrdinalIgnoreCase));

  private static TcVolume Unlock(Stream stream, string? password, TcUnlockOptions options) {
    if (password is null && options.Keyfiles.Count == 0)
      throw new ArchivePasswordException("VeraCrypt/TrueCrypt: the volume is encrypted; a password is required.", passwordWasSupplied: false);
    return TcVolume.Open(stream, password ?? "", options)
      ?? throw new ArchivePasswordException("VeraCrypt/TrueCrypt: no header opens with this password (or it is not such a volume).", passwordWasSupplied: true);
  }

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    if (password is not null && TcVolume.Open(stream, password) is { } v) {
      var metadata = Metadata(v);
      return [
        new(0, MetadataName, metadata.Length, metadata.Length, "stored", false, false, null),
        new(1, VolumeName, v.DataLength, v.DataLength, $"{v.Chain.DisplayName}-{v.Chain.Mode}", false, true, null),
      ];
    }
    var length = Math.Max(0, (stream.CanSeek ? stream.Length : 0) - 2 * TcVolume.DataAreaOffset);
    return [new(0, VolumeName, length, length, "encrypted", false, true, null)];
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var v = Unlock(stream, password, new());
    if (files is not { Length: > 0 } || MatchesFilter(MetadataName, files))
      WriteFile(outputDir, MetadataName, Metadata(v));
    if (files is { Length: > 0 } && !MatchesFilter(VolumeName, files))
      return;
    using var device = v.OpenBlockDevice(stream, writable: false);
    using var target = CreateEntryFile(outputDir, VolumeName);
    var buffer = new byte[256 * 512];
    for (long block = 0; block < device.Geometry.BlockCount;) {
      var n = (int)Math.Min(256, device.Geometry.BlockCount - block);
      device.ReadBlocks(block, buffer.AsSpan(0, n * 512));
      target.Write(buffer, 0, n * 512);
      block += n;
    }
  }

  public IRandomAccessBlockDevice OpenBlockDevice(Stream image, EncryptedVolumeCredentials credentials, bool writable, bool leaveOpen = true) {
    ArgumentNullException.ThrowIfNull(image);
    ArgumentNullException.ThrowIfNull(credentials);
    if (writable && !image.CanWrite)
      throw new NotSupportedException("A writable VeraCrypt/TrueCrypt volume needs a writable container stream.");
    var v = Unlock(image, credentials.Password, UnlockOptions(credentials.Get));
    return v.OpenBlockDevice(image, writable, leaveOpen);
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(options);
    if (string.IsNullOrEmpty(options.Password))
      throw new ArgumentException("VeraCrypt/TrueCrypt creation needs a password.", nameof(options));
    byte[]? payload = null;
    foreach (var input in inputs) {
      if (input.IsDirectory || Path.GetFileName(input.ArchiveName).Equals(MetadataName, StringComparison.OrdinalIgnoreCase)) continue;
      if (payload is not null) throw new ArgumentException("A VeraCrypt/TrueCrypt container holds exactly one volume image.", nameof(inputs));
      payload = input.ReadContent();
    }
    if (payload is null) throw new ArgumentException("VeraCrypt/TrueCrypt needs the volume image to encrypt.", nameof(inputs));
    var trueCrypt = (options.GetString(FormatName) ?? "VeraCrypt").Equals("TrueCrypt", StringComparison.OrdinalIgnoreCase);
    var size = options.GetString(SizeName) is { } s ? long.Parse(s, NumberStyles.Integer, CultureInfo.InvariantCulture) : 0;
    TcVolumeWriter.Write(output, payload, options.Password, new TcCreateOptions {
      VeraCrypt = !trueCrypt,
      Cipher = options.GetString(CipherName) ?? "aes",
      Prf = options.GetString(PrfName) ?? (trueCrypt ? "ripemd160" : "sha512"),
      Pim = options.TryGetInt(PimName, out var pim) ? pim : 0,
      VolumeSize = size,
      Sparse = IsTrue(options.GetString(SparseName)),
    });
  }

  private static byte[] Metadata(TcVolume v) {
    var sb = new StringBuilder();
    var inv = CultureInfo.InvariantCulture;
    sb.Append(inv, $"[volume]\nformat={(v.VeraCrypt ? "VeraCrypt" : "TrueCrypt")}\nheader_version={v.Version}\nminimum_program_version={v.MinimumVersion:X4}\n");
    sb.Append(inv, $"prf={v.Kdf.Prf}\niterations={v.Kdf.Iterations}\ncipher={v.Chain.DisplayName}\nmode={v.Chain.Mode}\n");
    sb.Append(inv, $"data_offset={v.DataOffset}\ndata_length={v.DataLength}\nsector_size={v.SectorSize}\nflags={v.Flags}\n");
    return Encoding.UTF8.GetBytes(sb.ToString());
  }
}
