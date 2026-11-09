using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using FileFormat.Bkf;

namespace Compression.Tests.Bkf;

/// <summary>
/// Editing media that carry a Type 1 Media Based Catalog (MTF 1.00a §7.3) — every NTBackup
/// backup file has one — and media with several Data Sets. After an edit, the File/Directory
/// Detail of every set must describe its DBLKs where they now are, the Set Map every set, and
/// the second ESET must point at both; on NTBackup's own backup, rewriting without a change
/// must give back NTBackup's catalog.
/// </summary>
[TestFixture]
public sealed class BkfCatalogTests {
  private const int Flb = 1024;

  private static MemoryStream Medium(ushort catalog, int sets) {
    var m = new MtfFixture(mediaBasedCatalog: catalog);
    for (var n = 1; n <= sets; ++n) {
      var number = n;
      m.Set(number, s => s.Volb("C:").Dirb("\0", security: true).File("top.txt", Encoding.ASCII.GetBytes($"top {number}"), security: true)
        .Dirb("docs\0").File("a.txt", Encoding.ASCII.GetBytes($"a {number}")).Dirb("docs\0sub\0").Dirb("other\0"));
    }
    var ms = new MemoryStream();
    ms.Write(m.Build());
    return ms;
  }

  private static Dictionary<string, byte[]> Files(Stream bkf) {
    bkf.Position = 0;
    using var r = new BkfReader(bkf, leaveOpen: true);
    return r.Entries.Where(e => !e.IsDirectory).ToDictionary(e => e.Name, r.Extract);
  }

  [Test, Category("HappyPath")]
  public void GivenACataloguedMedium_WhenAFileIsAddedAndAnotherRemoved_ThenTheCatalogDescribesTheEditedSet() {
    using var bkf = Medium(catalog: 1, sets: 1);
    BkfEditor.AddFile(bkf, "docs/sub/new.txt", "new"u8.ToArray());
    BkfEditor.Remove(bkf, "top.txt");

    Assert.That(Files(bkf).Keys, Is.EquivalentTo(new[] { "docs/a.txt", "docs/sub/new.txt" }));
    CatalogChecker.Check(bkf.ToArray());
  }

  [Test, Category("HappyPath")]
  public void GivenTwoDataSets_WhenTheSecondIsEdited_ThenTheFirstKeepsItsFilesAndEverySetIsCatalogued() {
    using var bkf = Medium(catalog: 1, sets: 2);
    BkfEditor.AddFile(bkf, "set-02/docs/added.txt", "added"u8.ToArray());
    BkfEditor.Remove(bkf, "set-02/docs/a.txt");

    Assert.That(Files(bkf).Keys, Is.EquivalentTo(new[] { "set-01/top.txt", "set-01/docs/a.txt", "set-02/top.txt", "set-02/docs/added.txt" }));
    Assert.That(Files(bkf)["set-01/docs/a.txt"], Is.EqualTo("a 1"u8.ToArray()));
    var sets = CatalogChecker.Check(bkf.ToArray());
    Assert.That(sets, Is.EqualTo(2), "a Set Map entry per Data Set");
  }

  [Test, Category("HappyPath")]
  public void GivenTwoDataSetsWithoutACatalog_WhenEdited_ThenNoCatalogIsWritten() {
    using var bkf = Medium(catalog: 0, sets: 2);
    BkfEditor.AddFile(bkf, "set-01/x.txt", [1]);
    Assert.That(Files(bkf).Keys, Does.Contain("set-01/x.txt"));
    Assert.That(bkf.ToArray().AsSpan().IndexOf("TFDD"u8), Is.EqualTo(-1));
  }

  [Test, Category("ErrorHandling")]
  public void GivenTwoDataSets_WhenAPathNamesNoSet_ThenItIsRefusedUntouched() {
    using var bkf = Medium(catalog: 1, sets: 2);
    var before = bkf.ToArray();
    Assert.Throws<NotSupportedException>(() => BkfEditor.AddFile(bkf, "docs/x.txt", [1]));
    Assert.Throws<NotSupportedException>(() => BkfEditor.AddFile(bkf, "set-03/x.txt", [1]));
    Assert.That(bkf.ToArray(), Is.EqualTo(before));
  }

  [Test, Category("ErrorHandling")]
  public void GivenATypeTwoCatalog_WhenEdited_ThenItIsRefusedUntouched() {
    using var bkf = Medium(catalog: 2, sets: 1);
    var before = bkf.ToArray();
    Assert.Throws<NotSupportedException>(() => BkfEditor.AddFile(bkf, "x.txt", [1]));
    Assert.That(bkf.ToArray(), Is.EqualTo(before));
  }

  // ── NTBackup's own backup ────────────────────────────────────────────────

