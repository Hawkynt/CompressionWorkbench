using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using Compression.Core.ExecutableUnpacking;
using FileFormat.ExePackers;
using NUnit.Framework;

namespace Compression.Tests.ExePackers;

/// <summary>
/// Round-trips the Yoda's Crypter stub walker against a synthetic image built
/// the way the packer builds one: a plaintext entry-point prologue whose inline
/// cipher loop decrypts the stub body, a decrypted body holding the section
/// walker plus a second cipher loop, and sections encrypted with that second
/// loop. The fixture keeps the polymorphic traits that matter — operations mixed
/// with the loop counter, and junk bytes hidden behind short jumps.
/// </summary>
[TestFixture]
public class YodaCrypterStaticUnpackTests {

  private const uint _IMAGE_BASE = 0x0040_0000;
  private const int _PE_OFFSET = 0x80;
  private const int _OPTIONAL_SIZE = 0xE0;
  private const int _SECTION_TABLE = _PE_OFFSET + 24 + _OPTIONAL_SIZE;

  private const uint _TEXT_RVA = 0x1000, _RDATA_RVA = 0x2000, _STUB_RVA = 0x3000;
  private const int _TEXT_RAW = 0x200, _RDATA_RAW = 0x400, _STUB_RAW = 0x600;
  private const int _SECTION_SIZE = 0x200;
  private const int _BODY_OFFSET = 0x80, _BODY_LENGTH = 0x80;
  private const uint _ORIGINAL_ENTRY_POINT = 0x0000_14C8;

  private static readonly YodaByteOp[] _STUB_CIPHER = [
    new(YodaByteOpKind.SubtractCounter, 0),
    new(YodaByteOpKind.XorImmediate, 0x73),
    new(YodaByteOpKind.RotateRight, 3),
    new(YodaByteOpKind.AddImmediate, 0x11),
  ];

  private static readonly YodaByteOp[] _SECTION_CIPHER = [
    new(YodaByteOpKind.AddCounter, 0),
    new(YodaByteOpKind.XorImmediate, 0x5A),
    new(YodaByteOpKind.RotateLeft, 2),
    new(YodaByteOpKind.Decrement, 0),
  ];

  [Test, Category("HappyPath")]
  public void StaticUnpack_RecoversSectionPlaintextAndEntryPoint() {
    var plaintext = BuildTextSection();
    var image = BuildPackedImage(plaintext, out var untouched);

    Assert.That(YodaCrypterStub.TryUnpack(image, out var stub), Is.True, "stub walk failed");
    Assert.That(stub, Is.Not.Null);

    Assert.That(stub!.OriginalEntryPoint, Is.EqualTo(_ORIGINAL_ENTRY_POINT));
    Assert.That(stub.SectionCipher, Is.EqualTo(_SECTION_CIPHER));
    Assert.That(stub.StubCipher, Is.EqualTo(_STUB_CIPHER));
    Assert.That(stub.DecryptedSections, Does.Contain(".text"));
    Assert.That(stub.SkippedSections, Does.Contain(".rdata"));
    Assert.That(stub.SkippedSections, Does.Contain("yC"));

    var recovered = stub.DecryptedImage.AsSpan(_TEXT_RAW, _SECTION_SIZE).ToArray();
    Assert.That(recovered, Is.EqualTo(plaintext), "encrypted section did not decrypt back");

    var skipped = stub.DecryptedImage.AsSpan(_RDATA_RAW, _SECTION_SIZE).ToArray();
    Assert.That(skipped, Is.EqualTo(untouched), "a skipped section was decrypted anyway");

    var restored = BinaryPrimitives.ReadUInt32LittleEndian(stub.DecryptedImage.AsSpan(_PE_OFFSET + 24 + 16));
    Assert.That(restored, Is.EqualTo(_ORIGINAL_ENTRY_POINT), "entry point was not written back");
  }

