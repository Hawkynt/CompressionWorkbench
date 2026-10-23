using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using FileSystem.HighSierra;

namespace Compression.Tests.HighSierra;

/// <summary>
/// High Sierra: the writer's structures against the byte positions of the 1986 working
/// paper, round trips through the reader, the reader on the paper's rarer record forms
/// (interleave, multi-extent, associated files), and the writer's refusals. The kernel
/// reading what we write is <see cref="HighSierraKernelMountTests"/>.
/// </summary>
[TestFixture]
public class HighSierraTests {

  private static byte[] Pattern(int size, int seed = 1) {
    var d = new byte[size];
    for (var i = 0; i < size; ++i) d[i] = (byte)(i * 13 + seed + (i >> 11));
    return d;
  }

  private static Dictionary<string, byte[]> ReadFiles(byte[] img) {
    var r = new HighSierraReader(new MemoryStream(img, writable: false));
    return r.Entries.Where(e => !e.IsDirectory).ToDictionary(e => e.Path, r.Extract);
  }

  // ── structures at the working paper's byte positions ──────────────────

  [Test]
  public void GivenVolume_WhenWritten_ThenDescriptorsSitAtTheWorkingPaperPositions() {
    var w = new HighSierraWriter { VolumeId = "test_vol" };
    w.AddFile("A.TXT", "a"u8.ToArray());
    var img = w.Build();
    var vd = img.AsSpan(16 * 2048, 2048).ToArray();
    Assert.Multiple(() => {
      Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(vd), Is.EqualTo(16u), "BP 1-8 LBN, LSB half");
      Assert.That(BinaryPrimitives.ReadUInt32BigEndian(vd[4..]), Is.EqualTo(16u), "BP 1-8 LBN, MSB half");
      Assert.That(vd[8], Is.EqualTo(1), "BP 9 type: Standard File Structure");
      Assert.That(Encoding.ASCII.GetString(vd.AsSpan(9, 5)), Is.EqualTo("CDROM"), "BP 10-14");
      Assert.That(vd[14], Is.EqualTo(1), "BP 15 version");
      Assert.That(Encoding.ASCII.GetString(vd.AsSpan(48, 32)).TrimEnd(), Is.EqualTo("TEST_VOL"), "BP 49-80 volume id, d-characters");
      Assert.That(BinaryPrimitives.ReadUInt32LittleEndian(vd[88..]) * 2048, Is.EqualTo((uint)img.Length), "BP 89-96 volume space size");
      Assert.That(BinaryPrimitives.ReadUInt16LittleEndian(vd[136..]), Is.EqualTo(2048), "BP 137-140 logical block size");
      Assert.That(vd[180], Is.EqualTo(34), "BP 181-214 root Directory Record, 34 bytes");
      Assert.That(vd[180 + 24] & 0x02, Is.EqualTo(2), "root record File Flags at BP 25: Directory");
      Assert.That(vd[180 + 33], Is.Zero, "root identifier is a (00) byte");
      Assert.That(vd[854], Is.EqualTo(1), "BP 855 file structure version");
      Assert.That(Encoding.ASCII.GetString(vd.AsSpan(822, 16)), Is.EqualTo("0000000000000000"), "BP 823-838 expiration: not specified");
    });
    var term = img.AsSpan(17 * 2048, 2048);
    Assert.That(term[8], Is.EqualTo(255));
    Assert.That(Encoding.ASCII.GetString(term.Slice(9, 5)), Is.EqualTo("CDROM"));
  }

  [Test]
  public void GivenTree_WhenWritten_ThenBothMandatoryPathTablesListDirectoriesInLevelOrder() {
    var w = new HighSierraWriter();
    w.AddFile("B/X.TXT", [1]);
    w.AddFile("A/C/Y.TXT", [2]);
    var img = w.Build();
    var vd = img.AsSpan(16 * 2048);
    var size = (int)BinaryPrimitives.ReadUInt32LittleEndian(vd[140..]);
    var l = (int)BinaryPrimitives.ReadUInt32LittleEndian(vd[148..]);
    var m = (int)BinaryPrimitives.ReadUInt32BigEndian(vd[164..]);

    static List<(uint Lbn, ushort Parent, string Id)> Parse(ReadOnlySpan<byte> t, int size, bool be) {
      var list = new List<(uint, ushort, string)>();
      for (var off = 0; off < size;) {
        int n = t[off + 5];
        var lbn = be ? BinaryPrimitives.ReadUInt32BigEndian(t[off..]) : BinaryPrimitives.ReadUInt32LittleEndian(t[off..]);
        var parent = be ? BinaryPrimitives.ReadUInt16BigEndian(t[(off + 6)..]) : BinaryPrimitives.ReadUInt16LittleEndian(t[(off + 6)..]);
        list.Add((lbn, parent, n == 1 && t[off + 8] == 0 ? "" : Encoding.ASCII.GetString(t.Slice(off + 8, n))));
        off += 8 + n + (n & 1);
      }
      return list;
    }
    var lt = Parse(img.AsSpan(l * 2048), size, be: false);
    var mt = Parse(img.AsSpan(m * 2048), size, be: true);
    Assert.That(lt.Select(r => (r.Parent, r.Id)), Is.EqualTo(new[] { ((ushort)1, ""), ((ushort)1, "A"), ((ushort)1, "B"), ((ushort)2, "C") }));
    Assert.That(mt, Is.EqualTo(lt), "the MSB-first table holds the same records");
    Assert.That(lt[0].Lbn, Is.EqualTo(BinaryPrimitives.ReadUInt32LittleEndian(vd[(180 + 2)..])), "record 1 is the root's extent");
  }

  [Test]
  public void GivenFile_WhenWritten_ThenItsRecordHasSixByteDateFlagsAtBp25AndVersionedIdentifier() {
    var when = new DateTime(1987, 6, 5, 4, 3, 2, DateTimeKind.Utc);
    var w = new HighSierraWriter();
    w.AddFile("readme", "x"u8.ToArray(), when);
    var img = w.Build();
    var root = (int)BinaryPrimitives.ReadUInt32LittleEndian(img.AsSpan(16 * 2048 + 180 + 2));
    var dir = img.AsSpan(root * 2048);
    var third = dir[0] + dir[dir[0]];   // after "." and ".."
    var rec = dir[third..];
    Assert.That(rec.Slice(18, 6).ToArray(), Is.EqualTo(new byte[] { 87, 6, 5, 4, 3, 2 }));
    Assert.That(rec[24], Is.Zero, "BP 25 flags: plain file");
    Assert.That(Encoding.ASCII.GetString(rec.Slice(33, rec[32])), Is.EqualTo("README.;1"));
    Assert.That(rec[0], Is.EqualTo(33 + 9), "odd identifier length: no pad byte");
  }

  // ── round trips ───────────────────────────────────────────────────────

  [Test]
  public void GivenTreeWithBoundarySizes_WhenWrittenAndRead_ThenEveryFileRoundTrips() {
    var expected = new Dictionary<string, byte[]> {
      ["EMPTY.DAT"] = [],
      ["ONE.BIN"] = [42],
      ["SECTOR.BIN"] = Pattern(2048),
      ["SECTOR1.BIN"] = Pattern(2049, 2),
      ["DOCS/GUIDE.TXT"] = "guide"u8.ToArray(),
      ["DOCS/API/REF.BIN"] = Pattern(100_000, 3),
      ["L1/L2/L3/L4/L5/L6/L7/DEEP.TXT"] = "eight levels"u8.ToArray(),
    };
    var w = new HighSierraWriter();
    foreach (var (n, d) in expected) w.AddFile(n, d);
    w.AddDirectory("EMPTYDIR");
    var img = w.Build();
    var r = new HighSierraReader(new MemoryStream(img));
    Assert.That(r.Problems, Is.Empty);
    Assert.That(r.Entries.Single(e => e.Path == "EMPTYDIR").IsDirectory, Is.True);
    var files = r.Entries.Where(e => !e.IsDirectory).ToDictionary(e => e.Path, r.Extract);
    Assert.That(files.Keys, Is.EquivalentTo(expected.Keys));
    foreach (var (n, d) in expected) Assert.That(files[n], Is.EqualTo(d), n);
  }

  [Test]
  public void GivenDirectoryLargerThanASector_WhenWritten_ThenNoRecordCrossesASectorAndAllRead() {
    var w = new HighSierraWriter();
    for (var i = 0; i < 150; ++i) w.AddFile($"FILE_NUMBER_{i:000}.DATA", [(byte)i]);
    var files = ReadFiles(w.Build());
    Assert.That(files, Has.Count.EqualTo(150));
    Assert.That(files["FILE_NUMBER_149.DATA"], Is.EqualTo(new byte[] { 149 }));
  }

  [Test]
  public void GivenLowerCaseNames_WhenWritten_ThenFoldedToDCharacters() {
    var w = new HighSierraWriter();
    w.AddFile("sub/inner.txt", [1]);
    Assert.That(ReadFiles(w.Build()).Keys, Is.EquivalentTo(new[] { "SUB/INNER.TXT" }));
  }

  [Test]
  public void GivenDescriptor_WhenCreatingFromInputs_ThenListAndExtractAgree() {
    var d = new HighSierraFormatDescriptor();
    using var ms = new MemoryStream();
    d.Create(ms, [ArchiveInputInfo.InMemory("HELLO.TXT", "hi"u8.ToArray()), new ArchiveInputInfo("", "SUB/", true), ArchiveInputInfo.InMemory("SUB/INNER.TXT", [9])],
      new FormatCreateOptions());
    ms.Position = 0;
    var list = d.List(ms, null);
    Assert.That(list.Select(e => e.Name), Is.EquivalentTo(new[] { "HELLO.TXT", "SUB", "SUB/INNER.TXT" }));
    Assert.That(d.ExtractEntryToMemory(ms, "sub/inner.txt", null), Is.EqualTo(new byte[] { 9 }));
  }

  // ── refusals ──────────────────────────────────────────────────────────

  [TestCase("A B.TXT", TestName = "HighSierra_SpaceInName_Refused")]
  [TestCase("A.B.C", TestName = "HighSierra_TwoFullStops_Refused")]
  [TestCase("NAME-1.TXT", TestName = "HighSierra_HyphenNotADCharacter_Refused")]
  [TestCase("ABCDEFGHIJKLMNOPQRSTUVWXYZ12345", TestName = "HighSierra_ThirtyOneCharFileName_Refused")]
  [TestCase("L1/L2/L3/L4/L5/L6/L7/L8/F", TestName = "HighSierra_NineLevels_Refused")]
  public void GivenUnstorablePath_WhenAdded_ThenRefused(string path)
    => Assert.Throws<NotSupportedException>(() => new HighSierraWriter().AddFile(path, [1]));

  [Test]
  public void GivenNamesThatFoldTogether_WhenAdded_ThenRefused() {
    var w = new HighSierraWriter();
    w.AddFile("a.txt", [1]);
    Assert.Throws<NotSupportedException>(() => w.AddFile("A.TXT", [2]));
  }

  [Test]
  public void GivenFileUsedAsDirectory_WhenAdded_ThenRefused() {
    var w = new HighSierraWriter();
    w.AddFile("A", [1]);
    Assert.Throws<NotSupportedException>(() => w.AddFile("A/B", [2]));
  }

  // ── reader on the paper's rarer record forms ─────────────────────────

  /// <summary>Builds a volume holding one file and returns the image plus the offset of that file's record.</summary>
  private static (byte[] Image, int Record) OneFile(byte[] data) {
    var w = new HighSierraWriter();
    w.AddFile("F.BIN", data);
    var img = w.Build();
    var root = (int)BinaryPrimitives.ReadUInt32LittleEndian(img.AsSpan(16 * 2048 + 180 + 2));
    var dir = root * 2048;
    return (img, dir + img[dir] + img[dir + img[dir]]);
  }

  [Test]
  public void GivenInterleavedExtent_WhenRead_ThenUnitsAreTakenAndSkipsJumped() {
    var data = new byte[3 * 2048];
    data.AsSpan(0, 2048).Fill(0xAA);
    data.AsSpan(2048, 2048).Fill(0xEE);     // belongs to "another file": skipped
    data.AsSpan(4096, 2048).Fill(0xBB);
    var (img, rec) = OneFile(data);
    HighSierraLayout.WriteBoth32(img.AsSpan(rec + 10), 4096);
    img[rec + 26] = 1;  // interleave size: one block per unit
    img[rec + 27] = 1;  // skip factor: one block of another file between units
    var f = ReadFiles(img)["F.BIN"];
    Assert.That(f.Take(2048).All(b => b == 0xAA) && f.Skip(2048).All(b => b == 0xBB), Is.True);
  }

  [Test]
  public void GivenAssociatedFileRecord_WhenRead_ThenItIsNotListed() {
    var (img, rec) = OneFile([1, 2, 3]);
    img[rec + 24] = 0x04;
    Assert.That(ReadFiles(img), Is.Empty);
  }

  [Test]
  public void GivenHiddenFlag_WhenRead_ThenListedAndMarkedHidden() {
    var (img, rec) = OneFile([1, 2, 3]);
    img[rec + 24] = 0x01;
    var r = new HighSierraReader(new MemoryStream(img));
    Assert.That(r.Entries.Single().IsHidden, Is.True);
  }

  [Test]
  public void GivenWrongSignature_WhenOpened_ThenInvalidData() {
    var (img, _) = OneFile([1]);
    img[16 * 2048 + 9] = (byte)'X';
    Assert.Throws<InvalidDataException>(() => new HighSierraReader(new MemoryStream(img)));
  }

  [Test]
  public void GivenIso9660Volume_WhenOpened_ThenNotMistakenForHighSierra() {
    var iso = new byte[18 * 2048];
    "\u0001CD001\u0001"u8.CopyTo(iso.AsSpan(16 * 2048));
    Assert.Throws<InvalidDataException>(() => new HighSierraReader(new MemoryStream(iso)));
    Assert.That(Compression.Lib.FormatDetector.DetectByMagic(iso).ToString(), Is.EqualTo("Iso"));
  }

  [Test]
  public void GivenHighSierraVolume_WhenDetectedByMagic_ThenHighSierra() {
    var (img, _) = OneFile([1]);
    Assert.That(Compression.Lib.FormatDetector.DetectByMagic(img).ToString(), Is.EqualTo("HighSierra"));
  }

  [Test]
  public void GivenFileExtentPastTheEnd_WhenExtracted_ThenRefused() {
    var (img, rec) = OneFile([1, 2, 3]);
    HighSierraLayout.WriteBoth32(img.AsSpan(rec + 2), 0x00FFFFFF);
    var r = new HighSierraReader(new MemoryStream(img));
    Assert.Throws<InvalidDataException>(() => r.Extract(r.Entries.Single()));
  }
}
