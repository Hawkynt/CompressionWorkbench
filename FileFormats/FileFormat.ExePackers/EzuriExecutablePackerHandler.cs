#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Compression.Core.ExecutableUnpacking;

namespace FileFormat.ExePackers;

/// <summary>
/// Static unpacker for Ezuri (github.com/guitmz/ezuri) ELF crypters. Ezuri
/// appends the AES key, the AES IV and the AES-256-CFB ciphertext of the
/// original ELF directly after its Go loader stub, in the clear:
/// <c>[stub ELF][32-byte key][16-byte IV][AES-256-CFB ciphertext]</c>.
/// The loader recovers the original by seeking to the appended key/IV and
/// decrypting the tail, so the key material is fully present in the file and
/// the original executable is recoverable byte-for-byte without running it.
/// </summary>
/// <remarks>
/// The key and IV are 32/16 ASCII characters drawn from Ezuri's fixed alphabet
/// (<see cref="AllowedChars"/>). The stub is a stripped Go binary whose true
/// file length equals the end of its last non-<c>SHT_NOBITS</c> section; the
/// appended blob begins immediately after. Recovery is validated by decrypting
/// the ciphertext with AES-256-CFB (128-bit feedback, matching Go's
/// <c>cipher.NewCFBDecrypter</c>) and confirming the result is a valid ELF.
/// </remarks>
public sealed class EzuriExecutablePackerHandler : IExecutablePackerHandler {
  private const string AllowedChars =
    "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ@#$%0123456789";
  private const int KeyLength = 32;
  private const int IvLength = 16;

  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "ezuri";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "Ezuri ELF crypter (AES-256-CFB)";

  /// <summary>
  /// Gets the capabilities.
  /// </summary>
  public ExecutableUnpackCapabilities Capabilities =>
    ExecutableUnpackCapabilities.CanDetect |
    ExecutableUnpackCapabilities.CanLocatePayload |
    ExecutableUnpackCapabilities.CanDecompressPayload |
    ExecutableUnpackCapabilities.CanRebuildExecutable |
    ExecutableUnpackCapabilities.SupportsElf |
    ExecutableUnpackCapabilities.SupportsX64;

  /// <summary>
  /// Performs the detect operation.
  /// </summary>
  public DetectionResult Detect(ReadOnlySpan<byte> image) {
    if (TryLocate(image.ToArray(), out _, out _, out _))
      return new(true, this.Id, 1.0, []);
    return new(false, this.Id, 0,
      [new(ExecutableDiagnosticCode.NotPackedExecutable,
        "No Ezuri loader with an appended AES-256-CFB key/IV/ciphertext trailer was found.", true)]);
  }

  /// <summary>
  /// Parses the value from the supplied data.
  /// </summary>
  public PackedExecutable Parse(ReadOnlySpan<byte> image, DetectionResult detection) {
    var bytes = image.ToArray();
    var info = ExecutableContainerParsers.ParseBestEffort(image);
    return new(this.Id, bytes, detection, info, this.Capabilities, new Dictionary<string, string> {
      ["packer"] = this.Id,
      ["container"] = info.Container.ToString(),
      ["architecture"] = info.Architecture.ToString(),
      ["cipher"] = "aes-256-cfb",
    });
  }

  /// <summary>
  /// Performs the unpack operation.
  /// </summary>
  public UnpackResult Unpack(PackedExecutable packed, UnpackOptions options) {
    var image = packed.OriginalImage;
    if (image.LongLength > options.MaximumInputSize)
      return new(ExecutableUnpackLevel.DetectionOnly, ExecutableUnpackCapabilities.CanDetect, [],
        [new(ExecutableDiagnosticCode.PayloadNotFound, "Input exceeds configured executable unpacking size limit.", true)]);

    if (!TryLocate(image, out var stubEnd, out var key, out var iv))
      return new(ExecutableUnpackLevel.DetectionOnly, ExecutableUnpackCapabilities.CanDetect, [],
        [new(ExecutableDiagnosticCode.PayloadNotFound, "Ezuri key/IV trailer could not be located.", true)]);

    var ciphertext = image.AsSpan(stubEnd + KeyLength + IvLength).ToArray();
    var recovered = DecryptCfb(ciphertext, key, iv);
    var isElf = recovered.Length >= 4 && recovered[0] == 0x7F && recovered[1] == 'E' && recovered[2] == 'L' && recovered[3] == 'F';

    var diagnostics = new List<ExecutableDiagnostic>();
    var level = isElf ? ExecutableUnpackLevel.RebuiltExecutable : ExecutableUnpackLevel.PayloadDecompressed;
    if (!isElf)
      diagnostics.Add(new(ExecutableDiagnosticCode.ExecutableRebuildFailed,
        "Ezuri payload decrypted but the result does not carry an ELF header.", options.StrictRebuild));

    var artifacts = new List<UnpackArtifact> {
      new("metadata.ini", BuildMetadata(image.Length, stubEnd, ciphertext.Length, recovered.Length, isElf), "stored"),
      new("original_packed.bin", image, "stored"),
      new("key.bin", key, "stored"),
      new("iv.bin", iv, "stored"),
      new("encrypted_payload.bin", ciphertext, "aes-256-cfb"),
      new("decrypted_payload.bin", recovered, "stored"),
    };
    if (isElf)
      artifacts.Add(new("reconstructed/original_executable.bin", recovered, "stored"));

    var caps = ExecutableUnpackCapabilities.CanDetect | ExecutableUnpackCapabilities.CanLocatePayload |
      ExecutableUnpackCapabilities.CanDecompressPayload | ExecutableUnpackCapabilities.SupportsElf |
      ExecutableUnpackCapabilities.SupportsX64;
    if (isElf) caps |= ExecutableUnpackCapabilities.CanRebuildExecutable;

    var result = new UnpackResult(level, caps, artifacts, diagnostics);
    artifacts.Add(new("diagnostics.json", ExecutableDiagnosticsJson.Build(this.Id, packed.ImageInfo, result), "stored"));
    return result with { Artifacts = artifacts };
  }

