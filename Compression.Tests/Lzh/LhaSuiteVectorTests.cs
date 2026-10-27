using System.Reflection;
using System.Security.Cryptography;
using FileFormat.Lzh;

namespace Compression.Tests.Lzh;

/// <summary>
/// Archives from lhasa's test suite, written by LHarc 1.13, LArc 3.33, PMarc 1.24 and 2, LHA for
/// Amiga 1.22, LHA OS/2 2.08 and UNLHA32 (see ReferenceVectors/README.md). Every member must pass the
/// CRC-16 its archiver recorded; several of them hold the same GPL-2 text, so the methods must also
/// agree with each other byte for byte.
/// </summary>
[TestFixture]
public class LhaSuiteVectorTests {
  private static byte[] Vector(string name) {
    using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("LzhVectors." + name)
      ?? throw new FileNotFoundException("Embedded LHA vector missing: " + name);
    using var ms = new MemoryStream();
    s.CopyTo(ms);
    return ms.ToArray();
  }

  private static List<(LhaEntry Entry, byte[] Data)> ExtractAll(string vector) {
    using var reader = new LhaReader(new MemoryStream(Vector(vector)));
    return reader.Entries.Select(e => (e, reader.ExtractEntry(e))).ToList();
  }

  [Test, Category("HappyPath")]
  [TestCase("lharc113_lh1.lzh", "-lh1-", "GPL-2", 0, TestName = "LHarc 1.13 -lh1-, level 0")]
  [TestCase("larc333_lz5.lzs", "-lz5-", "GPL-2", 0, TestName = "LArc 3.33 -lz5-, level 0")]
  [TestCase("larc333_initial.lzs", "-lz5-", "initial.bin", 0, TestName = "LArc 3.33 -lz5- reaching into the initial ring")]
  [TestCase("generated_lzs.lzs", "-lzs-", "GPL-2", 0, TestName = "-lzs-, level 0")]
  [TestCase("pmarc124_pm1.pma", "-pm1-", "COPYING.TXT", 0, TestName = "PMarc 1.24 -pm1-")]
  [TestCase("pmarc2_pm2.pma", "-pm2-", "GPL-2.", 0, TestName = "PMarc 2 -pm2-")]
  [TestCase("lha_os2_208_h3_lh5.lzh", "-lh5-", "GPL-2", 3, TestName = "LHA OS/2 2.08 level-3 header")]
  [TestCase("unlha32_h2_lhx.lzh", "-lhx-", "GPL-2", 2, TestName = "UNLHA32 -lhx- (1 MiB window)")]
  public void Member_DecodesToItsCrc(string vector, string method, string name, int level) {
    var (entry, data) = ExtractAll(vector).Single(m => m.Entry.FileName == name);
    Assert.That(entry.Method, Is.EqualTo(method));
    Assert.That(entry.HeaderLevel, Is.EqualTo(level));
    Assert.That(data, Has.Length.EqualTo(entry.OriginalSize));
  }

  [Test, Category("HappyPath")]
  public void DifferentMethods_OfTheSameText_Agree() {
    var expected = SHA256.HashData(ExtractAll("lharc113_lh1.lzh").Single().Data);
    foreach (var vector in new[] { "larc333_lz5.lzs", "generated_lzs.lzs", "lha_os2_208_h3_lh5.lzh", "unlha32_h2_lhx.lzh" })
      Assert.That(SHA256.HashData(ExtractAll(vector).Single(m => m.Entry.FileName == "GPL-2").Data), Is.EqualTo(expected), vector);
  }

  [Test, Category("Exception")]
  public void DamagedMember_FailsItsCrc() {
    var archive = Vector("lharc113_lh1.lzh");
    using var probe = new LhaReader(new MemoryStream(archive));
    var entry = probe.Entries.Single();
    archive[entry.DataOffset + entry.CompressedSize / 2] ^= 0x40;
    using var reader = new LhaReader(new MemoryStream(archive));
    Assert.Catch<Exception>(() => reader.ExtractEntry(reader.Entries.Single()));
  }

}
