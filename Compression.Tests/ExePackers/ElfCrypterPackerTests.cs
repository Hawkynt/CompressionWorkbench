using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Compression.Core.ExecutableUnpacking;
using Compression.Lib;
using FileFormat.ExePackers;

namespace Compression.Tests.ExePackers;

/// <summary>
/// Unpacking tests for the four open-source ELF crypters/packers added for the
/// packing-box campaign (Ezuri, Ward, m0dern_p4cker, MidgetPack). Each synthetic
/// sample mirrors the exact on-disk layout produced by the real tool, verified
/// out-of-band by packing a real ELF with the actual tool in WSL and confirming
/// byte-exact recovery through the same handler code.
/// </summary>
[TestFixture]
public class ElfCrypterPackerTests {
  private const string EzuriAlphabet =
    "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ@#$%0123456789";

  [Test, Category("HappyPath")]
  public void Registry_ContainsElfCrypterHandlers() {
    var ids = ExecutablePackerHandlers.All.Select(h => h.Id).ToArray();
    Assert.That(ids, Is.SupersetOf(new[] { "ezuri", "ward", "m0dern_p4cker", "midgetpack" }));
  }

  [Test, Category("HappyPath")]
  public void EzuriHandler_DecryptsAppendedPayloadToOriginalElf() {
    var original = MinimalElf64("ezuri original payload"u8.ToArray());
    var packed = BuildEzuri(original, out var key, out var iv);

    var handler = new EzuriExecutablePackerHandler();
    var result = Unpack(handler, packed);

    Assert.Multiple(() => {
      Assert.That(result.Level, Is.EqualTo(ExecutableUnpackLevel.RebuiltExecutable));
      Assert.That(result.Artifacts.Single(a => a.Name == "key.bin").Data, Is.EqualTo(key).AsCollection);
      Assert.That(result.Artifacts.Single(a => a.Name == "iv.bin").Data, Is.EqualTo(iv).AsCollection);
      Assert.That(result.Artifacts.Single(a => a.Name == "reconstructed/original_executable.bin").Data,
        Is.EqualTo(original).AsCollection);
    });
  }

  [Test, Category("HappyPath")]
  public void Registry_DetectBest_UnpacksEzuri() {
    var original = MinimalElf64("registry ezuri"u8.ToArray());
    var packed = BuildEzuri(original, out _, out _);

    var match = ExecutablePackerHandlers.DetectBest(packed);
    Assert.That(match, Is.Not.Null);
    Assert.That(match!.Handler.Id, Is.EqualTo("ezuri"));

    var result = ExecutablePackerHandlers.TryUnpack(packed);
    Assert.That(result!.Artifacts.Single(a => a.Name == "reconstructed/original_executable.bin").Data,
      Is.EqualTo(original).AsCollection);
  }

  [Test, Category("HappyPath")]
  public void WardHandler_RecoversAppendedElfFromPtNote() {
    var original = MinimalElf64("ward inner executable"u8.ToArray());
    var packed = BuildWard(original);

    var handler = new WardExecutablePackerHandler();
    var result = Unpack(handler, packed);

    Assert.Multiple(() => {
      Assert.That(result.Level, Is.EqualTo(ExecutableUnpackLevel.RebuiltExecutable));
      Assert.That(result.Artifacts.Single(a => a.Name == "reconstructed/original_executable.bin").Data,
        Is.EqualTo(original).AsCollection);
    });
  }

  [Test, Category("HappyPath")]
  public void Registry_DetectBest_UnpacksWard() {
    var original = MinimalElf64("registry ward"u8.ToArray());
    var packed = BuildWard(original);

    var match = ExecutablePackerHandlers.DetectBest(packed);
    Assert.That(match, Is.Not.Null);
    Assert.That(match!.Handler.Id, Is.EqualTo("ward"));
  }

