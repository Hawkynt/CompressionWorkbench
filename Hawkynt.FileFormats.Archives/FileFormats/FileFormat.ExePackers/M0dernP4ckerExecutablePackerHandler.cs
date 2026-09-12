#pragma warning disable CS1591
using System.Buffers.Binary;
using System.Text;
using Compression.Core.ExecutableUnpacking;

namespace FileFormat.ExePackers;

/// <summary>
/// Static unpacker for m0dern_p4cker (github.com/n4sm/m0dern_p4cker) ELF64
/// packers. The packer encrypts the original <c>.text</c> section in place with
/// a random single-byte key, copies its assembly stub into the code cave after
/// the executable segment, and repoints <c>e_entry</c> at the stub. The stub
/// carries the key and the original entry point as patched <c>mov</c>
/// immediates, so both are recoverable statically: the encrypted <c>.text</c>
/// is decrypted to the original bytes and the original entry point restored.
/// </summary>
/// <remarks>
/// Three cipher stubs exist. <c>xor</c> decrypts as <c>plain = cipher ^ key</c>;
/// <c>not</c> decrypts as <c>plain = (cipher ^ 0xFF) ^ key</c>; the <c>xorp</c>
/// (rotate/compound) stub applies a multi-round transform with a second
/// parameter and is reported at <see cref="ExecutableUnpackLevel.PayloadLocated"/>
/// only. The code-cave stub bytes overwrote inter-segment padding in the
/// original and are retained as inert data, exactly as the reference
/// Silent_Packer unpacker retains its loader section.
/// </remarks>
public sealed class M0dernP4ckerExecutablePackerHandler : IExecutablePackerHandler {
  // lodsb; xor al,dl; stosb; loop  -> plain = cipher ^ key
  private static ReadOnlySpan<byte> XorLoop => [0xAC, 0x30, 0xD0, 0xAA, 0xE2, 0xF4];
  // lodsb; not al; xor al,dl; stosb; loop  -> plain = (cipher ^ 0xFF) ^ key
  private static ReadOnlySpan<byte> NotLoop => [0xAC, 0xF6, 0xD0, 0x30, 0xD0, 0xAA, 0xE2, 0xF2];
  // mov edx,7; mov eax,10; syscall  (mprotect .., PROT_RWX) — stub prologue
  private static ReadOnlySpan<byte> MprotectPrologue => [0xBA, 0x07, 0x00, 0x00, 0x00, 0xB8, 0x0A, 0x00, 0x00, 0x00, 0x0F, 0x05];

  private enum Cipher { None, Xor, Not, Compound }

  /// <summary>
  /// Gets the id.
  /// </summary>
  public string Id => "m0dern_p4cker";
  /// <summary>
  /// Gets the display name.
  /// </summary>
  public string DisplayName => "m0dern_p4cker ELF64 stub packer";

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
    var bytes = image.ToArray();
    if (ClassifyCipher(bytes) != Cipher.None)
      return new(true, this.Id, 1.0, []);
    return new(false, this.Id, 0,
      [new(ExecutableDiagnosticCode.NotPackedExecutable,
        "No m0dern_p4cker decrypt-loop stub was found.", true)]);
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

    var cipher = ClassifyCipher(image);
    if (cipher == Cipher.None)
      return new(ExecutableUnpackLevel.DetectionOnly, ExecutableUnpackCapabilities.CanDetect, [],
        [new(ExecutableDiagnosticCode.PayloadNotFound, "m0dern_p4cker stub could not be classified.", true)]);

    if (!TryGetTextSection(image, out var textOffset, out var textSize))
      return new(ExecutableUnpackLevel.DetectionOnly, ExecutableUnpackCapabilities.CanDetect, [],
        [new(ExecutableDiagnosticCode.PayloadNotFound, "m0dern_p4cker: .text section could not be located.", true)]);

    var encryptedText = image.AsSpan(textOffset, textSize).ToArray();
    var diagnostics = new List<ExecutableDiagnostic>();
    var artifacts = new List<UnpackArtifact> {
      new("original_packed.bin", image, "stored"),
      new("encrypted_text.bin", encryptedText, cipher == Cipher.Xor ? "xor" : "xor-not"),
    };

