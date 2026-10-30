using Compression.Core.Checksums;
using Compression.Core.Dictionary.Rar;

namespace Compression.Tests.Rar;

/// <summary>
/// The RAR 5 encoder against the RAR 5 decoder (itself proven on rar-made archives in
/// <see cref="RarConformanceTests"/>), and BLAKE2sp's incremental form.
/// </summary>
[TestFixture]
public class Rar5CodecTests {
  private static byte[] Text(int size) {
    var words = "the quick brown fox jumps over the lazy dog while the archive keeps every byte".Split(' ');
    var rnd = new Random(size);
    var sb = new System.Text.StringBuilder();
    while (sb.Length < size) sb.Append(words[rnd.Next(words.Length)]).Append(rnd.Next(9) == 0 ? '\n' : ' ');
    return System.Text.Encoding.ASCII.GetBytes(sb.ToString(0, size));
  }

  private static byte[] Noise(int size) {
    var d = new byte[size];
    new Random(size ^ 0x55).NextBytes(d);
    return d;
  }

  public static IEnumerable<TestCaseData> Inputs() {
    yield return new TestCaseData(new byte[] { 42 }).SetName("OneByte");
    yield return new TestCaseData(new byte[3]).SetName("ThreeZeros");
    yield return new TestCaseData(new byte[100_000]).SetName("LongRunOfZeros");
    yield return new TestCaseData(Text(65_536)).SetName("Text64K");
    yield return new TestCaseData(Text(700_000)).SetName("TextSeveralBlocks");
    yield return new TestCaseData(Noise(70_000)).SetName("Incompressible");
    yield return new TestCaseData(Text(300_000).Concat(Noise(5000)).Concat(Text(300_000)).ToArray()).SetName("MixedFarMatches");
  }

  [TestCaseSource(nameof(Inputs))]
  [Category("RoundTrip")]
  public void Given_Data_When_EncodedAtEachLevel_Then_TheDecoderRestoresIt(byte[] data) {
    foreach (var level in new[] { 1, 3, 5 }) {
      var packed = new Rar5Encoder(1 << 20, level).Compress(data);
      Assert.That(new Rar5Decoder(1 << 20).Decompress(packed, data.Length), Is.EqualTo(data), $"level {level}");
    }
  }

  [Test]
  [Category("RoundTrip")]
  public void Given_ASolidSequence_When_EachFileContinuesTheWindow_Then_EachDecodes() {
    var encoder = new Rar5Encoder(1 << 20);
    var decoder = new Rar5Decoder(1 << 20);
    var parts = new[] { Text(20_000), Text(20_000), new byte[5000], Text(1) };
    foreach (var p in parts)
      Assert.That(decoder.Decompress(encoder.Compress(p), p.Length), Is.EqualTo(p));
  }

  [Test]
  [Category("RoundTrip")]
  public void Given_DataLargerThanTheDictionary_When_Encoded_Then_MatchesStayInsideTheWindow() {
    var data = Text(600_000).Concat(Text(600_000)).ToArray();
    var packed = new Rar5Encoder(Rar5Constants.MinDictionarySize, 5).Compress(data);
    Assert.That(new Rar5Decoder(Rar5Constants.MinDictionarySize).Decompress(packed, data.Length), Is.EqualTo(data));
  }

  [Test]
  [Category("Exception")]
  public void Given_TruncatedPackedData_When_Decoded_Then_ItThrows() {
    var packed = new Rar5Encoder().Compress(Text(50_000));
    Assert.Throws<InvalidDataException>(() => new Rar5Decoder(1 << 20).Decompress(packed.AsSpan(0, packed.Length / 2), 50_000));
  }

  [Test]
  [Category("Exception")]
  public void Given_ACorruptBlockHeader_When_Decoded_Then_ItThrows() {
    var packed = new Rar5Encoder().Compress(Text(5000));
    packed[1] ^= 0xFF;   // block header check byte
    Assert.Throws<InvalidDataException>(() => new Rar5Decoder(1 << 20).Decompress(packed, 5000));
  }

  [TestCase(0)]
  [TestCase(1)]
  [TestCase(64)]
  [TestCase(511)]
  [TestCase(512)]
  [TestCase(513)]
  [TestCase(100_000)]
  public void Given_BlakeInput_When_FedInPieces_Then_TheDigestEqualsTheOneShot(int size) {
    var data = Noise(size);
    var whole = Blake2sp.Compute(data);
    var h = new Blake2sp();
    for (var at = 0; at < size; at += 37) h.Update(data.AsSpan(at, Math.Min(37, size - at)));
    Assert.That(h.Finish(), Is.EqualTo(whole));
    Assert.That(whole, Has.Length.EqualTo(32));
  }

  [Test]
  public void Given_TheSameSaltAndPassword_When_Rar5KeysAreDerived_Then_TheyAreStableAndDistinct() {
    var salt = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();
    var a = Compression.Core.Crypto.KeyDerivation.Rar5DeriveKeys("secret", salt, 10);
    var b = Compression.Core.Crypto.KeyDerivation.Rar5DeriveKeys("secret", salt, 10);
    Assert.That(a.Key, Is.EqualTo(b.Key));
    Assert.That(a.Key, Is.Not.EqualTo(a.HashKey));
    Assert.That(a.PasswordCheck, Has.Length.EqualTo(8));
    // The first key is plain PBKDF2-HMAC-SHA256 with 2^10 iterations.
    Assert.That(a.Key, Is.EqualTo(System.Security.Cryptography.Rfc2898DeriveBytes.Pbkdf2("secret"u8.ToArray(), salt, 1024,
      System.Security.Cryptography.HashAlgorithmName.SHA256, 32)));
  }
}
