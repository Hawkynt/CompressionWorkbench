using Compression.Registry;

namespace Compression.Tests.Ntfs;

/// <summary>
/// An NTFS volume must hold everything it says it holds, and must refuse to be
/// written when it cannot.
/// </summary>
/// <remarks>
/// <para>Three faults sat behind these, and none of them announced itself: the
/// volume was written, no exception was raised, and files read back as zeros or
/// as other files' bytes.</para>
///
/// <para>They only appear a few megabytes in. Every other fixture here is
/// kilobytes, where $MFTMirr sits past everything and a run never reaches it.</para>
/// </remarks>
[TestFixture]
[Category("Slow")]
public class NtfsVolumeCapacityTests {

  /// <summary>A large file, some mid-sized ones, and some below a cluster.</summary>
  private static (List<ArchiveInputInfo> Inputs, Dictionary<string, byte[]> Expected) Mixed(int totalBytes) {
    var expected = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
    var inputs = new List<ArchiveInputInfo>();

    void Add(string name, int length, int seed) {
      var data = new byte[length];
      for (var i = 0; i < length; ++i) data[i] = (byte)(i * 31 + seed * 7 + (i >> 11));
      expected[name] = data;
      inputs.Add(ArchiveInputInfo.InMemory(name, data));
    }

    Add("BIG00001.BIN", totalBytes / 2, 1);
    var rest = totalBytes - totalBytes / 2;
    var perFile = rest / 32;
    for (var i = 0; i < 32; ++i) {
      var length = Math.Max(1, perFile + (i % 7) * 1024 - 3 * 1024);
      if (i % 11 == 0) length = 17 + i;      // smaller than a cluster
      Add($"F{i:D4}.BIN", length, i + 2);
    }
    return (inputs, expected);
  }

  private static void AssertReadsBack(byte[] image, Dictionary<string, byte[]> expected, string what) {
    using var stream = new MemoryStream(image);
    var reader = new FileSystem.Ntfs.NtfsReader(stream);
    foreach (var (name, want) in expected) {
      var entry = reader.Entries.FirstOrDefault(e =>
        !e.IsDirectory && string.Equals(Path.GetFileName(e.Name), name, StringComparison.OrdinalIgnoreCase));
      Assert.That(entry, Is.Not.Null, $"{what}: {name} is missing");
      Assert.That(reader.Extract(entry!), Is.EqualTo(want), $"{what}: {name} did not read back byte for byte");
    }
  }

  /// <summary>
  /// A run must not be laid over $MFTMirr.
  /// </summary>
  /// <remarks>
  /// The mirror sits at the volume's midpoint and the guard that steps runs over
  /// it asked whether the run <em>started below</em> the mirror and reached it.
  /// A run starting inside the mirror — or exactly on its first cluster — passed
  /// that test and was written straight over it. The file then read back as MFT
  /// records: its first bytes were the ASCII "FILE" every record begins with.
  /// </remarks>
  [Test, Category("Regression")]
  [TestCase(8 * 1024 * 1024)]
  [TestCase(16 * 1024 * 1024)]
  public void FilesAreNotWrittenOverTheMftMirror(int totalBytes) {
    var (inputs, expected) = Mixed(totalBytes);
    var w = new FileSystem.Ntfs.NtfsWriter();
    foreach (var i in inputs) w.AddFile(i.ArchiveName, i.InMemoryContent!);
    var image = w.BuildAutoSized();

    foreach (var (name, want) in expected) {
      using var stream = new MemoryStream(image);
      var reader = new FileSystem.Ntfs.NtfsReader(stream);
      var entry = reader.Entries.FirstOrDefault(e =>
        !e.IsDirectory && string.Equals(Path.GetFileName(e.Name), name, StringComparison.OrdinalIgnoreCase));
      if (entry is null) continue;
      var got = reader.Extract(entry);
      if (got.Length >= 4)
        Assert.That(System.Text.Encoding.ASCII.GetString(got, 0, 4), Is.Not.EqualTo("FILE"),
          $"{name} begins with an MFT record signature, so its run was laid over $MFTMirr");
      Assert.That(got, Is.EqualTo(want), $"{name} did not read back byte for byte");
    }
  }