    if (cipher == Cipher.Compound) {
      diagnostics.Add(new(ExecutableDiagnosticCode.TransformNotReversible,
        "m0dern_p4cker compound (xorp/rotate) stub detected; its multi-round keyed transform is not statically reversed. Encrypted .text is located only.", true));
      artifacts.Insert(0, new("metadata.ini", BuildMetadata(image.Length, cipher, textOffset, textSize, 0, 0, false), "stored"));
      var locatedCaps = ExecutableUnpackCapabilities.CanDetect | ExecutableUnpackCapabilities.CanLocatePayload |
        ExecutableUnpackCapabilities.SupportsElf | ExecutableUnpackCapabilities.SupportsX64;
      var located = new UnpackResult(ExecutableUnpackLevel.PayloadLocated, locatedCaps, artifacts, diagnostics);
      artifacts.Add(new("diagnostics.json", ExecutableDiagnosticsJson.Build(this.Id, packed.ImageInfo, located), "stored"));
      return located with { Artifacts = artifacts };
    }

    if (!TryExtractKey(image, out var key) || !TryExtractOriginalEntry(image, out var originalEntry)) {
      diagnostics.Add(new(ExecutableDiagnosticCode.PayloadNotFound,
        "m0dern_p4cker key or original entry point could not be read from the stub.", true));
      artifacts.Insert(0, new("metadata.ini", BuildMetadata(image.Length, cipher, textOffset, textSize, 0, 0, false), "stored"));
      var locatedCaps = ExecutableUnpackCapabilities.CanDetect | ExecutableUnpackCapabilities.CanLocatePayload |
        ExecutableUnpackCapabilities.SupportsElf | ExecutableUnpackCapabilities.SupportsX64;
      var located = new UnpackResult(ExecutableUnpackLevel.PayloadLocated, locatedCaps, artifacts, diagnostics);
      artifacts.Add(new("diagnostics.json", ExecutableDiagnosticsJson.Build(this.Id, packed.ImageInfo, located), "stored"));
      return located with { Artifacts = artifacts };
    }

    var decryptedText = Decrypt(encryptedText, key, cipher);
    var reconstructed = image.ToArray();
    decryptedText.CopyTo(reconstructed.AsSpan(textOffset));
    BinaryPrimitives.WriteUInt64LittleEndian(reconstructed.AsSpan(0x18), originalEntry);

    artifacts.Insert(0, new("metadata.ini", BuildMetadata(image.Length, cipher, textOffset, textSize, key, originalEntry, true), "stored"));
    artifacts.Add(new("decrypted_text.bin", decryptedText, "stored"));
    artifacts.Add(new("reconstructed/reconstructed.elf", reconstructed, "stored"));

