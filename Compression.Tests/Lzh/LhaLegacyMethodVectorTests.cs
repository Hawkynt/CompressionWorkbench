using System.Reflection;
using System.Text;
using Compression.Core.Dictionary.Lzh;
using FileFormat.Lzh;

namespace Compression.Tests.Lzh;

/// <summary>
/// <c>-lh2-</c> and <c>-lh3-</c> against archives UNLHA32.DLL wrote (from lhasa's test suite; see
/// ReferenceVectors/README.md). Both archives hold the same file, so besides the CRC-16 UNLHA32
/// recorded, the two independent decoders must agree with each other byte for byte.
/// </summary>
[TestFixture]
public class LhaLegacyMethodVectorTests {
  private static byte[] Vector(string name) {
    using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("LzhVectors." + name)
      ?? throw new FileNotFoundException("Embedded LHA vector missing: " + name);
    using var ms = new MemoryStream();
    s.CopyTo(ms);
    return ms.ToArray();
  }

  private static (LhaEntry Entry, byte[] Data) ExtractOnly(string vector, string expectedMethod) {
    using var reader = new LhaReader(new MemoryStream(Vector(vector)));
    var entry = reader.Entries.Single();
    Assert.That(entry.Method, Is.EqualTo(expectedMethod));
    return (entry, reader.ExtractEntry(entry)); // ExtractEntry verifies the header's CRC-16
  }

  [Test, Category("HappyPath")]
  [TestCase("lh2.lzh", LhaConstants.MethodLh2)]
  [TestCase("lh3.lzh", LhaConstants.MethodLh3)]
  public void RealArchive_DecodesToItsCrc(string vector, string method) {
    var (entry, data) = ExtractOnly(vector, method);
    Assert.That(entry.FileName, Is.EqualTo("LICENSE.MIT"));
    Assert.That(data, Has.Length.EqualTo(2853));
    var text = Encoding.ASCII.GetString(data);
    Assert.That(text, Does.Contain("Permission is hereby granted, free of charge"));
    Assert.That(text, Does.Contain("THE SOFTWARE IS PROVIDED \"AS IS\""));
  }

  [Test, Category("HappyPath")]
  public void Lh2AndLh3_OfTheSameFile_Agree() {
    var (_, lh2) = ExtractOnly("lh2.lzh", LhaConstants.MethodLh2);
    var (_, lh3) = ExtractOnly("lh3.lzh", LhaConstants.MethodLh3);
    Assert.That(lh2, Is.EqualTo(lh3).AsCollection);
  }

  [Test, Category("EdgeCase")]
  public void EmptyOutput_ReadsNothing() {
    Assert.That(Lh2Decoder.Decode([], 0), Is.Empty);
    Assert.That(Lh3Decoder.Decode([], 0), Is.Empty);
  }

  [Test, Category("Exception")]
  public void NegativeSize_Throws() {
    Assert.Throws<ArgumentOutOfRangeException>(() => Lh2Decoder.Decode([], -1));
    Assert.Throws<ArgumentOutOfRangeException>(() => Lh3Decoder.Decode([], -1));
  }

  [Test, Category("Exception")]
  public void Lh3_IncompleteTree_Throws() {
    // Block of 1 symbol; character tree: symbol 0 present with length 2 and every other symbol absent,
    // which leaves half the code space unassigned.
    var bits = new StringBuilder();
    bits.Append(Convert.ToString(1, 2).PadLeft(16, '0'));       // block size
    bits.Append("1" + "0001");                                 // symbol 0: present, length 1+1 = 2
    bits.Append(new string('0', 285));                         // symbols 1..285 absent
    var bytes = new byte[(bits.Length + 7) / 8 + 4];
    for (var i = 0; i < bits.Length; ++i)
      if (bits[i] == '1') bytes[i >> 3] |= (byte)(0x80 >> (i & 7));
    Assert.Throws<InvalidDataException>(() => Lh3Decoder.Decode(bytes, 1));
  }

  [Test, Category("Exception")]
  public void TruncatedArchive_FailsTheCrcInsteadOfReturningGarbage() {
    var archive = Vector("lh3.lzh");
    using var reader = new LhaReader(new MemoryStream(archive));
    var entry = reader.Entries.Single();
    // Zero the second half of the compressed body in place: the decoder reads zero bits there, and the
    // result must be rejected by the CRC check rather than handed back.
    var damaged = (byte[])archive.Clone();
    var bodyStart = (int)entry.DataOffset;
    for (var i = bodyStart + (int)entry.CompressedSize / 2; i < bodyStart + entry.CompressedSize; ++i)
      damaged[i] = 0;
    using var damagedReader = new LhaReader(new MemoryStream(damaged));
    Assert.Catch<Exception>(() => damagedReader.ExtractEntry(damagedReader.Entries.Single()));
  }
}
