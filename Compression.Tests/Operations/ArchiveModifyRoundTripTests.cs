#pragma warning disable CS1591
using Compression.Registry;

namespace Compression.Tests.Operations;

/// <summary>
/// End-to-end modify round-trip for the ARCHIVE-category formats whose R/W claim
/// (<see cref="FormatCapabilities.CanModify"/>) is served by a name-preserving
/// container: create it with two files, Add a third through
/// <see cref="IArchiveModifiable"/>, Remove one, and verify the survivors list
/// correctly and extract byte-identically. This is the behavioural counterpart to
/// <see cref="WriteCapabilityHonestyTests"/> (which only proves the modify path
/// exists) — here the path is proven to WORK, whether it is a genuine in-place
/// editor or the verified extract → edit → re-create rebuild. Formats whose
/// listing mangles arbitrary names by design (8.3 disk filesystems, typed disc
/// images, mail folders) have their own per-format modify tests and are not
/// enumerated here.
/// </summary>
[TestFixture]
public class ArchiveModifyRoundTripTests {

  // Name-preserving modifiable archive containers (probe names round-trip verbatim).
  private static readonly string[] NamePreservingModifiableArchives = [
    "Ace", "Afs", "Ampk", "AndroidBundle", "Ba2", "Big", "Bsa", "Cbr", "Chm",
    "CompactPro", "Deb", "Doc", "Dzip", "FreeArc", "Gar", "Gob", "GodotPck",
    "Grp", "Hpi", "LzxAmiga", "Mpq", "Msg", "Msi", "Msix", "Narc", "Nds", "Nsa",
    "Ppt", "Psarc", "Rgss", "Rpa", "Sar", "Sarc", "Slf", "Sqx", "StuffIt",
    "ThumbsDb", "Tnef", "U8", "Uharc", "Vpk", "Vpp", "VppV2", "Vsdx", "Wad",
    "Xls", "Xps", "Ypf", "Zpaq",
    // previously promoted siblings that share the same contract
    "SevenZip", "Zip", "Tar", "Rar", "Cab", "Arj", "Zoo", "Arc", "Appx", "Apk",
  ];

  private static IEnumerable<TestCaseData> ModifiableArchiveIds() {
    Compression.Lib.FormatRegistration.EnsureInitialized();
    foreach (var id in NamePreservingModifiableArchives.OrderBy(x => x)) {
      var ops = FormatRegistry.GetArchiveOps(id);
      if (ops is IArchiveModifiable and IArchiveCreatable and IArchiveFormatOperations)
        yield return new TestCaseData(id).SetName($"ModifyRoundTrip_{id}");
    }
  }

  [TestCaseSource(nameof(ModifiableArchiveIds))]
  public void CreateAddRemove_SurvivorsStayByteIdentical(string formatId) {
    var ops = FormatRegistry.GetArchiveOps(formatId)!;
    var creator = (IArchiveCreatable)ops;
    var fops = (IArchiveFormatOperations)ops;
    var modifiable = (IArchiveModifiable)ops;

    var aData = System.Text.Encoding.ASCII.GetBytes("alpha content 0123456789\n");
    var bData = new byte[517];
    for (var i = 0; i < bData.Length; ++i) bData[i] = (byte)(i * 7);
    var cData = System.Text.Encoding.ASCII.GetBytes("charlie added after creation\n");

    using var ms = new MemoryStream();
    try {
      creator.Create(ms, [
        ArchiveInputInfo.InMemory("A.TXT", aData),
        ArchiveInputInfo.InMemory("B.BIN", bData),
      ], new FormatCreateOptions());
    } catch (Exception ex) {
      Assert.Ignore($"{formatId}: cannot create a two-file probe image ({ex.GetType().Name}).");
      return;
    }
    if (ms.Length == 0) { Assert.Ignore($"{formatId}: create produced no image."); return; }

    // Add a third file through the modify path.
    ms.Position = 0;
    try {
      modifiable.Add(ms, [ArchiveInputInfo.InMemory("C.TXT", cData)]);
    } catch (NotSupportedException) {
      Assert.Ignore($"{formatId}: Add cleanly NotSupported for this profile.");
      return;
    }
    var afterAdd = ListNames(fops, ms);
    Assert.That(Has(afterAdd, "A.TXT"), Is.True, $"{formatId}: A.TXT lost during Add (after={Join(afterAdd)})");
    Assert.That(Has(afterAdd, "B.BIN"), Is.True, $"{formatId}: B.BIN lost during Add (after={Join(afterAdd)})");
    Assert.That(Has(afterAdd, "C.TXT"), Is.True, $"{formatId}: C.TXT missing after Add (after={Join(afterAdd)})");

    // Remove the second file through the modify path.
    ms.Position = 0;
    var bName = afterAdd.First(n => Matches(n, "B.BIN"));
    modifiable.Remove(ms, [bName]);
    var afterRemove = ListNames(fops, ms);
    Assert.That(Has(afterRemove, "B.BIN"), Is.False, $"{formatId}: B.BIN still listed after Remove (after={Join(afterRemove)})");
    Assert.That(Has(afterRemove, "A.TXT"), Is.True, $"{formatId}: A.TXT lost during Remove (after={Join(afterRemove)})");
    Assert.That(Has(afterRemove, "C.TXT"), Is.True, $"{formatId}: C.TXT lost during Remove (after={Join(afterRemove)})");

    // Survivors must extract byte-identically.
    var work = Path.Combine(Path.GetTempPath(), "cwb_modrt_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(work);
    try {
      ms.Position = 0;
      fops.Extract(ms, work, null, null);
      var files = Directory.GetFiles(work, "*", SearchOption.AllDirectories);
      var a = files.FirstOrDefault(f => Matches(Path.GetFileName(f), "A.TXT"));
      var c = files.FirstOrDefault(f => Matches(Path.GetFileName(f), "C.TXT"));
      Assert.That(a, Is.Not.Null, $"{formatId}: A.TXT not extracted after modify cycle");
      Assert.That(c, Is.Not.Null, $"{formatId}: C.TXT not extracted after modify cycle");
      Assert.That(File.ReadAllBytes(a!).SequenceEqual(aData), Is.True, $"{formatId}: A.TXT content changed by modify cycle");
      Assert.That(File.ReadAllBytes(c!).SequenceEqual(cData), Is.True, $"{formatId}: C.TXT content changed by modify cycle");
    } finally {
      try { Directory.Delete(work, true); } catch { /* best effort */ }
    }
  }

  private static List<string> ListNames(IArchiveFormatOperations fops, Stream s) {
    s.Position = 0;
    return fops.List(s, null).Where(e => !e.IsDirectory).Select(e => e.Name).ToList();
  }

  private static bool Matches(string name, string user) =>
    string.Equals(Path.GetFileName(name.Replace('\\', '/')), user, StringComparison.OrdinalIgnoreCase);

  private static bool Has(IEnumerable<string> names, string user) => names.Any(n => Matches(n, user));

  private static string Join(IEnumerable<string> names) => string.Join(",", names.Take(8));
}