  [Test, Category("OsIntegration")]
  public void GivenTheNtbackupSample_WhenRewrittenWithoutAChange_ThenItsCatalogComesBackAsNtbackupWroteIt() {
    var original = File.ReadAllBytes(RequireSample());
    using var bkf = new MemoryStream();
    bkf.Write(original);
    BkfEditor.AddFile(bkf, "os/probe.txt", [1]);
    BkfEditor.Remove(bkf, "os/probe.txt");

    var before = CatalogChecker.Streams(original);
    var after = CatalogChecker.Streams(bkf.ToArray());
    Assert.Multiple(() => {
      Assert.That(Convert.ToHexString(after.Fdd), Is.EqualTo(Convert.ToHexString(before.Fdd)), "File/Directory Detail");
      Assert.That(Convert.ToHexString(after.SetMap), Is.EqualTo(Convert.ToHexString(before.SetMap)), "Set Map");
      Assert.That(after.SecondEset, Is.EqualTo(before.SecondEset), "second ESET: Set Map and FDD PBAs, media sequence, set number");
      Assert.That(after.SsetPba, Is.EqualTo(before.SsetPba), "SSET PBA");
    });
    CatalogChecker.Check(bkf.ToArray());
  }

  [Test, Category("OsIntegration")]
  public void GivenTheNtbackupSample_WhenEdited_ThenTheCatalogFollowsAndTheFilesRead() {
    var original = File.ReadAllBytes(RequireSample());
    using var bkf = new MemoryStream();
    bkf.Write(original);
    BkfEditor.AddFile(bkf, "os/added by edit.txt", "added"u8.ToArray());
    BkfEditor.Remove(bkf, "os/winnt50C/hal.dll");

    var files = Files(bkf);
    Assert.That(files.Keys, Is.EquivalentTo(new[] {
      "os/added by edit.txt", "os/winnt50C/a121165.p64", "os/winnt50C/a221164.p64", "os/winnt50C/a221264.p64", "os/winnt50C/osloader.exe",
    }));
    Assert.That(Convert.ToHexStringLower(SHA256.HashData(files["os/winnt50C/osloader.exe"])),
      Is.EqualTo("d5d7ceb44df1f6a5b4c8561654d363ebd101fdb6175ca588f3189357a9269187"));
    CatalogChecker.Check(bkf.ToArray());
  }

  internal static string RequireSample() {
    var path = Environment.GetEnvironmentVariable("CWB_BKF_SAMPLE");
    if (string.IsNullOrEmpty(path) || !File.Exists(path))
      Assert.Ignore("CWB_BKF_SAMPLE does not name the NTBackup sample (axp64-boot.bkf).");
    return path;
  }

  /// <summary>Reads a Type 1 catalog back by the spec and checks it against the DBLKs it describes.</summary>
  internal static class CatalogChecker {
    public sealed record CatalogStreams(byte[] Fdd, byte[] SetMap, (ulong, ulong, ushort, ushort) SecondEset, ulong SsetPba);