  [Test, Category("HappyPath")]
  public void Handler_ClaimsPayloadDecompressedAndEmitsDecryptedSection() {
    var plaintext = BuildTextSection();
    var image = BuildPackedImage(plaintext, out _);

    var handler = new YodaCrypterExecutablePackerHandler();
    var detection = handler.Detect(image);
    Assert.That(detection.IsMatch, Is.True);

    var result = handler.Unpack(handler.Parse(image, detection), new UnpackOptions());

    Assert.That(result.Level, Is.EqualTo(ExecutableUnpackLevel.PayloadDecompressed));
    Assert.That(result.Capabilities.HasFlag(ExecutableUnpackCapabilities.CanDecompressPayload), Is.True);

    var section = result.Artifacts.SingleOrDefault(a => a.Name == "decrypted_sections/.text.bin");
    Assert.That(section, Is.Not.Null, "no decrypted .text artifact");
    Assert.That(section!.Data, Is.EqualTo(plaintext));
  }

  [Test, Category("EdgeCase")]
  public void StaticUnpack_RefusesAnImageWhoseStubDoesNotMatch() {
    var image = BuildPackedImage(BuildTextSection(), out _);

    // Blank the entry-point prologue: without it there is no decryption range.
    image.AsSpan(_STUB_RAW, 0x40).Clear();

    Assert.That(YodaCrypterStub.TryUnpack(image, out var stub), Is.False);
    Assert.That(stub, Is.Null);
  }

  private static byte[] BuildTextSection() {
    var buffer = new byte[_SECTION_SIZE];
    for (var i = 0; i < buffer.Length; ++i)
      buffer[i] = (byte)(i * 7 + 3);
    return buffer;
  }

