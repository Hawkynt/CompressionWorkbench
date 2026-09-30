#pragma warning disable CS1591
using Compression.Registry;

namespace Compression.Tests.Maintenance;

/// <summary>
/// Every editing and maintenance verb, run against a volume the real Linux tools
/// made and filled, must either keep everything the operation was not asked to
/// change — names, contents, modes, owners, times, links, extended attributes,
/// DOS attributes, label, UUID — and leave the reference checker satisfied, or
/// refuse and leave the volume byte for byte as it was.
/// </summary>
/// <remarks>
/// <para>"R/W" that quietly unpacks a volume and writes a fresh one is WORM in
/// disguise: the files come back, and the label, the owners, the symlinks, the
/// hidden flags and every timestamp do not. A self-round-trip cannot see it,
/// because our writer never produced those in the first place; only a volume
/// made by the real tools carries them. <see cref="RealImageLab" /> builds one.</para>
///
/// <para>The expected outcome of each verb on each filesystem is part of the test:
/// the table below is the evidence matrix. <see cref="Outcome.Preserve" /> means
/// the verb works in place and loses nothing; <see cref="Outcome.Refuse" /> means
/// the format cannot do it without losing something, and says so.</para>
/// </remarks>
[TestFixture]
[Category("ExternalFsInterop")]
[NonParallelizable]
public class MaintenancePreservesRealVolumesTests {

  public enum Outcome { Preserve, Refuse }

  private sealed record Op(string Name, Action<Stream, object> Run, string[] Changes, string[] TouchedFolders);

  private static readonly Op[] Ops = [
    new("add_root", (s, d) => ((IArchiveModifiable)d).Add(s, [ArchiveInputInfo.InMemory("added.txt", "added root\n"u8)]),
      ["added.txt"], []),
    new("add_nested", (s, d) => ((IArchiveModifiable)d).Add(s, [ArchiveInputInfo.InMemory("docs/added2.txt", "added nested\n"u8)]),
      ["docs/added2.txt"], ["docs"]),
    new("remove_root", (s, d) => ((IArchiveModifiable)d).Remove(s, ["big.bin"]), ["big.bin"], []),
    new("remove_nested", (s, d) => ((IArchiveModifiable)d).Remove(s, ["docs/readme.txt"]), ["docs/readme.txt"], ["docs"]),
    new("defrag_start", (s, d) => Defrag(s, d, new DefragOptions { Mode = DefragMode.ConsolidateAtStart }), [], []),
    new("defrag_end", (s, d) => Defrag(s, d, new DefragOptions { Mode = DefragMode.ConsolidateAtEnd }), [], []),
    new("defrag_fill", (s, d) => Defrag(s, d, new DefragOptions { Mode = DefragMode.FillHolesLazy }), [], []),
    new("defrag_carve", (s, d) => Defrag(s, d, new DefragOptions { Mode = DefragMode.CarveHole, HoleSize = 1 << 20 }), [], []),
    new("defrag_ascending", (s, d) => Defrag(s, d, new DefragOptions { Mode = DefragMode.AscendingOrder }), [], []),
    new("defrag_interleave", (s, d) => Defrag(s, d, new DefragOptions { InterleaveStride = 2 }), [], []),
    new("defrag_metadata_front", (s, d) => Defrag(s, d, new DefragOptions { MetadataZonePlacement = MetadataZone.Front }), [], []),
    new("wipe", (s, d) => ((IWipeEmpty)d).WipeUnusedSpace(s), [], []),
    new("shrink", (s, d) => {
      using var o = new MemoryStream();
      ((IArchiveShrinkable)d).Shrink(s, o);
      s.Position = 0; s.SetLength(0); o.Position = 0; o.CopyTo(s);
    }, [], []),
  ];

  private static void Defrag(Stream s, object d, DefragOptions o) {
    ((IArchiveDefragmentable)d).Defragment(s, o);
  }

