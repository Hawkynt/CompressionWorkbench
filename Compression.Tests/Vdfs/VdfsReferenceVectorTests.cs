using System.Reflection;
using FileSystem.Vdfs;

namespace Compression.Tests.Vdfs;

/// <summary>
/// VDF volumes written by software that is not ours, and ZenKit's reading of volumes that
/// are. Provenance of every file is in <c>Vdfs/ReferenceVectors/README.md</c>.
/// </summary>
[TestFixture]
public class VdfsReferenceVectorTests {

  private static byte[] Vector(string name) {
    using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"VdfsVectors.{name}")
      ?? throw new InvalidOperationException($"reference vector {name} is not embedded");
    using var copy = new MemoryStream();
    stream.CopyTo(copy);
    return copy.ToArray();
  }

  /// <summary>The listing format the oracle prints: <c>D path</c> and <c>F path size fnv1a64</c>.</summary>
  private static SortedSet<string> Manifest(string tsvName)
    => new(System.Text.Encoding.UTF8.GetString(Vector(tsvName))
      .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), StringComparer.Ordinal);

  private static SortedSet<string> ManifestOf(byte[] volume) {
    using var r = new VdfsReader(new MemoryStream(volume));
    Assert.That(r.IsPartial, Is.False);
    return new(r.Entries.Select(e => e.IsDirectory
      ? $"D\t{e.Name}"
      : FileLine(e.Name, r.Extract(e))), StringComparer.Ordinal);
  }

  private static SortedSet<string> ManifestOf(IEnumerable<(string Path, byte[] Data)> tree) {
    var lines = new SortedSet<string>(StringComparer.Ordinal);
    foreach (var (path, data) in tree) {
      lines.Add(FileLine(path, data));
      var parts = path.Split('/');
      for (var i = 1; i < parts.Length; ++i) lines.Add("D\t" + string.Join('/', parts[..i]));
    }
    return lines;
  }

  private static string FileLine(string path, byte[] data) => $"F\t{path}\t{data.Length}\t{Fnv1a64(data):x16}";

  private static ulong Fnv1a64(byte[] data) {
    var hash = 14695981039346656037UL;
    foreach (var b in data) { hash ^= b; hash *= 1099511628211UL; }
    return hash;
  }

  private static byte[] Pattern(int size, int seed) {
    var data = new byte[size];
    for (var i = 0; i < size; ++i) data[i] = (byte)((seed + i * 7 + i / 256) & 0xFF);
    return data;
  }

  /// <summary>
  /// The tree packed into the <c>zenkit_*</c> and <c>cwb_*</c> volumes: ZenKit's own
  /// compressed-save test set, plus an empty file.
  /// </summary>
  internal static IEnumerable<(string Path, byte[] Data)> SampleTree() {
    yield return ("README.TXT", Pattern(128, 0x41));
    yield return ("ONEBLOCK.BIN", Pattern(8192, 0x10));
    yield return ("SCRIPTS/STARTUP.D", Pattern(25000, 0x55));
    var world = new byte[50000];
    Array.Fill(world, (byte)0xAB);
    yield return ("MESHES/WORLD.MRM", world);
    yield return ("SOUND/THEME.WAV", Pattern(4096, 0xCC));
    yield return ("SOUND/VOICE.OGG", Pattern(2048, 0xDD));
    yield return ("TEXTURES/_COMPILED/STONE.TEX", Pattern(16384, 0x77));
    yield return ("EMPTY.TXT", []);
  }

  /// <summary>
  /// <see cref="SampleTree" /> written, then edited in place and defragmented: one file
  /// removed, one added in a new directory (growing the catalog into the first payloads),
  /// one replaced by a shorter one, then the gaps closed.
  /// </summary>
  internal static byte[] EditedVolume(bool zipped) {
    var w = new VdfsWriter { Zipped = zipped, Timestamp = new DateTime(2020, 9, 13, 12, 26, 40), Comment = "CompressionWorkbench reference volume" };
    foreach (var (path, data) in SampleTree()) w.AddFile(path, data);
    using var volume = new MemoryStream();
    volume.Write(w.Build());

    VdfsInPlaceModifier.RemoveFile(volume, "SCRIPTS/STARTUP.D");
    VdfsInPlaceModifier.AddFile(volume, "NEW/ADDED.BIN", Pattern(30000, 0x99));
    VdfsInPlaceModifier.ReplaceFile(volume, "README.TXT", Pattern(40, 0x42));
    new VdfsFormatDescriptor().Defragment(volume, new Compression.Registry.DefragOptions { Mode = Compression.Registry.DefragMode.ConsolidateAtStart });
    return volume.ToArray();
  }

  /// <summary>What <see cref="EditedVolume" /> holds.</summary>
  internal static IEnumerable<(string Path, byte[] Data)> EditedTree()
    => SampleTree()
      .Where(f => f.Path != "SCRIPTS/STARTUP.D")
      .Select(f => f.Path == "README.TXT" ? (f.Path, Pattern(40, 0x42)) : f)
      .Append(("NEW/ADDED.BIN", Pattern(30000, 0x99)));

  // ── Volumes written by the reference tools ──────────────────────────────

  [Test, Category("ThemVsUs")]
  public void GothicVdfsVolume_ReadsAsZenKitReadsIt() {
    // Given basic.vdf, a stored volume GothicVDFS wrote
    // When we list and decode it, Then we see exactly what ZenKit sees
    Assert.That(ManifestOf(Vector("basic.vdf")), Is.EqualTo(Manifest("basic.zenkit.tsv")));
  }

  [Test, Category("ThemVsUs")]
  public void VdfsToolZippedVolume_ReadsAsZenKitReadsIt() {
    // Given basic_zipped.vdf, the same files zipped by VDFSTool (quarter-file blocks, zlib level 9)
    // When we inflate it, Then we see exactly what ZenKit sees
    Assert.That(ManifestOf(Vector("basic_zipped.vdf")), Is.EqualTo(Manifest("basic_zipped.zenkit.tsv")));
  }

  [Test, Category("ThemVsUs")]
  public void VdfsToolZippedVolume_InflatesToTheStoredVolumesBytes() {
    // The two volumes hold the same files: one stored, one zipped by a different tool
    Assert.That(ManifestOf(Vector("basic_zipped.vdf")), Is.EqualTo(ManifestOf(Vector("basic.vdf"))));
    using var r = new VdfsReader(new MemoryStream(Vector("basic_zipped.vdf")));
    Assert.That(r.IsZippedVolume, Is.True);
    Assert.That(r.Entries.Where(e => !e.IsDirectory).All(e => e.IsZipped), Is.True);
  }

  [Test, Category("ThemVsUs")]
  public void Writer_ReproducesTheGothicVdfsVolume_ByteForByte() {
    // Given the files, comment and timestamp of basic.vdf
    var original = Vector("basic.vdf");
    using var r = new VdfsReader(new MemoryStream(original));
    var w = new VdfsWriter { Comment = r.Comment, Timestamp = r.Timestamp!.Value };
    foreach (var e in r.Entries.Where(e => !e.IsDirectory)) w.AddFile(e.Name, r.Extract(e));

    // When we write them as a stored volume, Then it is GothicVDFS's volume, byte for byte:
    // same catalog order, same last flags, same attributes, same payload order
    Assert.That(w.Build(), Is.EqualTo(original));
  }

  [TestCase("zenkit_stored.vdf", false)]
  [TestCase("zenkit_zipped.vdf", true)]
  [Category("ThemVsUs")]
  public void ZenKitVolumes_ReadBackToTheTreeZenKitPacked(string name, bool zipped) {
    // Given a volume ZenKit's save / save_compressed wrote from SampleTree
    var volume = Vector(name);
    using (var r = new VdfsReader(new MemoryStream(volume))) Assert.That(r.IsZippedVolume, Is.EqualTo(zipped));

    // Then every file decodes to what went in. ZenKit's mount_host skips empty host files,
    // so EMPTY.TXT never reached its writer.
    Assert.That(ManifestOf(volume), Is.EqualTo(ManifestOf(SampleTree().Where(f => f.Data.Length > 0))));
  }

  [TestCase("cwb_stored.vdf")]
  [TestCase("cwb_zipped.vdf")]
  [Category("Regression")]
  public void OurWriter_PointsEmptyFilesAtTheEndOfTheVolume(string name) {
    // ZenKit takes a zero catalog size for "unknown" and reads a zipped volume's empty file
    // as everything after its offset: pointing it at the next payload decoded that payload
    // (8192 bytes of ONEBLOCK.BIN), a 12-byte empty header decoded as the rest of the volume.
    // At the very end ZenKit skips the entry; a stored volume still lists it, empty.
    var volume = Vector(name);
    using var r = new VdfsReader(new MemoryStream(volume));
    var empty = r.Entries.Single(e => e.Name == "EMPTY.TXT");
    Assert.That(empty.DataOffset, Is.EqualTo(volume.Length));
    Assert.That(empty.StoredSize, Is.Zero);
    Assert.That(r.Extract(empty), Is.Empty);
  }

  // ── Volumes we wrote, as ZenKit read them ───────────────────────────────

  [TestCase("cwb_stored", true)]
  [TestCase("cwb_zipped", false)]
  [Category("ThemVsUs")]
  public void OurVolumes_ZenKitReadEveryFileBack(string name, bool zenKitListsEmptyFiles) {
    // Given ZenKit's listing of a volume our writer made from SampleTree
    // Then ZenKit decoded every file to what went in. In the zipped volume it leaves the
    // empty file out (see OurWriter_PointsEmptyFilesAtTheEndOfTheVolume) rather than
    // inventing content for it.
    var expected = SampleTree().Where(f => zenKitListsEmptyFiles || f.Data.Length > 0);
    Assert.That(Manifest($"{name}.zenkit.tsv"), Is.EqualTo(ManifestOf(expected)));
    Assert.That(ManifestOf(Vector($"{name}.vdf")), Is.EqualTo(ManifestOf(SampleTree())));
  }

  [TestCase("cwb_edited_stored", true)]
  [TestCase("cwb_edited_zipped", false)]
  [Category("ThemVsUs")]
  public void OurEditedVolumes_ZenKitReadEveryFileBack(string name, bool zenKitListsEmptyFiles) {
    // Given ZenKit's listing of a volume edited in place and defragmented
    // Then ZenKit decoded every surviving, added and replaced file, and nothing removed
    var expected = EditedTree().Where(f => zenKitListsEmptyFiles || f.Data.Length > 0);
    Assert.That(Manifest($"{name}.zenkit.tsv"), Is.EqualTo(ManifestOf(expected)));
    Assert.That(ManifestOf(Vector($"{name}.vdf")), Is.EqualTo(ManifestOf(EditedTree())));
  }

  [TestCase(false)]
  [TestCase(true)]
  [Category("Regression")]
  public void TheEditSequence_StillProducesWhatZenKitAccepted(bool zipped) {
    // The same edits today give the same tree; the stored volume, which involves no
    // deflate, gives the very bytes ZenKit read
    var edited = EditedVolume(zipped);
    Assert.That(ManifestOf(edited), Is.EqualTo(ManifestOf(EditedTree())));
    if (!zipped) Assert.That(edited, Is.EqualTo(Vector("cwb_edited_stored.vdf")));
  }

  [Test, Category("Regression")]
  public void OurStoredWriter_StillProducesTheVolumeZenKitAccepted() {
    // A stored volume involves no deflate, so the writer's output is fixed: if it ever
    // changes, ZenKit's acceptance above no longer speaks for it
    var w = new VdfsWriter { Timestamp = new DateTime(2020, 9, 13, 12, 26, 40), Comment = "CompressionWorkbench reference volume" };
    foreach (var (path, data) in SampleTree()) w.AddFile(path, data);
    Assert.That(w.Build(), Is.EqualTo(Vector("cwb_stored.vdf")));
  }

  [Test, Category("Regression")]
  public void OurZippedWriter_StillLaysOutTheVolumeZenKitAccepted() {
    // The deflate bytes may improve; the catalog, the raw sound files and the stream
    // framing must not drift from what ZenKit accepted
    var w = new VdfsWriter { Zipped = true, Timestamp = new DateTime(2020, 9, 13, 12, 26, 40), Comment = "CompressionWorkbench reference volume" };
    foreach (var (path, data) in SampleTree()) w.AddFile(path, data);
    using var ours = new VdfsReader(new MemoryStream(w.Build()));
    using var accepted = new VdfsReader(new MemoryStream(Vector("cwb_zipped.vdf")));
    Assert.That(ours.Entries.Select(e => (e.Name, e.IsDirectory, e.Size, e.IsZipped)),
      Is.EqualTo(accepted.Entries.Select(e => (e.Name, e.IsDirectory, e.Size, e.IsZipped))));
    Assert.That(ManifestOf(w.Build()), Is.EqualTo(ManifestOf(SampleTree())));
  }
}