  /// <summary>Builds a yC-packed image and reports the bytes of the section the stub must leave alone.</summary>
  private static byte[] BuildPackedImage(byte[] textPlaintext, out byte[] rdataUntouched) {
    var image = new byte[_STUB_RAW + _SECTION_SIZE];
    image[0] = (byte)'M';
    image[1] = (byte)'Z';
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(0x3C), _PE_OFFSET);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(_PE_OFFSET), 0x0000_4550);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(_PE_OFFSET + 4), 0x014C);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(_PE_OFFSET + 6), 3);
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(_PE_OFFSET + 20), _OPTIONAL_SIZE);

    var optional = _PE_OFFSET + 24;
    BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(optional), 0x010B);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(optional + 16), _STUB_RVA);   // entry point -> stub
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(optional + 28), _IMAGE_BASE);

    WriteSection(image, 0, ".text", _TEXT_RVA, _TEXT_RAW);
    WriteSection(image, 1, ".rdata", _RDATA_RVA, _RDATA_RAW);
    WriteSection(image, 2, "yC", _STUB_RVA, _STUB_RAW);

    // .rdata is on the stub's skip list, so it stays plaintext.
    rdataUntouched = new byte[_SECTION_SIZE];
    for (var i = 0; i < rdataUntouched.Length; ++i)
      rdataUntouched[i] = (byte)(0xA0 + (i & 0x0F));
    rdataUntouched.CopyTo(image.AsSpan(_RDATA_RAW));

    EncryptWith(textPlaintext, _SECTION_CIPHER).CopyTo(image.AsSpan(_TEXT_RAW));
    BuildStub().CopyTo(image.AsSpan(_STUB_RAW));
    return image;
  }

  private static void WriteSection(byte[] image, int index, string name, uint rva, int rawOffset) {
    var at = _SECTION_TABLE + 40 * index;
    var bytes = System.Text.Encoding.ASCII.GetBytes(name);
    bytes.CopyTo(image.AsSpan(at, 8));
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(at + 8), _SECTION_SIZE);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(at + 12), rva);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(at + 16), _SECTION_SIZE);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(at + 20), (uint)rawOffset);
    BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(at + 36), 0xE000_0020);
  }

  /// <summary>Assembles the two-layer stub: plaintext prologue, encrypted body.</summary>
  private static byte[] BuildStub() {
    var stubVa = _IMAGE_BASE + _STUB_RVA;
    // The prologue reconstructs `delta` from its own address, exactly as the
    // packer's does; picking K fixes delta and hence the template constants.
    var k = _IMAGE_BASE + 5;
    var delta = stubVa + 5 - k;                       // == _STUB_RVA
    var bodyTemplate = stubVa + _BODY_OFFSET - delta; // `B`, the template address
    var lengthTemplate = bodyTemplate + _BODY_LENGTH; // `A`, so A - B == body length

    var stub = new byte[_SECTION_SIZE];
    var w = new List<byte> {
      0xE8, 0x00, 0x00, 0x00, 0x00,                   // call $+5
      0x5D,                                           // pop ebp
      0x81, 0xED,                                     // sub ebp, k
    };
    w.AddRange(BitConverter.GetBytes(k));
    w.Add(0xB9); w.AddRange(BitConverter.GetBytes(lengthTemplate));   // mov ecx, A
    w.AddRange([0x81, 0xE9]); w.AddRange(BitConverter.GetBytes(bodyTemplate)); // sub ecx, B
    w.AddRange([0x8B, 0xD5]);                                        // mov edx, ebp
    w.AddRange([0x81, 0xC2]); w.AddRange(BitConverter.GetBytes(bodyTemplate)); // add edx, B
    w.AddRange([0x8D, 0x3A, 0x8B, 0xF7, 0x33, 0xC0]);                // lea edi,[edx]; mov esi,edi; xor eax,eax
    w.Add(0xAC);                                                     // lodsb
    w.AddRange(EncodeCipher(_STUB_CIPHER, withJunk: false));
    w.AddRange([0xAA, 0xE2, 0xF0]);                                  // stosb; loop
    Assert.That(w.Count, Is.LessThanOrEqualTo(_BODY_OFFSET), "prologue overran the body");
    w.CopyTo(stub);

    var body = BuildStubBody(stubVa, delta);
    EncryptWith(body, _STUB_CIPHER).CopyTo(stub.AsSpan(_BODY_OFFSET));
    return stub;
  }

  /// <summary>The decrypted stub body: walker anchor, skip table, entry-point slot and the section cipher.</summary>
  private static byte[] BuildStubBody(uint stubVa, uint delta) {
    const int cipherAt = 0x40, entryPointAt = 0x60;
    var body = new byte[_BODY_LENGTH];
    var bodyVa = stubVa + _BODY_OFFSET;

    // `mov esi, ds:[esi+0xc]; add esi, eax; call <section cipher>`
    new byte[] { 0x3E, 0x8B, 0x76, 0x0C, 0x03, 0xF0, 0xE8 }.CopyTo(body.AsSpan(0));
    BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(7), cipherAt - 11);

    // the walker's own skip table: `cmp dword ptr ds:[esi], '<name>'`
    var skip = 0x0B;
    foreach (var name in new[] { ".rda"u8.ToArray(), "yC\0\0"u8.ToArray() }) {
      new byte[] { 0x3E, 0x81, 0x3E }.CopyTo(body.AsSpan(skip));
      name.CopyTo(body.AsSpan(skip + 3));
      skip += 7;
    }

    // `mov edx,ebp; add edx,<base slot>; mov ebx,[edx]; mov edx,ebp; add edx,<entry slot>; add ebx,[edx]; ror ebx,7`
    var oep = 0x19;
    body[oep] = 0x8B; body[oep + 1] = 0xD5; body[oep + 2] = 0x81; body[oep + 3] = 0xC2;
    BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(oep + 4), _IMAGE_BASE);
    body[oep + 8] = 0x8B; body[oep + 9] = 0x1A;
    body[oep + 10] = 0x8B; body[oep + 11] = 0xD5; body[oep + 12] = 0x81; body[oep + 13] = 0xC2;
    BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(oep + 14), bodyVa + entryPointAt - delta);
    body[oep + 18] = 0x03; body[oep + 19] = 0x1A;
    body[oep + 20] = 0xC1; body[oep + 21] = 0xCB; body[oep + 22] = 0x07;

    var cipher = new List<byte> { 0xAC };
    cipher.AddRange(EncodeCipher(_SECTION_CIPHER, withJunk: true));
    cipher.AddRange([0xAA, 0xE2, 0xF0]);
    cipher.CopyTo(body, cipherAt);

    BinaryPrimitives.WriteUInt32LittleEndian(body.AsSpan(entryPointAt), _ORIGINAL_ENTRY_POINT);
    return body;
  }

  private static IEnumerable<byte> EncodeCipher(IReadOnlyList<YodaByteOp> cipher, bool withJunk) {
    var bytes = new List<byte>();
    for (var i = 0; i < cipher.Count; ++i) {
      // Hide a junk byte behind a short jump, the way the polymorphic engine does.
      if (withJunk && i == 1)
        bytes.AddRange([0xEB, 0x01, 0xCC]);
      bytes.AddRange(cipher[i].Kind switch {
        YodaByteOpKind.AddCounter => [0x02, 0xC1],
        YodaByteOpKind.SubtractCounter => [0x2A, 0xC1],
        YodaByteOpKind.XorCounter => [0x32, 0xC1],
        YodaByteOpKind.AddImmediate => [0x04, cipher[i].Operand],
        YodaByteOpKind.SubtractImmediate => [0x2C, cipher[i].Operand],
        YodaByteOpKind.XorImmediate => [0x34, cipher[i].Operand],
        YodaByteOpKind.RotateLeft => [0xC0, 0xC0, cipher[i].Operand],
        YodaByteOpKind.RotateRight => [0xC0, 0xC8, cipher[i].Operand],
        YodaByteOpKind.Increment => [0xFE, 0xC0],
        YodaByteOpKind.Decrement => [0xFE, 0xC8],
        YodaByteOpKind.Not => [0xF6, 0xD0],
        YodaByteOpKind.Negate => new byte[] { 0xF6, 0xD8 },
        _ => throw new ArgumentOutOfRangeException(nameof(cipher)),
      });
    }
    return bytes;
  }

  /// <summary>Applies the inverse of <paramref name="cipher"/>, so the unpacker's forward pass restores the input.</summary>
  private static byte[] EncryptWith(byte[] plaintext, IReadOnlyList<YodaByteOp> cipher) {
    var encrypted = new byte[plaintext.Length];
    for (var i = 0; i < plaintext.Length; ++i) {
      var counter = (byte)((plaintext.Length - i) & 0xFF);
      var value = plaintext[i];
      for (var op = cipher.Count - 1; op >= 0; --op)
        value = Invert(value, counter, cipher[op]);
      encrypted[i] = value;
    }
    return encrypted;
  }

  private static byte Invert(byte value, byte counter, YodaByteOp op) => op.Kind switch {
    YodaByteOpKind.AddImmediate => (byte)(value - op.Operand),
    YodaByteOpKind.SubtractImmediate => (byte)(value + op.Operand),
    YodaByteOpKind.XorImmediate => (byte)(value ^ op.Operand),
    YodaByteOpKind.AddCounter => (byte)(value - counter),
    YodaByteOpKind.SubtractCounter => (byte)(value + counter),
    YodaByteOpKind.XorCounter => (byte)(value ^ counter),
    YodaByteOpKind.RotateLeft => Ror(value, op.Operand),
    YodaByteOpKind.RotateRight => Rol(value, op.Operand),
    YodaByteOpKind.Increment => (byte)(value - 1),
    YodaByteOpKind.Decrement => (byte)(value + 1),
    YodaByteOpKind.Not => (byte)~value,
    YodaByteOpKind.Negate => (byte)-value,
    _ => value,
  };

  private static byte Rol(byte value, int count) {
    count &= 7;
    return (byte)((value << count) | (value >> (8 - count)));
  }

  private static byte Ror(byte value, int count) {
    count &= 7;
    return (byte)((value >> count) | (value << (8 - count)));
  }
}
