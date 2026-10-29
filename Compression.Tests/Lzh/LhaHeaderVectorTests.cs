using System.Reflection;
using FileFormat.Lzh;

namespace Compression.Tests.Lzh;

/// <summary>
/// Header levels 1 and 3 and directory names, from archives in lhasa's test suite written by LHA for
/// Amiga 1.22 and LHA OS/2 2.08 (see ReferenceVectors/README.md). Each member must land at the data its
/// header describes, which the CRC-16 check on extraction proves.
/// </summary>
[TestFixture]
public class LhaHeaderVectorTests {
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
  public void Level1_ExtendedHeaders_GiveTheDirectoryAndTheDataStart() {
    var member = ExtractAll("lha_amiga_122_level1.lzh").Single();
    Assert.That(member.Entry.HeaderLevel, Is.EqualTo(1));
    Assert.That(member.Entry.FileName, Is.EqualTo("subdir/subdir2/hello.txt"));
  }

  [Test, Category("HappyPath")]
  public void Level3_ExtendedHeaders_GiveTheDirectory() {
    var member = ExtractAll("lha_os2_208_h3_subdir.lzh").Single(m => m.Entry.FileName.EndsWith("HELLO.TXT", StringComparison.Ordinal));
    Assert.That(member.Entry.FileName, Is.EqualTo("subdir/subdir2/HELLO.TXT"));
  }

  [Test, Category("EdgeCase")]
  public void AmigaArchive_WithDirectoriesStoredAsLh0_ReadsEveryFile() {
    var names = ExtractAll("lha_amiga_122_lh0_dirs_bug.lzh").Select(m => m.Entry.FileName).ToList();
    Assert.That(names, Does.Contain("MainDir/00_File.txt"));
    Assert.That(names, Does.Contain("MainDir/03_Dir/File01.txt"));
  }

  [Test, Category("EdgeCase")]
  public void TruncatedLastMember_IsListedButFailsToExtract() {
    var archive = Vector("lha_os2_208_h3_subdir.lzh");
    using var full = new LhaReader(new MemoryStream(archive));
    var last = full.Entries[^1];
    using var reader = new LhaReader(new MemoryStream(archive[..(int)(last.DataOffset + last.CompressedSize - 1)]));
    Assert.That(reader.Entries, Has.Count.EqualTo(full.Entries.Count));
    Assert.Catch<Exception>(() => reader.ExtractEntry(reader.Entries[^1]));
  }
}
