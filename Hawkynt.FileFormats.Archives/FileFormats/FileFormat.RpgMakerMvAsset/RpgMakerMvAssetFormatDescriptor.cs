#pragma warning disable CS1591
using Compression.Registry;
using static Compression.Registry.FormatHelpers;

namespace FileFormat.RpgMakerMvAsset;

/// <summary>
/// RPG Maker MV/MZ encrypted asset — one image or sound of a deployed game, listed as a single
/// decrypted entry. The key (System.json's <c>encryptionKey</c>, 32 hex digits) is the password; a PNG
/// or Ogg asset also decrypts without it, because its header is fixed.
///
/// References:
/// <list type="bullet">
///   <item><description><c>https://github.com/Petschko/RPG-Maker-MV-Decrypter</c> — Petschko's decrypter/encrypter (MIT), the oracle</description></item>
///   <item><description>RPG Maker MV/MZ corescript <c>Decrypter</c> (<c>rpg_core.js</c>) — the engine's own reader</description></item>
/// </list>
/// </summary>
public sealed class RpgMakerMvAssetFormatDescriptor : IFormatDescriptor, IArchiveFormatOperations, IArchiveCreatable {

  public string Id => "RpgMakerMvAsset";
  public string DisplayName => "RPG Maker MV/MZ encrypted asset";
  public FormatCategory Category => FormatCategory.Archive;
  // WORM: one asset in, one asset out; there is nothing to edit in place.
  public FormatCapabilities Capabilities =>
    FormatCapabilities.CanList | FormatCapabilities.CanExtract | FormatCapabilities.CanCreate |
    FormatCapabilities.CanTest | FormatCapabilities.SupportsPassword;
  public string DefaultExtension => ".rpgmvp";
  public IReadOnlyList<string> Extensions => [".rpgmvp", ".rpgmvo", ".rpgmvm", ".png_", ".ogg_", ".m4a_"];
  public IReadOnlyList<string> CompoundExtensions => [];
  public IReadOnlyList<MagicSignature> MagicSignatures => [new(RpgMakerMvAsset.Header.ToArray(), Confidence: 0.95)];
  public IReadOnlyList<FormatMethodInfo> Methods => [new("xor16", "16-byte XOR prefix")];
  public string? TarCompressionFormatId => null;
  public AlgorithmFamily Family => AlgorithmFamily.Archive;
  public string Description =>
    "RPG Maker MV/MZ deployment-encrypted image or sound; password = System.json encryptionKey " +
    "(PNG and Ogg assets decrypt without it). Create needs the key as password and takes one file.";

  public List<ArchiveEntryInfo> List(Stream stream, string? password) {
    var asset = ReadAll(stream);
    var body = RpgMakerMvAsset.Body(asset);
    var key = Key(asset, password);
    var name = key != null ? EntryName(RpgMakerMvAsset.Decrypt(asset, key)) : "decrypted.bin";
    return [new ArchiveEntryInfo(0, name, body.Length, asset.Length, "xor16", false, key == null, null)];
  }

  public void Extract(Stream stream, string outputDir, string? password, string[]? files) {
    var asset = ReadAll(stream);
    var key = Key(asset, password)
      ?? throw new InvalidOperationException("RPG Maker MV asset: this asset needs the game's key (System.json encryptionKey) as password.");
    var plain = RpgMakerMvAsset.Decrypt(asset, key);
    var name = EntryName(plain);
    if (files != null && !MatchesFilter(name, files)) return;
    WriteFile(outputDir, name, plain);
  }

  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(inputs);
    ArgumentNullException.ThrowIfNull(options);
    if (string.IsNullOrEmpty(options.Password))
      throw new ArgumentException("RPG Maker MV asset: give the game's key (32 hex digits) as password.", nameof(options));
    var files = FilesOnly(inputs).ToList();
    if (files.Count != 1)
      throw new ArgumentException($"RPG Maker MV asset: holds exactly one file, {files.Count} given.", nameof(inputs));
    output.Write(RpgMakerMvAsset.Encrypt(files[0].Data, RpgMakerMvAsset.ParseKey(options.Password)));
  }

  private static byte[]? Key(byte[] asset, string? password)
    => !string.IsNullOrEmpty(password) ? RpgMakerMvAsset.ParseKey(password) : RpgMakerMvAsset.RecoverKey(asset);

  private static string EntryName(ReadOnlySpan<byte> plain) => "decrypted." + (RpgMakerMvAsset.SniffExtension(plain) ?? "bin");

  private static byte[] ReadAll(Stream stream) {
    stream.Position = 0;
    using var ms = new MemoryStream();
    stream.CopyTo(ms);
    return ms.ToArray();
  }
}