    var caps = ExecutableUnpackCapabilities.CanDetect | ExecutableUnpackCapabilities.CanLocatePayload |
      ExecutableUnpackCapabilities.CanDecompressPayload | ExecutableUnpackCapabilities.CanRebuildExecutable |
      ExecutableUnpackCapabilities.SupportsElf | ExecutableUnpackCapabilities.SupportsX64;
    var result = new UnpackResult(ExecutableUnpackLevel.RebuiltExecutable, caps, artifacts, diagnostics);
    artifacts.Add(new("diagnostics.json", ExecutableDiagnosticsJson.Build(this.Id, packed.ImageInfo, result), "stored"));
    return result with { Artifacts = artifacts };
  }

  private static Cipher ClassifyCipher(byte[] image) {
    if (image.Length < 0x40 || image[0] != 0x7F || image[1] != 'E' || image[2] != 'L' || image[3] != 'F' || image[4] != 2)
      return Cipher.None;
    if (IndexOf(image, MprotectPrologue) < 0)
      return Cipher.None;
    if (IndexOf(image, NotLoop) >= 0)
      return Cipher.Not;
    if (IndexOf(image, XorLoop) >= 0)
      return Cipher.Xor;
    // Prologue present but neither simple loop matched: the compound stub.
    return Cipher.Compound;
  }

  private static byte[] Decrypt(byte[] cipher, byte key, Cipher mode) {
    var result = new byte[cipher.Length];
    for (var i = 0; i < cipher.Length; i++) {
      var b = cipher[i];
      if (mode == Cipher.Not)
        b = (byte)(b ^ 0xFF);
      result[i] = (byte)(b ^ key);
    }
    return result;
  }

  /// <summary>
  /// Reads the single-byte key from the stub's <c>mov rdx, imm64</c>
  /// (<c>48 BA</c>) that immediately precedes the register-copy feeding the
  /// decrypt loop (<c>48 89 F7</c> non-PIE or <c>48 89 FE</c> PIE). The key is a
  /// 1..255 value stored in the low byte with the upper seven bytes zero.
  /// </summary>
  private static bool TryExtractKey(byte[] image, out byte key) {
    key = 0;
    for (var i = 0; i + 13 <= image.Length; i++) {
      if (image[i] != 0x48 || image[i + 1] != 0xBA)
        continue;
      var candidate = image[i + 2];
      var upperZero = image[i + 3] == 0 && image[i + 4] == 0 && image[i + 5] == 0 && image[i + 6] == 0 &&
        image[i + 7] == 0 && image[i + 8] == 0 && image[i + 9] == 0;
      if (candidate == 0 || !upperZero)
        continue;
      var follow = image[i + 10] == 0x48 && image[i + 11] == 0x89 && (image[i + 12] == 0xF7 || image[i + 12] == 0xFE);
      if (!follow)
        continue;
      key = candidate;
      return true;
    }
    return false;
  }

  /// <summary>
  /// Reads the original entry point from the stub's tail
  /// <c>mov rax, imm64 ; jmp rax</c> (<c>48 B8 &lt;entry&gt; FF E0</c>).
  /// </summary>
  private static bool TryExtractOriginalEntry(byte[] image, out ulong entry) {
    entry = 0;
    for (var i = 0; i + 12 <= image.Length; i++) {
      if (image[i] != 0x48 || image[i + 1] != 0xB8)
        continue;
      if (image[i + 10] != 0xFF || image[i + 11] != 0xE0)
        continue;
      var value = BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan(i + 2));
      if (value == 0)
        continue;
      entry = value;
      return true;
    }
    return false;
  }

  private static bool TryGetTextSection(byte[] image, out int offset, out int size) {
    offset = 0;
    size = 0;
    if (image.Length < 0x40)
      return false;
    var shoff = BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan(0x28));
    var shentsize = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(0x3A));
    var shnum = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(0x3C));
    var strndx = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(0x3E));
    if (shoff == 0 || shentsize < 64 || shnum == 0 || strndx >= shnum)
      return false;
    if (shoff + (ulong)shnum * shentsize > (ulong)image.Length)
      return false;

    var strHeader = (int)shoff + strndx * shentsize;
    var strOff = BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan(strHeader + 24));
    var strSize = BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan(strHeader + 32));
    if (strOff > (ulong)image.Length || strOff + strSize > (ulong)image.Length)
      return false;

    for (var i = 0; i < shnum; i++) {
      var o = (int)shoff + i * shentsize;
      var nameIdx = BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(o));
      if (!NameEquals(image, (int)strOff, (int)strSize, nameIdx, ".text"))
        continue;
      var secOff = BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan(o + 24));
      var secSize = BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan(o + 32));
      if (secSize == 0 || secOff > (ulong)image.Length || secOff + secSize > (ulong)image.Length)
        return false;
      offset = (int)secOff;
      size = (int)secSize;
      return true;
    }
    return false;
  }

  private static bool NameEquals(byte[] image, int strOff, int strSize, uint nameIdx, string name) {
    if (nameIdx >= strSize)
      return false;
    var p = strOff + (int)nameIdx;
    for (var i = 0; i < name.Length; i++) {
      if (p + i >= image.Length || image[p + i] != (byte)name[i])
        return false;
    }
    return p + name.Length < image.Length && image[p + name.Length] == 0;
  }

  private static int IndexOf(byte[] haystack, ReadOnlySpan<byte> needle) {
    for (var i = 0; i + needle.Length <= haystack.Length; i++) {
      var match = true;
      for (var j = 0; j < needle.Length; j++)
        if (haystack[i + j] != needle[j]) { match = false; break; }
      if (match)
        return i;
    }
    return -1;
  }

  private static byte[] BuildMetadata(int imageSize, Cipher cipher, int textOffset, int textSize, byte key, ulong entry, bool rebuilt) {
    var cipherName = cipher switch { Cipher.Xor => "xor", Cipher.Not => "xor-not", _ => "compound" };
    var sb = new StringBuilder();
    sb.AppendLine("[m0dern_p4cker]");
    sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"image_size = {imageSize}\n");
    sb.AppendLine("container = ELF64");
    sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"cipher = {cipherName}\n");
    sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"text_offset = 0x{textOffset:X}\n");
    sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"text_size = {textSize}\n");
    if (rebuilt) {
      sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"key = {key}\n");
      sb.Append(System.Globalization.CultureInfo.InvariantCulture, $"original_entry_point = 0x{entry:X}\n");
      sb.AppendLine("capability_level = RebuiltExecutable");
      sb.AppendLine("note = .text decrypted with the stub-embedded key and the original entry point restored; the code-cave stub is retained as inert data.");
    } else {
      sb.AppendLine("capability_level = PayloadLocated");
      sb.AppendLine("note = Encrypted .text located; recovery of this stub variant is not claimed.");
    }
    return Encoding.UTF8.GetBytes(sb.ToString());
  }
}