  /// <summary>
  /// Locates the appended key/IV trailer: computes the stub's true file length
  /// (the end of its last loaded section), verifies the following 48 bytes are
  /// Ezuri key/IV characters and that the remaining ciphertext AES-256-CFB
  /// decrypts to an ELF. This is the reliable detector because a coincidental
  /// 48-printable-byte run that also decrypts to <c>\x7fELF</c> is vanishingly
  /// unlikely.
  /// </summary>
  private static bool TryLocate(byte[] image, out int stubEnd, out byte[] key, out byte[] iv) {
    stubEnd = 0;
    key = [];
    iv = [];
    if (!TryGetElf64SectionEnd(image, out var end))
      return false;
    if (end < 0 || (long)end + KeyLength + IvLength + 16 > image.Length)
      return false;

    var candidateKey = image.AsSpan(end, KeyLength);
    var candidateIv = image.AsSpan(end + KeyLength, IvLength);
    if (!IsAllowedRun(candidateKey) || !IsAllowedRun(candidateIv))
      return false;

    // Decrypt only the first block to gate detection cheaply.
    var head = image.AsSpan(end + KeyLength + IvLength, 16).ToArray();
    var probe = DecryptCfb(head, candidateKey.ToArray(), candidateIv.ToArray());
    if (probe[0] != 0x7F || probe[1] != 'E' || probe[2] != 'L' || probe[3] != 'F')
      return false;

    stubEnd = end;
    key = candidateKey.ToArray();
    iv = candidateIv.ToArray();
    return true;
  }

  private static bool IsAllowedRun(ReadOnlySpan<byte> bytes) {
    foreach (var b in bytes)
      if (AllowedChars.IndexOf((char)b) < 0)
        return false;
    return true;
  }

  /// <summary>
  /// Returns the offset just past the last byte belonging to any loaded
  /// (non-<c>SHT_NOBITS</c>) ELF64 section — the true length of the Go stub
  /// before Ezuri's appended trailer.
  /// </summary>
  private static bool TryGetElf64SectionEnd(byte[] image, out int end) {
    end = 0;
    if (image.Length < 0x40 || image[0] != 0x7F || image[1] != 'E' || image[2] != 'L' || image[3] != 'F')
      return false;
    if (image[4] != 2 || image[5] != 1) // ELFCLASS64, little-endian
      return false;

    var shoff = BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan(0x28));
    var shentsize = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(0x3A));
    var shnum = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(0x3C));
    if (shoff == 0 || shentsize < 64 || shnum == 0 || shoff > (ulong)image.Length)
      return false;
    if (shoff + (ulong)shnum * shentsize > (ulong)image.Length)
      return false;

    ulong max = 0;
    for (var i = 0; i < shnum; i++) {
      var o = (int)shoff + i * shentsize;
      var type = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(o + 4));
      if (type == 8) // SHT_NOBITS occupies no file space
        continue;
      var off = BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan(o + 24));
      var size = BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan(o + 32));
      if (off > (ulong)image.Length || size > (ulong)image.Length)
        continue;
      max = Math.Max(max, off + size);
    }
    if (max == 0 || max > (ulong)image.Length)
      return false;
    end = (int)max;
    return true;
  }

  /// <summary>
  /// AES-256 decryption in CFB mode with 128-bit (full-block) feedback, matching
  /// Go's <c>cipher.NewCFBDecrypter</c> as used by the Ezuri stub.
  /// </summary>
  private static byte[] DecryptCfb(byte[] ciphertext, byte[] key, byte[] iv) {
    var result = new byte[ciphertext.Length];
    using var aes = Aes.Create();
    aes.Key = key;
    aes.Mode = CipherMode.ECB;
    aes.Padding = PaddingMode.None;

    var feedback = (byte[])iv.Clone();
    for (var pos = 0; pos < ciphertext.Length; pos += 16) {
      var keystream = aes.EncryptEcb(feedback, PaddingMode.None);
      var blockLen = Math.Min(16, ciphertext.Length - pos);
      for (var i = 0; i < blockLen; i++)
        result[pos + i] = (byte)(ciphertext[pos + i] ^ keystream[i]);
      // The next feedback register is the current ciphertext block.
      if (blockLen == 16)
        Array.Copy(ciphertext, pos, feedback, 0, 16);
    }
    return result;
  }

  private static byte[] BuildMetadata(int imageSize, int stubEnd, int ciphertextLength, int recoveredLength, bool isElf) {
    var sb = new StringBuilder();
    sb.AppendLine("[ezuri]");
    sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"image_size = {imageSize}\n");
    sb.AppendLine("container = ELF64");
    sb.AppendLine("cipher = aes-256-cfb");
    sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"stub_size = {stubEnd}\n");
    sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"key_offset = 0x{stubEnd:X}\n");
    sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"iv_offset = 0x{stubEnd + KeyLength:X}\n");
    sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"ciphertext_offset = 0x{stubEnd + KeyLength + IvLength:X}\n");
    sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"ciphertext_size = {ciphertextLength}\n");
    sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"recovered_size = {recoveredLength}\n");
    sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"capability_level = {(isElf ? "RebuiltExecutable" : "PayloadDecompressed")}\n");
    sb.AppendLine("note = Ezuri appends the AES key, IV and ciphertext in the clear; the original ELF is recovered statically by AES-256-CFB decryption.");
    return Encoding.UTF8.GetBytes(sb.ToString());
  }
}