  /// <summary>
  /// The evidence matrix: what each verb does on each filesystem. Anything absent
  /// is not yet verified and is not run.
  /// </summary>
  private static readonly Dictionary<string, (Func<object> Descriptor, Dictionary<string, Outcome> Expect)> Matrix =
    new(StringComparer.OrdinalIgnoreCase) {
      ["ext4"] = (() => new FileSystem.Ext.ExtFormatDescriptor(), ExtExpectations()),
      ["ext2"] = (() => new FileSystem.Ext.ExtFormatDescriptor(), ExtExpectations()),
      ["fat16"] = (() => new FileSystem.Fat.FatFormatDescriptor(), FatExpectations()),
      // Interleaving a 3 MB file cluster by cluster exceeds the planner's move budget
      // on 4 KiB clusters; the defragmenter refuses before moving anything.
      ["fat32"] = (() => new FileSystem.Fat.FatFormatDescriptor(),
        new Dictionary<string, Outcome>(FatExpectations()) { ["defrag_interleave"] = Outcome.Refuse }),
      ["ntfs"] =(() => new FileSystem.Ntfs.NtfsFormatDescriptor(), new() {
        ["add_root"] = Outcome.Preserve, ["add_nested"] = Outcome.Preserve,
        ["remove_root"] = Outcome.Preserve, ["remove_nested"] = Outcome.Preserve,
        ["defrag_start"] = Outcome.Preserve, ["defrag_end"] = Outcome.Preserve, ["defrag_fill"] = Outcome.Preserve,
        ["defrag_carve"] = Outcome.Preserve, ["defrag_ascending"] = Outcome.Preserve,
        ["defrag_interleave"] = Outcome.Refuse, ["defrag_metadata_front"] = Outcome.Preserve,
        ["wipe"] = Outcome.Preserve, ["shrink"] = Outcome.Preserve,
      }),
    };

  private static Dictionary<string, Outcome> ExtExpectations() => new() {
    ["add_root"] = Outcome.Preserve, ["add_nested"] = Outcome.Preserve,
    ["remove_root"] = Outcome.Preserve, ["remove_nested"] = Outcome.Preserve,
    ["defrag_start"] = Outcome.Preserve, ["defrag_end"] = Outcome.Preserve, ["defrag_fill"] = Outcome.Preserve,
    ["defrag_carve"] = Outcome.Preserve, ["defrag_ascending"] = Outcome.Preserve,
    ["defrag_interleave"] = Outcome.Refuse, ["defrag_metadata_front"] = Outcome.Preserve,
    ["wipe"] = Outcome.Preserve, ["shrink"] = Outcome.Preserve,
  };

  private static Dictionary<string, Outcome> FatExpectations() => new() {
    ["add_root"] = Outcome.Preserve, ["add_nested"] = Outcome.Preserve,
    ["remove_root"] = Outcome.Preserve, ["remove_nested"] = Outcome.Preserve,
    ["defrag_start"] = Outcome.Preserve, ["defrag_end"] = Outcome.Preserve, ["defrag_fill"] = Outcome.Preserve,
    ["defrag_carve"] = Outcome.Preserve, ["defrag_ascending"] = Outcome.Preserve,
    ["defrag_interleave"] = Outcome.Preserve, ["defrag_metadata_front"] = Outcome.Preserve,
    ["wipe"] = Outcome.Preserve, ["shrink"] = Outcome.Preserve,
  };

  private static IEnumerable<TestCaseData> Cases() {
    foreach (var (fs, (_, expect)) in Matrix)
      foreach (var op in Ops)
        if (expect.TryGetValue(op.Name, out var outcome))
          yield return new TestCaseData(fs, op.Name, outcome).SetName($"{fs}: {op.Name} -> {outcome}");
  }

  private string _root = null!;
  private readonly Dictionary<string, (string Image, List<string> Manifest)> _references = new(StringComparer.OrdinalIgnoreCase);