  [Test, Category("HappyPath")]
  public void M0dernP4ckerHandler_DecryptsXorTextAndRestoresEntry(
      [Values(false, true)] bool notMode) {
    var originalText = "m0dern text section bytes 0123456789"u8.ToArray();
    const byte key = 0x52;
    const ulong originalEntry = 0x401050;
    var packed = BuildM0dernP4cker(originalText, key, originalEntry, notMode, out var textOffset);

    var handler = new M0dernP4ckerExecutablePackerHandler();
    var result = Unpack(handler, packed);

    Assert.Multiple(() => {
      Assert.That(result.Level, Is.EqualTo(ExecutableUnpackLevel.RebuiltExecutable));
      Assert.That(result.Artifacts.Single(a => a.Name == "decrypted_text.bin").Data,
        Is.EqualTo(originalText).AsCollection);
      var rebuilt = result.Artifacts.Single(a => a.Name == "reconstructed/reconstructed.elf").Data;
      Assert.That(rebuilt.AsSpan(textOffset, originalText.Length).ToArray(),
        Is.EqualTo(originalText).AsCollection);
      Assert.That(BinaryPrimitives.ReadUInt64LittleEndian(rebuilt.AsSpan(0x18)), Is.EqualTo(originalEntry));
    });
  }

  [Test, Category("HappyPath")]
  public void Registry_DetectBest_UnpacksM0dernP4cker() {
    var packed = BuildM0dernP4cker("registry m0dern text!"u8.ToArray(), 0x37, 0x401234, notMode: false, out _);

    var match = ExecutablePackerHandlers.DetectBest(packed);
    Assert.That(match, Is.Not.Null);
    Assert.That(match!.Handler.Id, Is.EqualTo("m0dern_p4cker"));
  }

  [Test, Category("HappyPath")]
  public void MidgetPackHandler_LocatesEncryptedPayloadWithoutKey() {
    var payload = new byte[256];
    new Random(0x1234).NextBytes(payload);
    var packed = BuildMidgetPack(payload, packType: 1);

    var handler = new MidgetPackExecutablePackerHandler();
    var result = Unpack(handler, packed);

    Assert.Multiple(() => {
      Assert.That(result.Level, Is.EqualTo(ExecutableUnpackLevel.PayloadLocated));
      Assert.That(result.Artifacts.Single(a => a.Name == "encrypted_payload.bin").Data,
        Is.EqualTo(payload).AsCollection);
      Assert.That(result.Diagnostics.Any(d => d.Code == ExecutableDiagnosticCode.TransformNotReversible), Is.True);
      Assert.That(result.Capabilities.HasFlag(ExecutableUnpackCapabilities.CanRebuildExecutable), Is.False);
    });
  }

  [Test, Category("HappyPath")]
  public void Registry_DetectBest_LocatesMidgetPack() {
    var payload = new byte[128];
    new Random(0x99).NextBytes(payload);
    var packed = BuildMidgetPack(payload, packType: 2);

    var match = ExecutablePackerHandlers.DetectBest(packed);
    Assert.That(match, Is.Not.Null);
    Assert.That(match!.Handler.Id, Is.EqualTo("midgetpack"));
  }

  [Test, Category("EdgeCase")]
  public void ElfCrypterHandlers_RejectPlainElf() {
    var plain = MinimalElf64("not packed"u8.ToArray());
    Assert.Multiple(() => {
      Assert.That(new EzuriExecutablePackerHandler().Detect(plain).IsMatch, Is.False);
      Assert.That(new WardExecutablePackerHandler().Detect(plain).IsMatch, Is.False);
      Assert.That(new M0dernP4ckerExecutablePackerHandler().Detect(plain).IsMatch, Is.False);
      Assert.That(new MidgetPackExecutablePackerHandler().Detect(plain).IsMatch, Is.False);
    });
  }

  private static UnpackResult Unpack(IExecutablePackerHandler handler, byte[] image) {
    var detection = handler.Detect(image);
    Assert.That(detection.IsMatch, Is.True, handler.Id);
    return handler.Unpack(handler.Parse(image, detection), new());
  }

