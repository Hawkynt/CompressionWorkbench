using System.Formats.Tar;
using System.IO.Compression;
using System.Reflection;
using FileFormat.AlZip;
using FileFormat.Egg;

namespace Compression.Tests.AlZip;

/// <summary>
/// Split ALZ and EGG sets ALZip 12.38 wrote from generated inputs (ReferenceVectors/README.md):
/// joined, every member must come back as the input it was made from, whatever volume it crosses.
/// </summary>
[TestFixture]
public sealed class AlZipEggSplitTests {

  private static readonly Lazy<Dictionary<string, byte[]>> Files = new(() => {
    using var res = Assembly.GetExecutingAssembly().GetManifestResourceStream("AlZipVectors.alzip-split-sets.tar.gz")
      ?? throw new FileNotFoundException("alzip-split-sets.tar.gz is not embedded");
    using var gz = new GZipStream(res, CompressionMode.Decompress);
    using var tar = new TarReader(gz);
    var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
    while (tar.GetNextEntry() is { } e) {
      if (e.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile)) continue;
      using var ms = new MemoryStream();
      e.DataStream?.CopyTo(ms);
      files[e.Name.TrimStart('.', '/')] = ms.ToArray();
    }
    return files;
  });

  private static readonly string[] Inputs = ["text.txt", "sub/table.csv", "small.txt"];

  private static List<byte[]> AlzVolumes(int method) {
    var list = new List<byte[]> { Files.Value[$"split-m{method}.alz"] };
    for (var i = 0; Files.Value.TryGetValue($"split-m{method}.a{i:00}", out var v); ++i) list.Add(v);
    return list;
  }

  private static List<byte[]> EggVolumeList(int method) {
    var list = new List<byte[]>();
    for (var i = 1; Files.Value.TryGetValue($"split-m{method}.vol{i}.egg", out var v); ++i) list.Add(v);
    return list;
  }

  [TestCase(0, 4)]
  [TestCase(1, 2)]
  [TestCase(2, 2)]
  public void GivenAnAlzSplitSet_WhenJoined_ThenEveryMemberMatchesItsInput(int method, int volumes) {
    var set = AlzVolumes(method);
    Assert.That(set, Has.Count.EqualTo(volumes));
    Assert.That(AlZipVolumes.ContinuesInNextVolume(set[0]), Is.True);
    using var r = new AlZipReader(new MemoryStream(AlZipVolumes.Join(set)));
    Assert.That(r.IsSplit, Is.False);
    var files = r.Entries.Where(e => !e.IsDirectory).ToList();
    Assert.That(files.Select(e => e.FileName), Is.EquivalentTo(Inputs));
    foreach (var e in files) Assert.That(r.Extract(e), Is.EqualTo(Files.Value[e.FileName]), e.FileName);
  }

  [TestCase(0, 4)]
  [TestCase(1, 2)]
  [TestCase(2, 2)]
  [TestCase(3, 2)]
  [TestCase(4, 2)]
  public void GivenAnEggSplitSet_WhenJoined_ThenEveryMemberMatchesItsInput(int method, int volumes) {
    var set = EggVolumeList(method);
    Assert.That(set, Has.Count.EqualTo(volumes));
    using var r = EggReader.FromJoinedVolumes(EggVolumes.Join(set));
    var files = r.Entries.Where(e => !e.IsDirectory).ToList();
    Assert.That(files.Select(e => e.Name), Is.EquivalentTo(Inputs));
    foreach (var e in files) Assert.That(r.Extract(e), Is.EqualTo(Files.Value[e.Name]), e.Name);
  }

  [Test]
  public void GivenOneEggVolume_WhenRead_ThenExtractionIsRefusedNotGuessed() {
    using var r = new EggReader(new MemoryStream(EggVolumeList(1)[0]));
    Assert.That(r.IsSplit, Is.True);
    Assert.Throws<NotSupportedException>(() => r.Extract(r.Entries.First(e => !e.IsDirectory && e.UncompressedSize > 0)));
  }

  [Test]
  public void GivenAnAlzSetMissingItsLastVolume_WhenJoined_ThenItIsRefused() =>
    Assert.Throws<InvalidDataException>(() => AlZipVolumes.Join(AlzVolumes(0)[..^1]));

  [Test]
  public void GivenAlzVolumesOutOfOrder_WhenJoined_ThenItIsRefused() {
    var set = AlzVolumes(0);
    (set[1], set[2]) = (set[2], set[1]);
    Assert.Throws<InvalidDataException>(() => AlZipVolumes.Join(set));
  }

  [Test]
  public void GivenAnEggSetMissingItsLastVolume_WhenJoined_ThenItIsRefused() =>
    Assert.Throws<InvalidDataException>(() => EggVolumes.Join(EggVolumeList(0)[..^1]));

  [Test]
  public void GivenEggVolumesOutOfOrder_WhenJoined_ThenItIsRefused() {
    var set = EggVolumeList(0);
    (set[1], set[2]) = (set[2], set[1]);
    Assert.Throws<InvalidDataException>(() => EggVolumes.Join(set));
  }

  /// <summary>The descriptors find the other volumes beside whichever volume they are given.</summary>
  [TestCase("split-m2.alz")]
  [TestCase("split-m0.a01")]
  [TestCase("split-m3.vol1.egg")]
  [TestCase("split-m0.vol3.egg")]
  public void GivenAVolumeOnDisk_WhenExtractedThroughTheDescriptor_ThenTheWholeSetIsRead(string volume) {
    var dir = Path.Combine(Path.GetTempPath(), "cwb-split-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    try {
      foreach (var (name, data) in Files.Value.Where(f => f.Key.StartsWith("split-", StringComparison.Ordinal)))
        File.WriteAllBytes(Path.Combine(dir, name), data);
      var output = Path.Combine(dir, "out");
      using (var fs = File.OpenRead(Path.Combine(dir, volume))) {
        if (volume.EndsWith(".egg", StringComparison.Ordinal)) new EggFormatDescriptor().Extract(fs, output, null, null);
        else new AlZipFormatDescriptor().Extract(fs, output, null, null);
      }
      foreach (var input in Inputs)
        Assert.That(File.ReadAllBytes(Path.Combine(output, input.Replace('/', Path.DirectorySeparatorChar))), Is.EqualTo(Files.Value[input]), input);
    } finally {
      try { Directory.Delete(dir, true); } catch { /* best effort */ }
    }
  }
}