  /// <summary>
  /// A volume too small for its files must be refused, not written.
  /// </summary>
  /// <remarks>
  /// Runs placed past the end of the volume were not written and not complained
  /// about. The file kept its length in the MFT record and read back as zeros,
  /// which is data loss wearing the appearance of a successful write. FAT has
  /// always refused such a volume and said by how much it was short.
  /// </remarks>
  [Test, Category("Regression")]
  public void AVolumeTooSmallForItsFilesIsRefused() {
    var (inputs, _) = Mixed(8 * 1024 * 1024);
    var w = new FileSystem.Ntfs.NtfsWriter();
    foreach (var i in inputs) w.AddFile(i.ArchiveName, i.InMemoryContent!);

    // Two megabytes cannot hold eight, and saying so is the only correct answer.
    using var target = new MemoryStream();
    var thrown = Assert.Catch<InvalidOperationException>(
      () => w.BuildToStreaming(target, 2 * 1024 * 1024));
    Assert.That(thrown!.Message, Does.Contain("clusters"),
      "the refusal should say how many clusters were needed against how many there are");
  }

  /// <summary>
  /// An auto-sized volume must be large enough for the layout that follows it.
  /// </summary>
  /// <remarks>
  /// The estimate models the volume's overhead and adds a tenth for slack, but
  /// not what the layout strands when a run is pushed wholly past $MFTMirr —
  /// which depends on the order the files arrive in. It came out a couple of per
  /// cent short, and a couple of per cent short is a volume that cannot be
  /// filled.
  /// </remarks>
  [Test, Category("Regression")]
  [TestCase(8 * 1024 * 1024)]
  [TestCase(16 * 1024 * 1024)]
  public void AutoSizedVolumeHoldsEverythingItWasGiven(int totalBytes) {
    var (inputs, expected) = Mixed(totalBytes);
    var w = new FileSystem.Ntfs.NtfsWriter();
    foreach (var i in inputs) w.AddFile(i.ArchiveName, i.InMemoryContent!);
    AssertReadsBack(w.BuildAutoSized(), expected, "auto-sized");
  }

  /// <summary>
  /// Defragmenting must not lose a file, whichever way it packs them.
  /// </summary>
  /// <remarks>
  /// The rebuild path — which is what runs whenever a file is fragmented enough
  /// that the in-place planner declines — streamed entries in the order the
  /// reader found them. That packed badly enough to need half again the room,
  /// and the writer then had files it could not place. Consolidating at the end
  /// already spilled and sorted, which is why only two of the three modes lost
  /// anything and the fault looked like a defrag bug rather than a layout one.
  /// </remarks>
  [Test, Category("Regression")]
  [TestCase(DefragMode.ConsolidateAtStart)]
  [TestCase(DefragMode.ConsolidateAtEnd)]
  [TestCase(DefragMode.FillHolesLazy)]
  public void DefragmentKeepsEveryFile(DefragMode mode) {
    Compression.Lib.FormatRegistration.EnsureInitialized();
    var ops = FormatRegistry.GetArchiveOps("Ntfs")!;
    var (inputs, expected) = Mixed(8 * 1024 * 1024);

    using var image = new MemoryStream();
    ((IArchiveCreatable)ops).Create(image, inputs, new FormatCreateOptions());

    // Fragment it first: a freshly created volume has nothing out of place, and
    // the in-place planner handles it without ever reaching the rebuild.
    var doomed = expected.Keys.Where(k => k.StartsWith('F')).Where((_, n) => n % 3 == 1).Take(12).ToArray();
    image.Position = 0;
    ((IArchiveModifiable)ops).Remove(image, doomed);
    foreach (var d in doomed) expected.Remove(d);

    var added = new List<ArchiveInputInfo>();
    for (var i = 0; i < 6; ++i) {
      var data = new byte[40 * 1024 + i * 977];
      for (var j = 0; j < data.Length; ++j) data[j] = (byte)(j * 31 + (900 + i) * 7);
      expected[$"ADD{i:D2}.BIN"] = data;
      added.Add(ArchiveInputInfo.InMemory($"ADD{i:D2}.BIN", data));
    }
    image.Position = 0;
    ((IArchiveModifiable)ops).Add(image, added);

    image.Position = 0;
    ((IArchiveDefragmentable)ops).Defragment(image, new DefragOptions { Mode = mode });

    AssertReadsBack(image.ToArray(), expected, mode.ToString());
  }
}