    private static ushort U16(byte[] b, long at) => BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan((int)at));
    private static uint U32(byte[] b, long at) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan((int)at));
    private static ulong U64(byte[] b, long at) => BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan((int)at));
    private static string Type(byte[] b, long at) => Encoding.ASCII.GetString(b, (int)at, 4);

    private static bool IsDblk(byte[] b, int at) {
      if (at + 52 > b.Length || b[at] is < (byte)'A' or > (byte)'Z') return false;
      ushort x = 0;
      for (var i = 0; i < 50; i += 2) x ^= U16(b, at + i);
      return x == U16(b, at + 50);
    }

    /// <summary>The first Data Set's TFDD and TSMP data, its second ESET's catalog fields and its SSET PBA.</summary>
    public static CatalogStreams Streams(byte[] b) {
      var sset = Enumerable.Range(0, b.Length / Flb).Select(i => i * Flb).First(o => IsDblk(b, o) && Type(b, o) == "SSET");
      var tfdd = b.AsSpan().IndexOf("TFDD"u8);
      var tsmp = b.AsSpan().IndexOf("TSMP"u8);
      var second = Enumerable.Range(0, b.Length / Flb).Select(i => i * Flb).Where(o => IsDblk(b, o) && Type(b, o) == "ESET").ElementAt(1);
      return new(b.AsSpan(tfdd + 22, (int)U64(b, tfdd + 8)).ToArray(), b.AsSpan(tsmp + 22, (int)U64(b, tsmp + 8)).ToArray(),
        (U64(b, second + 60), U64(b, second + 68), U16(b, second + 76), U16(b, second + 78)), U64(b, sset + 80));
    }

    /// <summary>
    /// For every Data Set: each FDD entry names a DBLK of its type and name at its Format Logical
    /// Address, in DBLK order, with no DBLK left out; file entries link to their folder's entry;
    /// FEND pads to the Set Map's physical block; the second ESET holds the Set Map's and the FDD's
    /// PBAs (counted from one); the Set Map lists every set so far with its counts. Returns the
    /// number of Data Sets.
    /// </summary>
    public static int Check(byte[] b) {
      var blocks = Enumerable.Range(0, b.Length / Flb).Select(i => i * Flb).Where(o => IsDblk(b, o)).Select(o => (Offset: o, Type: Type(b, o))).ToList();
      var sets = 0;
      for (var i = 0; i < blocks.Count; ++i) {
        if (blocks[i].Type != "SSET") continue;
        ++sets;
        var ssetAt = blocks[i].Offset;
        var inSet = blocks.Skip(i + 1).TakeWhile(x => x.Type is not ("SFMB" or "ESET")).ToList();
        var eset = blocks.Skip(i + 1).First(x => x.Type == "ESET").Offset;
        var tfdd = eset + U16(b, eset + 8);
        Assert.That(Type(b, tfdd), Is.EqualTo("TFDD"), $"set {sets}: TFDD after the ESET");
        Assert.That(tfdd % Flb, Is.Zero, "TFDD on a physical block");
        var fddLength = (int)U64(b, tfdd + 8);
        var tsmp = tfdd + 22 + fddLength;
        Assert.That(Type(b, tsmp), Is.EqualTo("TSMP"), "TSMP right after the FDD");
        Assert.That(tsmp % Flb, Is.Zero, "TSMP on a physical block");

        // FDD entries against the DBLKs.
        var fdd = tfdd + 22;
        var at = 0;
        var described = new List<(string Type, ulong Fla)>();
        var entryOffsets = new Dictionary<int, string>();
        var lastDir = -1;
        while (true) {
          var len = U16(b, fdd + at);
          var type = Type(b, fdd + at + 2);
          if (type == "FEND") {
            Assert.That(fdd + at + len, Is.EqualTo(tsmp), "FEND runs to the Set Map");
            break;
          }
          var fla = U64(b, fdd + at + 12);
          var dblk = ssetAt + (int)fla * Flb;
          Assert.That(Type(b, dblk), Is.EqualTo(type), $"set {sets}: FDD {type} entry at FLA {fla}");
          Assert.That(U64(b, fdd + at + 20), Is.EqualTo(U64(b, dblk + 12)), "displayable size");
          if (type is "DIRB" or "FILE") {
            var nameSize = U16(b, fdd + at + 60);
            var nameOffset = U16(b, fdd + at + 62);
            var nameField = type == "DIRB" ? 80 : 84;
            var dblkName = b.AsSpan(dblk + U16(b, dblk + nameField + 2), U16(b, dblk + nameField)).ToArray();
            Assert.That(b.AsSpan(fdd + at + nameOffset, nameSize).ToArray(), Is.EqualTo(dblkName), $"{type} name at FLA {fla}");
            if (type == "FILE") Assert.That(U32(b, fdd + at + 28), Is.EqualTo((uint)lastDir), "a file links to its folder's entry");
            else lastDir = at;
          }
          described.Add((type, fla));
          at += len;
        }
        var expected = inSet.Where(x => x.Type is "VOLB" or "DIRB" or "FILE").Select(x => (x.Type, (ulong)((x.Offset - ssetAt) / Flb))).ToList();
        Assert.That(described, Is.EqualTo(expected), $"set {sets}: one FDD entry per DBLK, in order");

        // The second ESET, the Set Map.
        var second = blocks.Skip(i + 1).Where(x => x.Type == "ESET").ElementAt(1).Offset;
        Assert.That(U64(b, second + 60), Is.EqualTo((ulong)(tsmp / Flb + 1)), "second ESET: Set Map PBA");
        Assert.That(U64(b, second + 68), Is.EqualTo((ulong)(tfdd / Flb + 1)), "second ESET: FDD PBA");
        var map = tsmp + 22;
        Assert.That(U16(b, map + 4), Is.EqualTo((ushort)sets), "Set Map entries so far");
        var entry = map + 8;
        for (var s = 1; s < sets; ++s) entry += U16(b, entry) + VolumeEntries(b, entry);
        Assert.That(U64(b, entry + 12), Is.EqualTo(U64(b, ssetAt + 80)), "Set Map: SSET PBA");
        Assert.That(U64(b, ssetAt + 80), Is.EqualTo((ulong)(ssetAt / Flb + 1)), "SSET PBA counted from one");
        Assert.That(U64(b, entry + 20), Is.EqualTo((ulong)(tfdd / Flb + 1)), "Set Map: FDD PBA");
        Assert.That(U32(b, entry + 40), Is.EqualTo((uint)inSet.Count(x => x.Type == "DIRB")), "Set Map: directories");
        Assert.That(U32(b, entry + 44), Is.EqualTo((uint)inSet.Count(x => x.Type == "FILE")), "Set Map: files");
      }
      return sets;
    }

    /// <summary>Bytes of the Volume Entries after a Set Map entry.</summary>
    private static int VolumeEntries(byte[] b, int entry) {
      var count = U16(b, entry + 60);
      var at = entry + U16(b, entry);
      var total = 0;
      for (var v = 0; v < count; ++v) {
        total += U16(b, at + total);
      }
      return total;
    }
  }
}