  /// <summary>Minimal valid ELF64 with one PROGBITS section covering the body.</summary>
  private static byte[] MinimalElf64(byte[] body) {
    // 64-byte header + body + section-header table with .text + .shstrtab.
    const int headerSize = 64;
    var strtab = "\0.text\0.shstrtab\0"u8.ToArray();
    var bodyOffset = headerSize;
    var strtabOffset = bodyOffset + body.Length;
    var shoff = strtabOffset + strtab.Length;
    var image = new byte[shoff + 3 * 64];

    image[0] = 0x7F; image[1] = (byte)'E'; image[2] = (byte)'L'; image[3] = (byte)'F';
    image[4] = 2; image[5] = 1; image[6] = 1; // ELF64 LE v1
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x10), 2);     // ET_EXEC
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x12), 0x3E);  // x86-64
    BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(0x18), 0x401000); // e_entry
    BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(0x28), (ulong)shoff);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x34), 64);    // ehsize
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x3A), 64);    // shentsize
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x3C), 3);     // shnum
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x3E), 2);     // shstrndx

    body.CopyTo(image.AsSpan(bodyOffset));
    strtab.CopyTo(image.AsSpan(strtabOffset));

    WriteSection(image, shoff + 64, 1, 1, 0x401000, bodyOffset, body.Length);       // .text PROGBITS
    WriteSection(image, shoff + 128, 7, 3, 0, strtabOffset, strtab.Length);         // .shstrtab STRTAB
    return image;
  }

  private static void WriteSection(byte[] image, int offset, uint nameIdx, uint type, ulong addr, int fileOffset, int size) {
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(offset), nameIdx);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(offset + 4), type);
    BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(offset + 16), addr);
    BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(offset + 24), (ulong)fileOffset);
    BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(offset + 32), (ulong)size);
  }

  private static byte[] BuildEzuri(byte[] original, out byte[] key, out byte[] iv) {
    var rng = new Random(0xE2);
    key = new byte[32];
    iv = new byte[16];
    for (var i = 0; i < key.Length; i++) key[i] = (byte)EzuriAlphabet[rng.Next(EzuriAlphabet.Length)];
    for (var i = 0; i < iv.Length; i++) iv[i] = (byte)EzuriAlphabet[rng.Next(EzuriAlphabet.Length)];

    // A stub ELF64 whose last loaded section ends exactly at its file length
    // (section-header table near the front, section data to EOF — as Go builds).
    var stub = BuildEzuriStub(512);
    var ciphertext = EncryptCfb(original, key, iv);

    var result = new byte[stub.Length + key.Length + iv.Length + ciphertext.Length];
    stub.CopyTo(result.AsSpan());
    key.CopyTo(result.AsSpan(stub.Length));
    iv.CopyTo(result.AsSpan(stub.Length + key.Length));
    ciphertext.CopyTo(result.AsSpan(stub.Length + key.Length + iv.Length));
    return result;
  }

  /// <summary>
  /// A stub ELF64 whose section-header table sits right after the header and
  /// whose <c>.text</c> section data runs to the exact end of the file, so
  /// max(sh_offset + sh_size) equals the stub length — the invariant Ezuri's
  /// static unpacker relies on to find the appended key/IV.
  /// </summary>
  private static byte[] BuildEzuriStub(int stubLen) {
    const int headerSize = 64;
    const int shoff = headerSize;
    const int shnum = 3;
    var shtEnd = shoff + shnum * 64;
    var strtab = "\0.text\0.shstrtab\0"u8.ToArray();
    var strtabOffset = shtEnd;
    var textOffset = strtabOffset + strtab.Length;
    var textSize = stubLen - textOffset;
    if (textSize <= 0)
      throw new ArgumentOutOfRangeException(nameof(stubLen));

    var image = new byte[stubLen];
    image[0] = 0x7F; image[1] = (byte)'E'; image[2] = (byte)'L'; image[3] = (byte)'F';
    image[4] = 2; image[5] = 1; image[6] = 1;
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x10), 2);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x12), 0x3E);
    BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(0x18), 0x401000);
    BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(0x28), shoff);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x34), 64);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x3A), 64);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x3C), shnum);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x3E), 2);
    strtab.CopyTo(image.AsSpan(strtabOffset));
    WriteSection(image, shoff + 64, 1, 1, 0x401000, textOffset, textSize);   // .text to EOF
    WriteSection(image, shoff + 128, 7, 3, 0, strtabOffset, strtab.Length);  // .shstrtab
    return image;
  }

  private static byte[] BuildWard(byte[] original) {
    // Stub ELF64 with a PT_NOTE program header we repoint at the appended ELF.
    const int phoff = 64;
    const int phentsize = 56;
    const int phnum = 1;
    var stubBodyOffset = phoff + phentsize * phnum;
    var stubLen = stubBodyOffset + 32; // small stub body
    var stub = new byte[stubLen];
    stub[0] = 0x7F; stub[1] = (byte)'E'; stub[2] = (byte)'L'; stub[3] = (byte)'F';
    stub[4] = 2; stub[5] = 1; stub[6] = 1;
    BinaryPrimitives.WriteUInt16LittleEndian(stub.AsSpan(0x10), 2);
    BinaryPrimitives.WriteUInt16LittleEndian(stub.AsSpan(0x12), 0x3E);
    BinaryPrimitives.WriteUInt64LittleEndian(stub.AsSpan(0x20), phoff);
    BinaryPrimitives.WriteUInt16LittleEndian(stub.AsSpan(0x36), phentsize);
    BinaryPrimitives.WriteUInt16LittleEndian(stub.AsSpan(0x38), phnum);

    var result = new byte[stubLen + original.Length];
    stub.CopyTo(result.AsSpan());
    original.CopyTo(result.AsSpan(stubLen));

    // PT_NOTE phdr repointed at the appended payload (Ward's injector behaviour).
    var ph = phoff;
    BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(ph), 4);           // PT_NOTE
    BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(ph + 8), (ulong)stubLen);       // p_offset
    BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(ph + 32), (ulong)original.Length); // p_filesz
    return result;
  }

  private static byte[] BuildM0dernP4cker(byte[] originalText, byte key, ulong originalEntry, bool notMode, out int textOffset) {
    // ELF64 with a .text section holding the encrypted bytes plus a code-cave
    // stub carrying the mprotect prologue, key mov, decrypt loop and entry jump.
    textOffset = 0x100;
    var stub = BuildM0dernStub(key, originalEntry, notMode);
    var stubOffset = textOffset + ((originalText.Length + 15) & ~15);
    var strtab = "\0.text\0.shstrtab\0"u8.ToArray();
    var strtabOffset = stubOffset + stub.Length;
    var shoff = (strtabOffset + strtab.Length + 15) & ~15;
    var image = new byte[shoff + 3 * 64];

    image[0] = 0x7F; image[1] = (byte)'E'; image[2] = (byte)'L'; image[3] = (byte)'F';
    image[4] = 2; image[5] = 1; image[6] = 1;
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x10), 2);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x12), 0x3E);
    BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(0x18), 0x401161); // packed entry (into cave)
    BinaryPrimitives.WriteUInt64LittleEndian(image.AsSpan(0x28), (ulong)shoff);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x34), 64);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x3A), 64);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x3C), 3);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(0x3E), 2);

    // Encrypt .text the way the stub decrypts it.
    var enc = new byte[originalText.Length];
    for (var i = 0; i < enc.Length; i++) {
      var b = (byte)(originalText[i] ^ key);
      if (notMode) b = (byte)(b ^ 0xFF);
      enc[i] = b;
    }
    enc.CopyTo(image.AsSpan(textOffset));
    stub.CopyTo(image.AsSpan(stubOffset));
    strtab.CopyTo(image.AsSpan(strtabOffset));

    WriteSection(image, shoff + 64, 1, 1, 0x401000, textOffset, originalText.Length);
    WriteSection(image, shoff + 128, 7, 3, 0, strtabOffset, strtab.Length);
    return image;
  }

  private static byte[] BuildM0dernStub(byte key, ulong originalEntry, bool notMode) {
    var s = new List<byte>();
    // mov edx,7 ; mov eax,10 ; syscall  (mprotect prologue)
    s.AddRange(new byte[] { 0xBA, 0x07, 0x00, 0x00, 0x00, 0xB8, 0x0A, 0x00, 0x00, 0x00, 0x0F, 0x05 });
    // mov rdx, key ; mov rdi, rsi
    s.AddRange(new byte[] { 0x48, 0xBA, key, 0, 0, 0, 0, 0, 0, 0, 0x48, 0x89, 0xF7 });
    // decrypt loop
    if (notMode)
      s.AddRange(new byte[] { 0xAC, 0xF6, 0xD0, 0x30, 0xD0, 0xAA, 0xE2, 0xF2 });
    else
      s.AddRange(new byte[] { 0xAC, 0x30, 0xD0, 0xAA, 0xE2, 0xF4 });
    // mov rax, originalEntry ; jmp rax
    s.Add(0x48); s.Add(0xB8);
    var entry = new byte[8];
    BinaryPrimitives.WriteUInt64LittleEndian(entry, originalEntry);
    s.AddRange(entry);
    s.AddRange(new byte[] { 0xFF, 0xE0 });
    return s.ToArray();
  }

  /// <summary>
  /// Builds a sample shaped like a real MidgetPack output: an ELF64 stub whose
  /// program header table gains an RWX <c>PT_LOAD</c> covering the appended
  /// payload, with the payload's load address and length also written into the
  /// stub's data area the way the run-time stub keeps them.
  /// </summary>
  private static byte[] BuildMidgetPack(byte[] encryptedPayload, uint packType) {
    const int phoff = 0x40;
    const int phentsize = 56;
    const int phnum = 2;
    const int descOffset = 0x100;
    const int stubLen = 0x200;
    const ulong payloadAddress = 0xDA81380;

    var result = new byte[stubLen + encryptedPayload.Length];
    result[0] = 0x7F; result[1] = (byte)'E'; result[2] = (byte)'L'; result[3] = (byte)'F';
    result[4] = 2; result[5] = 1; result[6] = 1;
    BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(0x10), 2);     // ET_EXEC
    BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(0x12), 0x3E);  // EM_X86_64
    BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(0x20), phoff);
    BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(0x36), phentsize);
    BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(0x38), phnum);

    // The stub's own read/execute segment.
    var stubSeg = result.AsSpan(phoff);
    BinaryPrimitives.WriteUInt32LittleEndian(stubSeg, 1);            // PT_LOAD
    BinaryPrimitives.WriteUInt32LittleEndian(stubSeg[4..], 5);       // PF_R|PF_X
    BinaryPrimitives.WriteUInt64LittleEndian(stubSeg[8..], 0);
    BinaryPrimitives.WriteUInt64LittleEndian(stubSeg[16..], 0x400000);
    BinaryPrimitives.WriteUInt64LittleEndian(stubSeg[32..], stubLen);

    // The appended payload segment: RWX and reaching exactly end-of-file.
    var paySeg = result.AsSpan(phoff + phentsize);
    BinaryPrimitives.WriteUInt32LittleEndian(paySeg, 1);             // PT_LOAD
    BinaryPrimitives.WriteUInt32LittleEndian(paySeg[4..], 7);        // PF_R|PF_W|PF_X
    BinaryPrimitives.WriteUInt64LittleEndian(paySeg[8..], stubLen);
    BinaryPrimitives.WriteUInt64LittleEndian(paySeg[16..], payloadAddress);
    BinaryPrimitives.WriteUInt64LittleEndian(paySeg[32..], (ulong)encryptedPayload.Length);

    // The stub's copy of the payload address and length, followed by the pack type.
    BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(descOffset), payloadAddress);
    BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(descOffset + 8), (uint)encryptedPayload.Length);
    BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(descOffset + 12 + 0x10), packType);

    encryptedPayload.CopyTo(result.AsSpan(stubLen));
    return result;
  }

  /// <summary>AES-256 CFB (128-bit feedback) encryption, matching Go's cipher.NewCFBEncrypter.</summary>
  private static byte[] EncryptCfb(byte[] plaintext, byte[] key, byte[] iv) {
    var result = new byte[plaintext.Length];
    using var aes = Aes.Create();
    aes.Key = key;
    aes.Mode = CipherMode.ECB;
    aes.Padding = PaddingMode.None;
    var feedback = (byte[])iv.Clone();
    for (var pos = 0; pos < plaintext.Length; pos += 16) {
      var keystream = aes.EncryptEcb(feedback, PaddingMode.None);
      var blockLen = Math.Min(16, plaintext.Length - pos);
      for (var i = 0; i < blockLen; i++)
        result[pos + i] = (byte)(plaintext[pos + i] ^ keystream[i]);
      if (blockLen == 16)
        Array.Copy(result, pos, feedback, 0, 16);
    }
    return result;
  }
}