  [OneTimeSetUp]
  public void CreateRoot() {
    this._root = Path.Combine(Path.GetTempPath(), "cwb_realvol_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(this._root);
  }

  [OneTimeTearDown]
  public void DeleteRoot() {
    try { Directory.Delete(this._root, true); } catch { /* best effort */ }
  }

  private (string Image, List<string> Manifest) Reference(RealImageLab.FsKind kind) {
    if (this._references.TryGetValue(kind.Name, out var cached)) return cached;
    var img = RealImageLab.BuildReference(kind, Path.Combine(this._root, kind.Name));
    var (clean, output) = RealImageLab.Check(kind, img);
    Assert.That(clean, Is.True, $"{kind.Name}: the reference volume itself is not clean\n{output}");
    var result = (img, RealImageLab.Manifest(kind, img));
    this._references[kind.Name] = result;
    return result;
  }

  [TestCaseSource(nameof(Cases)), CancelAfter(600_000)]
  public void Verb_KeepsEverythingElse_OrRefusesUntouched(string fs, string opName, Outcome expected) {
    var kind = RealImageLab.Kinds[fs];
    if (!RealImageLab.Available(kind, out var why)) Assert.Ignore(why);
    var op = Ops.Single(o => o.Name == opName);
    var (reference, before) = this.Reference(kind);

    var work = Path.Combine(this._root, $"{fs}.{opName}.img");
    File.Copy(reference, work, true);
    var original = File.ReadAllBytes(work);

    Exception? refusal = null;
    using (var stream = new FileStream(work, FileMode.Open, FileAccess.ReadWrite)) {
      try {
        op.Run(stream, Matrix[fs].Descriptor());
      } catch (NotSupportedException ex) {
        refusal = ex;
      }
    }

    if (expected == Outcome.Refuse) {
      Assert.That(refusal, Is.Not.Null, $"{fs}: {opName} was expected to refuse but ran.");
      Assert.That(File.ReadAllBytes(work), Is.EqualTo(original), $"{fs}: {opName} refused but changed the volume.");
      return;
    }

    Assert.That(refusal, Is.Null, $"{fs}: {opName} refused: {refusal?.Message}");
    var (clean, output) = RealImageLab.Check(kind, work);
    Assert.That(clean, Is.True, $"{fs}: after {opName} the reference checker reports:\n{output}");

    var after = RealImageLab.Manifest(kind, work);
    var normalizedBefore = WithoutFolderTimes(before, op.TouchedFolders);
    var normalizedAfter = WithoutFolderTimes(after, op.TouchedFolders);
    var diff = RealImageLab.UnexpectedDifferences(normalizedBefore, normalizedAfter, op.Changes);
    Assert.That(diff, Is.Empty, $"{fs}: {opName} changed what it was not asked to:\n{string.Join("\n", diff)}");

    foreach (var change in op.Changes) {
      var present = after.Any(l => l.StartsWith($"entry {change}|", StringComparison.Ordinal));
      var shouldExist = opName.StartsWith("add", StringComparison.Ordinal);
      Assert.That(present, Is.EqualTo(shouldExist), $"{fs}: {opName} did not {(shouldExist ? "add" : "remove")} '{change}'.");
    }

    if (opName.StartsWith("defrag", StringComparison.Ordinal) || opName == "wipe")
      Assert.That(new FileInfo(work).Length, Is.EqualTo(original.LongLength), $"{fs}: {opName} changed the image size.");
  }

  /// <summary>
  /// A folder the operation added to or removed from legitimately gets a new
  /// modification time; everything else about it must stay.
  /// </summary>
  private static List<string> WithoutFolderTimes(List<string> manifest, string[] folders)
    => manifest.Select(l => {
      if (!l.StartsWith("entry ", StringComparison.Ordinal)) return l;
      var f = l.Split('|');
      if (f.Length != 9 || !folders.Contains(f[0][6..])) return l;
      f[6] = "*";
      return string.Join('|', f);
    }).ToList();
}
