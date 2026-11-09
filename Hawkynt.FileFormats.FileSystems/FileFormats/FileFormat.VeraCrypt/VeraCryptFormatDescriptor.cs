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
  public const string ProtectHiddenName = "ProtectHidden", ProtectHiddenPimName = "ProtectHiddenPim", ProtectHiddenKeyfilesName = "ProtectHiddenKeyfiles";
  public const string HiddenVolumeName = "hidden.img";
  public const string HiddenPasswordName = "HiddenPassword", HiddenSizeName = "HiddenSize", HiddenCipherName = "HiddenCipher", HiddenPrfName = "HiddenPrf", HiddenPimName = "HiddenPim";

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
    new(HiddenPasswordName, "Hidden-volume password", FormatOptionKind.String, "", null,
      "With a hidden.img input: the hidden volume's password (it must differ from the outer one).", IsOptimizationAxis: false),
    new(HiddenSizeName, "Hidden-volume size (bytes)", FormatOptionKind.Integer, "0", null,
      "Hidden data-area size; 0 for the size of hidden.img. It sits at the end of the outer volume, which must leave that space free.", IsOptimizationAxis: false),
    new(HiddenCipherName, "Hidden-volume cipher", FormatOptionKind.String, "", null,
      "Cipher or cascade of the hidden volume; empty for the outer volume's.", IsOptimizationAxis: false),
    new(HiddenPrfName, "Hidden-volume PRF", FormatOptionKind.String, "", null,
      "Header-key PRF of the hidden volume; empty for the outer volume's.", IsOptimizationAxis: false),
    new(HiddenPimName, "Hidden-volume PIM", FormatOptionKind.Integer, "0", null,
      "VeraCrypt PIM of the hidden volume.", IsOptimizationAxis: false),
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
    var options = UnlockOptions(credentials.Get);
    var v = Unlock(image, credentials.Password, options);
    if (credentials.Get(ProtectHiddenName) is not { } hiddenPassword || options.Header == TcHeaderKind.Hidden)
      return v.OpenBlockDevice(image, writable, leaveOpen);

    // Hidden-volume protection: the hidden header tells where the hidden data starts.
    var hiddenKeyfiles = credentials.Get(ProtectHiddenKeyfilesName) is { Length: > 0 } list
      ? list.Split(['\n', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(File.ReadAllBytes).ToArray()
      : [];
    var hidden = TcVolume.Open(image, hiddenPassword, new TcUnlockOptions {
      Header = TcHeaderKind.Hidden, Keyfiles = hiddenKeyfiles,
      Pim = credentials.Get(ProtectHiddenPimName) is { } p ? int.Parse(p, NumberStyles.Integer, CultureInfo.InvariantCulture) : 0,
    }) ?? throw new ArchivePasswordException("VeraCrypt/TrueCrypt: the hidden-volume protection password does not open a hidden volume.", passwordWasSupplied: true);
    var firstProtected = Math.Max(0, (hidden.DataOffset - v.DataOffset) / TcSectorCipher.SectorSize);
    return new HiddenProtectedBlockDevice(v.OpenBlockDevice(image, writable, leaveOpen), firstProtected);
  }

  /// <summary>
  /// Encrypts <c>volume.img</c> into a new container; with a <c>hidden.img</c> input and a
  /// <c>HiddenPassword</c>, also writes a hidden volume at the end of the outer one, after proving
  /// from the outer filesystem's allocation map (or, without a recognised filesystem, from the
  /// outer image's length) that the space is free.
  /// </summary>
  public void Create(Stream output, IReadOnlyList<ArchiveInputInfo> inputs, FormatCreateOptions options) {
    ArgumentNullException.ThrowIfNull(output);
    ArgumentNullException.ThrowIfNull(options);
    if (string.IsNullOrEmpty(options.Password))
      throw new ArgumentException("VeraCrypt/TrueCrypt creation needs a password.", nameof(options));
    byte[]? payload = null, hiddenPayload = null;
    foreach (var input in inputs) {
      var name = Path.GetFileName(input.ArchiveName);
      if (input.IsDirectory || name.Equals(MetadataName, StringComparison.OrdinalIgnoreCase)) continue;
      if (name.Equals(HiddenVolumeName, StringComparison.OrdinalIgnoreCase)) {
        hiddenPayload = input.ReadContent();
        continue;
      }
      if (payload is not null) throw new ArgumentException("A VeraCrypt/TrueCrypt container holds one volume image (and optionally hidden.img).", nameof(inputs));
      payload = input.ReadContent();
    }
    if (payload is null) throw new ArgumentException("VeraCrypt/TrueCrypt needs the volume image to encrypt.", nameof(inputs));
    var trueCrypt = (options.GetString(FormatName) ?? "VeraCrypt").Equals("TrueCrypt", StringComparison.OrdinalIgnoreCase);
    var outerOptions = new TcCreateOptions {
      VeraCrypt = !trueCrypt,
      Cipher = options.GetString(CipherName) ?? "aes",
      Prf = options.GetString(PrfName) ?? (trueCrypt ? "ripemd160" : "sha512"),
      Pim = options.TryGetInt(PimName, out var pim) ? pim : 0,
      VolumeSize = options.GetString(SizeName) is { } s ? long.Parse(s, NumberStyles.Integer, CultureInfo.InvariantCulture) : 0,
      Sparse = IsTrue(options.GetString(SparseName)),
    };
    if (hiddenPayload is null) {
      TcVolumeWriter.Write(output, payload, options.Password, outerOptions);
      return;
    }

    var hiddenPassword = options.GetString(HiddenPasswordName);
    if (string.IsNullOrEmpty(hiddenPassword) || hiddenPassword == options.Password)
      throw new ArgumentException("A hidden volume needs its own HiddenPassword, different from the outer one.", nameof(options));
    var hiddenOptions = outerOptions with {
      Cipher = options.GetString(HiddenCipherName) is { Length: > 0 } hc ? hc : outerOptions.Cipher,
      Prf = options.GetString(HiddenPrfName) is { Length: > 0 } hp ? hp : outerOptions.Prf,
      Pim = options.TryGetInt(HiddenPimName, out var hpim) ? hpim : 0,
      VolumeSize = 0, Sparse = false,
    };
    var hiddenSize = options.GetString(HiddenSizeName) is { } hs && long.Parse(hs, NumberStyles.Integer, CultureInfo.InvariantCulture) is > 0 and var h
      ? h : hiddenPayload.Length;

    // The hidden volume is written into the finished outer container, which therefore has to be read back.
    var work = output.CanRead && output.CanSeek ? output : new MemoryStream();
    var start = work.Position;
    TcVolumeWriter.Write(work, payload, options.Password, outerOptions);
    using (var container = new SubStream(work, start, work.Length - start)) {
      var outer = TcVolume.Open(container, options.Password, new TcUnlockOptions { Pim = outerOptions.Pim, Prf = outerOptions.ResolveKdf().Prf, Cipher = outerOptions.ResolveChain().Name })
        ?? throw new InvalidOperationException("The outer volume just written does not reopen.");
      var freeFrom = TcHiddenVolume.ProvenFreeFrom(outer, container) ?? payload.Length;
      TcHiddenVolume.Write(container, outer, freeFrom, hiddenPayload, hiddenPassword, hiddenOptions, hiddenSize);
    }
    if (!ReferenceEquals(work, output)) {
      work.Position = 0;
      work.CopyTo(output);
    }
  }

  /// <summary>A window onto <c>[offset, offset + length)</c> of a seekable stream, so container offsets stay relative to the container.</summary>
  private sealed class SubStream(Stream inner, long offset, long length) : Stream {
    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => inner.CanWrite;
    public override long Length => length;
    public override long Position { get; set; }
    public override void Flush() => inner.Flush();
    public override int Read(byte[] buffer, int index, int count) {
      inner.Position = offset + this.Position;
      var n = inner.Read(buffer, index, (int)Math.Min(count, Math.Max(0, length - this.Position)));
      this.Position += n;
      return n;
    }
    public override void Write(byte[] buffer, int index, int count) {
      if (this.Position + count > length) throw new IOException("Write past the end of the container.");
      inner.Position = offset + this.Position;
      inner.Write(buffer, index, count);
      this.Position += count;
    }
    public override long Seek(long o, SeekOrigin origin) => this.Position = origin switch {
      SeekOrigin.Begin => o, SeekOrigin.Current => this.Position + o, _ => length + o,
    };
    public override void SetLength(long value) => throw new NotSupportedException();
    protected override void Dispose(bool disposing) { }
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
