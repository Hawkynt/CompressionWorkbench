#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Core.ExecutableUnpacking;

namespace FileFormat.ExePackers;

/// <summary>
/// Detector and payload locator for MidgetPack (github.com/arisada/midgetpack)
/// ELF crypters. MidgetPack embeds a descriptor (magic <c>0xf00dbea7</c>) in a
/// precompiled stub, adds a PT_LOAD program header for the appended payload, and
/// appends the AES-128-CBC-encrypted original ELF. The AES key is derived at
/// run time — either by PBKDF2 over a password the user types, or from a
/// Curve25519 key-exchange keyfile — and is <em>never</em> stored in the binary.
/// Static recovery of the plaintext is therefore impossible; this handler
/// honestly reports <see cref="ExecutableUnpackLevel.PayloadLocated"/>, carving
/// the encrypted payload and its descriptor while making the missing-key
/// limitation explicit in diagnostics.
/// </summary>
public sealed class MidgetPackExecutablePackerHandler : IExecutablePackerHandler {
  private const uint Magic = 0xF00DBEA7;

  public string Id => "midgetpack";
  public string DisplayName => "MidgetPack ELF crypter (AES-128-CBC, runtime key)";

  public ExecutableUnpackCapabilities Capabilities =>
    ExecutableUnpackCapabilities.CanDetect |
    ExecutableUnpackCapabilities.CanLocatePayload |
    ExecutableUnpackCapabilities.SupportsElf |
    ExecutableUnpackCapabilities.SupportsX86 |
    ExecutableUnpackCapabilities.SupportsX64 |
    ExecutableUnpackCapabilities.SupportsArm32;

  public DetectionResult Detect(ReadOnlySpan<byte> image) {
    if (TryParseDescriptor(image.ToArray(), out _))
      return new(true, this.Id, 1.0, []);
    return new(false, this.Id, 0,
      [new(ExecutableDiagnosticCode.NotPackedExecutable,
        "No MidgetPack stub descriptor (magic 0xf00dbea7) was found.", true)]);
  }

  public PackedExecutable Parse(ReadOnlySpan<byte> image, DetectionResult detection) {
    var bytes = image.ToArray();
    var info = ExecutableContainerParsers.ParseBestEffort(image);
    return new(this.Id, bytes, detection, info, this.Capabilities, new Dictionary<string, string> {
      ["packer"] = this.Id,
      ["container"] = info.Container.ToString(),
      ["architecture"] = info.Architecture.ToString(),
      ["cipher"] = "aes-128-cbc",
    });
  }

  public UnpackResult Unpack(PackedExecutable packed, UnpackOptions options) {
    var image = packed.OriginalImage;
    if (image.LongLength > options.MaximumInputSize)
      return new(ExecutableUnpackLevel.DetectionOnly, ExecutableUnpackCapabilities.CanDetect, [],
        [new(ExecutableDiagnosticCode.PayloadNotFound, "Input exceeds configured executable unpacking size limit.", true)]);

    if (!TryParseDescriptor(image, out var desc))
      return new(ExecutableUnpackLevel.DetectionOnly, ExecutableUnpackCapabilities.CanDetect, [],
        [new(ExecutableDiagnosticCode.PayloadNotFound, "MidgetPack descriptor could not be parsed.", true)]);

    // Layout: stub | encrypted-payload(data_len) | banner(banner_len).
    var payloadOffset = image.Length - desc.BannerLen - desc.DataLen;
    if (payloadOffset < 0 || payloadOffset + desc.DataLen > image.Length)
      return new(ExecutableUnpackLevel.DetectionOnly, ExecutableUnpackCapabilities.CanDetect, [],
        [new(ExecutableDiagnosticCode.PayloadNotFound, "MidgetPack payload region falls outside the file.", true)]);

    var payload = image.AsSpan(payloadOffset, desc.DataLen).ToArray();
    var mode = desc.Type == 2 ? "curve25519" : "password";
    var diagnostics = new List<ExecutableDiagnostic> {
      new(ExecutableDiagnosticCode.TransformNotReversible,
        $"MidgetPack encrypts the payload with AES-128-CBC under a {mode}-derived session key that is not stored in the binary; static decryption is impossible without the {(desc.Type == 2 ? "Curve25519 keyfile" : "password")}. Encrypted payload is located only."),
    };

    var artifacts = new List<UnpackArtifact> {
      new("metadata.ini", BuildMetadata(image.Length, desc, payloadOffset), "stored"),
      new("original_packed.bin", image, "stored"),
      new("encrypted_payload.bin", payload, "aes-128-cbc"),
    };

    var caps = ExecutableUnpackCapabilities.CanDetect | ExecutableUnpackCapabilities.CanLocatePayload |
      ExecutableUnpackCapabilities.SupportsElf | ExecutableUnpackCapabilities.SupportsX86 |
      ExecutableUnpackCapabilities.SupportsX64 | ExecutableUnpackCapabilities.SupportsArm32;
    var result = new UnpackResult(ExecutableUnpackLevel.PayloadLocated, caps, artifacts, diagnostics);
    artifacts.Add(new("diagnostics.json", ExecutableDiagnosticsJson.Build(this.Id, packed.ImageInfo, result), "stored"));
    return result with { Artifacts = artifacts };
  }

