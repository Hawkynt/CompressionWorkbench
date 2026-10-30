using System.Reflection;
using System.Text;
using Compression.Core.Dictionary.Arj;
using FileFormat.Arj;

namespace Compression.Tests.Arj;

/// <summary>
/// ARJ methods 1-4 against archives the open-source ARJ port wrote (see Arj/ReferenceVectors/README.md).
/// Every member's CRC-32 is checked by the reader; on top of that the four archives hold the same files,
/// so all four methods must decode to identical bytes.
/// </summary>
[TestFixture]
public class ArjMethodVectorTests {
  private static byte[] Vector(string name) {
    using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("ArjVectors." + name)
      ?? throw new FileNotFoundException("Embedded ARJ vector missing: " + name);
    using var ms = new MemoryStream();
    s.CopyTo(ms);
    return ms.ToArray();
  }

  private static Dictionary<string, byte[]> ExtractAll(string vector, int expectedMethod) {
    using var reader = new ArjReader(new MemoryStream(Vector(vector)));
    var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
    foreach (var entry in reader.Entries) {
      // ARJ stores a member that does not shrink (RANDOM.BIN, ONE.TXT); the other two compress.
      if (entry.FileName is "TEXT.TXT" or "REPEAT.BIN")
        Assert.That(entry.Method, Is.EqualTo(expectedMethod), $"{vector}: {entry.FileName}");
      result[entry.FileName] = reader.ExtractEntry(entry);
    }
    return result;
  }

  [Test, Category("HappyPath")]
  [TestCase("m1.arj", 1)]
  [TestCase("m2.arj", 2)]
  [TestCase("m3.arj", 3)]
  [TestCase("m4.arj", 4)]
  public void RealArchive_EveryMemberPassesItsCrc(string vector, int method) {
    var files = ExtractAll(vector, method);
    Assert.That(files.Keys, Is.EquivalentTo(new[] { "TEXT.TXT", "RANDOM.BIN", "REPEAT.BIN", "ONE.TXT" }));
    Assert.That(Encoding.ASCII.GetString(files["TEXT.TXT"]), Does.StartWith("line 00001: the quick brown fox"));
    Assert.That(files["REPEAT.BIN"], Is.EqualTo(files["RANDOM.BIN"].Concat(files["RANDOM.BIN"]).ToArray()),
      "REPEAT.BIN is RANDOM.BIN twice: its second half can only be coded as matches 3,000 bytes back");
    Assert.That(files["ONE.TXT"], Is.EqualTo("a"u8.ToArray()));
    // SHA-256 of the inputs as the generator script printed them (see ReferenceVectors/README.md).
    Assert.That(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(files["TEXT.TXT"])),
      Is.EqualTo("c0541069eb5a1c9d4a11c2abb3993a554ba4a272fa671ea926f035775548de8e"));
    Assert.That(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(files["RANDOM.BIN"])),
      Is.EqualTo("44763bf1aa0863dd94c239c350c8fe4aa8dc640d51fe567e94f1b956b261a46f"));
  }

  [Test, Category("HappyPath")]
  public void AllFourMethods_DecodeToTheSameFiles() {
    var reference = ExtractAll("m1.arj", 1);
    for (var m = 2; m <= 4; ++m) {
      var other = ExtractAll($"m{m}.arj", m);
      foreach (var (name, bytes) in reference)
        Assert.That(other[name], Is.EqualTo(bytes).AsCollection, $"method {m}: {name}");
    }
  }

  // ── Method 4 decoder, hand-built streams ──────────────────────────────────────

  private static byte[] Bits(string bits) {
    bits = bits.Replace(" ", "");
    var result = new byte[(bits.Length + 7) / 8];
    for (var i = 0; i < bits.Length; ++i)
      if (bits[i] == '1') result[i >> 3] |= (byte)(0x80 >> (i & 7));
    return result;
  }

  [Test, Category("HappyPath")]
  public void Fastest_LiteralsAndOverlappingMatch() {
    // 'a' 'b', then length code 1 (bits "1 0" + 1-bit literal 0 = 1) → match of 3, distance code
    // 0 ("0" + 9-bit 000000001 = 1) → copy from 2 back: "aba"; then 'c'.
    var stream = Bits("0 01100001  0 01100010  10 0  0 000000001  0 01100011");
    Assert.That(ArjFastestDecoder.Decode(stream, 6), Is.EqualTo("ababac"u8.ToArray()));
  }

  [Test, Category("Boundary")]
  public void Fastest_LongestLengthAndFarthestDistanceCodes() {
    // Seed 15,872 bytes so the farthest distance (512+1024+2048+4096 + 8191 = 15,871, i.e. 15,872 back)
    // lands exactly on byte 0. Length code: seven 1-bits (no terminator) + 7-bit 1111111 →
    // 127 + 127 = 254 → match of 256.
    var seed = new byte[15_872];
    for (var i = 0; i < seed.Length; ++i) seed[i] = (byte)(i * 7 + 1);
    var sb = new StringBuilder();
    foreach (var b in seed)
      sb.Append('0').Append(Convert.ToString(b, 2).PadLeft(8, '0'));
    sb.Append("1111111 1111111").Append("1111 1111111111111");
    var output = ArjFastestDecoder.Decode(Bits(sb.ToString()), seed.Length + 256);
    Assert.That(output.AsSpan(0, seed.Length).SequenceEqual(seed), Is.True);
    Assert.That(output.AsSpan(seed.Length, 256).SequenceEqual(seed.AsSpan(0, 256)), Is.True);
  }

  [Test, Category("Exception")]
  public void Fastest_MatchBeforeStart_Throws() {
    var stream = Bits("10 0  0 000000000");
    Assert.Throws<InvalidDataException>(() => ArjFastestDecoder.Decode(stream, 3));
  }

  [Test, Category("EdgeCase")]
  public void Fastest_ZeroLength_ReadsNothing() =>
    Assert.That(ArjFastestDecoder.Decode([], 0), Is.Empty);

  [Test, Category("Exception")]
  public void Fastest_NegativeLength_Throws() =>
    Assert.Throws<ArgumentOutOfRangeException>(() => ArjFastestDecoder.Decode([], -1));
}
