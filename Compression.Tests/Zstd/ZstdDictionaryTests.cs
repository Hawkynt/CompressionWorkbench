using System.Buffers.Binary;
using Compression.Core.Dictionary.Zstd;

namespace Compression.Tests.Zstd;

[TestFixture]
public class ZstdDictionaryTests {
  private static byte[] BuildDictionary(uint dictId, byte[] content) {
    byte[] data = new byte[8 + content.Length];
    BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0), ZstdDictionary.DictionaryMagic);
    BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), dictId);
    content.CopyTo(data, 8);
    return data;
  }

  [Test]
  public void Parse_ValidDictionary_ExtractsDictionaryId() {
    byte[] content = new byte[32];
    new Random(123).NextBytes(content);
    byte[] raw = BuildDictionary(42, content);

    var dict = ZstdDictionary.Parse(raw);

    Assert.That(dict.DictionaryId, Is.EqualTo(42u));
  }

  [Test]
  public void Parse_DictionaryId_IsCorrect() {
    uint expectedId = 0xDEADBEEF;
    byte[] content = new byte[16];
    byte[] raw = BuildDictionary(expectedId, content);

    var dict = ZstdDictionary.Parse(raw);

    Assert.That(dict.DictionaryId, Is.EqualTo(expectedId));
  }

  [Test]
  public void Parse_Content_MatchesPayload() {
    byte[] content = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16];
    byte[] raw = BuildDictionary(1, content);

    var dict = ZstdDictionary.Parse(raw);

    Assert.That(dict.Content, Is.EqualTo(content));
  }

  [Test]
  public void Parse_InvalidMagic_Throws() {
    byte[] data = new byte[16];
    BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0), 0x12345678);

    var ex = Assert.Throws<InvalidDataException>(() => ZstdDictionary.Parse(data));
    Assert.That(ex!.Message, Does.Contain("Invalid Zstd dictionary magic"));
  }

  [Test]
  public void Parse_TooShort_Throws() {
    byte[] data = [0x37, 0xA4, 0x30, 0xEC, 0x01]; // 5 bytes, magic ok but too short

    Assert.Throws<InvalidDataException>(() => ZstdDictionary.Parse(data));
  }

  [Test]
  public void Parse_ExactlyMinSize_Succeeds() {
    byte[] data = new byte[8];
    BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0), ZstdDictionary.DictionaryMagic);
    BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), 99u);

    var dict = ZstdDictionary.Parse(data);

    Assert.That(dict.DictionaryId, Is.EqualTo(99u));
    Assert.That(dict.Content, Is.Empty);
  }

  [Test]
  public void CreateRaw_StoresContent() {
    byte[] content = [10, 20, 30, 40, 50];

    var dict = ZstdDictionary.CreateRaw(7, content);

    Assert.That(dict.DictionaryId, Is.EqualTo(7u));
    Assert.That(dict.Content, Is.EqualTo(content));
  }

  [Test]
  public void CreateRaw_ShortContent_DefaultRepeatOffsets() {
    byte[] content = [1, 2, 3]; // too short for offset derivation

    var dict = ZstdDictionary.CreateRaw(1, content);

    Assert.That(dict.RepeatOffsets, Is.EqualTo(new[] { 1, 4, 8 }));
  }

  [Test]
  public void CreateRaw_NullContent_Throws() {
    Assert.Throws<ArgumentNullException>(() => ZstdDictionary.CreateRaw(1, null!));
  }

  [Test]
  public void Parse_RepeatOffsets_DerivedFromContent() {
    // Build content where last 12 bytes encode specific offsets as little-endian uint32
    byte[] content = new byte[16];
    // offset3 at bytes [4..7], offset2 at [8..11], offset1 at [12..15]
    BinaryPrimitives.WriteUInt32LittleEndian(content.AsSpan(4), 100u);  // offset 3
    BinaryPrimitives.WriteUInt32LittleEndian(content.AsSpan(8), 200u);  // offset 2
    BinaryPrimitives.WriteUInt32LittleEndian(content.AsSpan(12), 300u); // offset 1

    byte[] raw = BuildDictionary(1, content);
    var dict = ZstdDictionary.Parse(raw);

    Assert.That(dict.RepeatOffsets[0], Is.EqualTo(300));
    Assert.That(dict.RepeatOffsets[1], Is.EqualTo(200));
    Assert.That(dict.RepeatOffsets[2], Is.EqualTo(100));
  }

  [Test]
  public void Parse_RepeatOffsets_ContentExactly8Bytes_ThirdOffsetDefaults() {
    byte[] content = new byte[8];
    BinaryPrimitives.WriteUInt32LittleEndian(content.AsSpan(0), 50u);  // offset 2
    BinaryPrimitives.WriteUInt32LittleEndian(content.AsSpan(4), 25u);  // offset 1

    byte[] raw = BuildDictionary(1, content);
    var dict = ZstdDictionary.Parse(raw);

    Assert.That(dict.RepeatOffsets[0], Is.EqualTo(25));
    Assert.That(dict.RepeatOffsets[1], Is.EqualTo(50));
    Assert.That(dict.RepeatOffsets[2], Is.EqualTo(8)); // default
  }

  [Test]
  public void GetContentForWindow_ReturnsContent() {
    byte[] content = [0xAA, 0xBB, 0xCC, 0xDD];
    var dict = ZstdDictionary.CreateRaw(5, content);

    ReadOnlySpan<byte> window = dict.GetContentForWindow();

    Assert.That(window.ToArray(), Is.EqualTo(content));
  }

  [Test]
  public void ToBytes_RoundTrips() {
    byte[] content = new byte[64];
    new Random(42).NextBytes(content);
    uint dictId = 12345u;
    byte[] raw = BuildDictionary(dictId, content);

    var dict = ZstdDictionary.Parse(raw);
    byte[] serialized = dict.ToBytes();

    Assert.That(serialized, Is.EqualTo(raw));
  }

  [Test]
  public void DictionaryMagic_HasExpectedValue() {
    Assert.That(ZstdDictionary.DictionaryMagic, Is.EqualTo(0xEC30A437u));
  }

  [Test]
  public void CreateRaw_EmptyContent_Succeeds() {
    var dict = ZstdDictionary.CreateRaw(0, []);

    Assert.That(dict.Content, Is.Empty);
    Assert.That(dict.RepeatOffsets, Is.EqualTo(new[] { 1, 4, 8 }));
  }

  [Test]
  public void Parse_ZeroId_IsValid() {
    byte[] raw = BuildDictionary(0, [1, 2, 3, 4, 5, 6, 7, 8]);

    var dict = ZstdDictionary.Parse(raw);

    Assert.That(dict.DictionaryId, Is.EqualTo(0u));
  }

  [Test]
  public void Parse_MaxId_IsValid() {
    byte[] raw = BuildDictionary(uint.MaxValue, [1, 2, 3, 4, 5, 6, 7, 8]);

    var dict = ZstdDictionary.Parse(raw);

    Assert.That(dict.DictionaryId, Is.EqualTo(uint.MaxValue));
  }
}