  private readonly record struct Descriptor(int Offset, bool Is64, int DataLen, int BannerLen, uint Type, uint HashLoops);

  /// <summary>
  /// Finds the MidgetPack descriptor by its magic and validates the fields that
  /// gate a real pack (pack type 1/2 and a payload length that fits the file),
  /// then reads the field offsets for the ELF class in play.
  /// </summary>
  private static bool TryParseDescriptor(byte[] image, out Descriptor descriptor) {
    descriptor = default;
    if (image.Length < 0x40 || image[0] != 0x7F || image[1] != 'E' || image[2] != 'L' || image[3] != 'F')
      return false;
    var is64 = image[4] == 2;

    // stub_data offsets (padded C layout): 64-bit data_len@16 banner_len@32 type@36;
    // 32-bit data_len@8 banner_len@16 type@20.
    var dataLenOff = is64 ? 16 : 8;
    var bannerLenOff = is64 ? 32 : 16;
    var typeOff = is64 ? 36 : 20;
    var hashLoopsOff = is64 ? 80 : 40;
    var structSize = is64 ? 112 : 56;

    var magic = new byte[4];
    BinaryPrimitives.WriteUInt32LittleEndian(magic, Magic);
    var search = 0;
    while (true) {
      var at = IndexOf(image, magic, search);
      if (at < 0)
        return false;
      search = at + 1;
      if (at + structSize > image.Length)
        continue;

      var type = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(at + typeOff));
      if (type is not (1 or 2))
        continue;
      var dataLenRaw = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(at + dataLenOff));
      var bannerLenRaw = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(at + bannerLenOff));
      if (dataLenRaw == 0 || dataLenRaw > (uint)image.Length || bannerLenRaw > (uint)image.Length)
        continue;
      if ((long)dataLenRaw + bannerLenRaw > image.Length)
        continue;

      descriptor = new(at, is64, (int)dataLenRaw, (int)bannerLenRaw, type,
        BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(at + hashLoopsOff)));
      return true;
    }
  }

  private static int IndexOf(byte[] haystack, ReadOnlySpan<byte> needle, int start) {
    for (var i = start; i + needle.Length <= haystack.Length; i++) {
      var match = true;
      for (var j = 0; j < needle.Length; j++)
        if (haystack[i + j] != needle[j]) { match = false; break; }
      if (match)
        return i;
    }
    return -1;
  }

  private static byte[] BuildMetadata(int imageSize, Descriptor desc, int payloadOffset) {
    var sb = new StringBuilder();
    sb.AppendLine("[midgetpack]");
    sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"image_size = {imageSize}\n");
    sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"container = {(desc.Is64 ? "ELF64" : "ELF32")}\n");
    sb.AppendLine("cipher = aes-128-cbc");
    sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"pack_type = {(desc.Type == 2 ? "curve25519" : "password")}\n");
    sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"descriptor_offset = 0x{desc.Offset:X}\n");
    sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"payload_offset = 0x{payloadOffset:X}\n");
    sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"payload_size = {desc.DataLen}\n");
    sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"banner_size = {desc.BannerLen}\n");
    if (desc.Type == 1)
      sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"pbkdf2_iterations = {desc.HashLoops}\n");
    sb.AppendLine("capability_level = PayloadLocated");
    sb.AppendLine("note = AES-128-CBC key derived at runtime (PBKDF2 password or Curve25519 keyfile); not embedded, so plaintext cannot be recovered statically.");
    return Encoding.UTF8.GetBytes(sb.ToString());
  }
}
