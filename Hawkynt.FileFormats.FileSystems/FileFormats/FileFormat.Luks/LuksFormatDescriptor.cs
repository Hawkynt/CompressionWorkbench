#pragma warning disable CS1591
using System.Globalization;
using System.Text;
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.Luks;

/// <summary>
/// LUKS1/LUKS2 encrypted volume (dm-crypt). Lists the header as <c>metadata.ini</c> and the
/// decrypted data segment as <c>volume.img</c>; extracting the volume needs the passphrase,
/// which opens a PBKDF2 key slot and is checked against the volume-key digest. Creates LUKS1
/// volumes (aes-xts-plain64, PBKDF2-SHA256) that cryptsetup opens. Argon2 key slots and ciphers
/// other than AES-XTS/CBC are refused.
/// </summary>
/// <remarks>Specification: <c>docs/LUKS-ON-DISK.md</c>.</remarks>
public sealed class LuksFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable, ISyntheticEntryNames, IFormatOptionsSchema {
  public const string VolumeName = "volume.img";
  public const string MetadataName = "metadata.ini";
  private static readonly IReadOnlySet<string> Synthetic = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { MetadataName };

  public string Id => "Luks";
  public string DisplayName => "LUKS";
  public FormatCategory Category => FormatCategory.Archive;
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsPassword | FormatCapabilities.SupportsMultipleEntries;
  public string DefaultExtension => ".luks";
  public IReadOnlyList<string> Extensions => [".luks"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [new([(byte)'L', (byte)'U', (byte)'K', (byte)'S', 0xBA, 0xBE], Confidence: 0.95)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("aes-xts-plain64", "AES-XTS (plain64)")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description => "LUKS1/LUKS2 encrypted volume (PBKDF2 key slots, AES-XTS/CBC); creates LUKS1";
  public IReadOnlySet<string> SyntheticEntryNames => Synthetic;

  public IReadOnlyList<FormatOptionDescriptor> OptionsSchema { get; } = [
    new("Iterations", "PBKDF2 iterations", FormatOptionKind.Integer, "100000", null,
      "PBKDF2-SHA256 iterations for the key slot and the volume-key digest (cryptsetup requires at least 1000).", IsOptimizationAxis: false),
  ];

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    try {
      var v = LuksVolume.Read(stream);
      var metadata = Metadata(v);
      var length = v.PayloadLength(stream.Length);
      return [
        new(0, MetadataName, metadata.Length, metadata.Length, "stored", false, false, null),
        new(1, VolumeName, length, length, $"{v.Cipher}-{v.CipherMode}", false, true, null),
      ];
    } catch (Exception e) when (e is InvalidDataException or NotSupportedException or System.Text.Json.JsonException or KeyNotFoundException or FormatException) {
      var length = stream.CanSeek ? stream.Length : 0;
      return [new(0, "FULL.luks", length, length, "stored", false, false, null)];
    }
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var v = LuksVolume.Read(stream);
    if (files is not { Length: > 0 } || MatchesFilter(MetadataName, files))
      WriteFile(outputDir, MetadataName, Metadata(v));
    if (files is { Length: > 0 } && !MatchesFilter(VolumeName, files))
      return;
    if (password is null)
      throw new InvalidOperationException("LUKS: the volume is encrypted; a passphrase is required.");
    var key = v.Unlock(stream, password);
    using var target = CreateEntryFile(outputDir, VolumeName);
    v.DecryptPayload(stream, key, target);
  }

  /// <summary>Encrypts the single input (whole 512-byte sectors) into a new LUKS1 volume.</summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(options);
    if (string.IsNullOrEmpty(options.Password))
      throw new ArgumentException("LUKS creation needs a passphrase.", nameof(options));
    byte[]? payload = null;
    foreach (var input in inputs) {
      if (input.IsDirectory || Path.GetFileName(input.ArchiveName).Equals(MetadataName, StringComparison.OrdinalIgnoreCase)) continue;
      if (payload is not null) throw new ArgumentException("LUKS holds exactly one volume image.", nameof(inputs));
      payload = input.ReadContent();
    }
    if (payload is null) throw new ArgumentException("LUKS needs the volume image to encrypt.", nameof(inputs));
    var iterations = options.TryGetInt("Iterations", out var it) ? it : 100_000;
    LuksVolume.WriteLuks1(output, payload, options.Password, iterations);
  }

  private static byte[] Metadata(LuksVolume v) {
    var sb = new StringBuilder();
    var inv = CultureInfo.InvariantCulture;
    sb.Append(inv, $"[luks]\nversion={v.Version}\ncipher={v.Cipher}-{v.CipherMode}\nkey_bits={v.KeyBytes * 8}\nhash={v.Hash}\n");
    sb.Append(inv, $"uuid={v.Uuid}\nlabel={v.Label}\npayload_offset={v.PayloadOffset}\nsector_size={v.PayloadSectorSize}\n");
    foreach (var s in v.KeySlots)
      sb.Append(inv, $"[keyslot {s.Index}]\nactive={(s.Active ? "yes" : "no")}\nkdf={s.Kdf}\nhash={s.KdfHash}\niterations={s.Iterations}\nstripes={s.Stripes}\n");
    return Encoding.UTF8.GetBytes(sb.ToString());
  }
}
