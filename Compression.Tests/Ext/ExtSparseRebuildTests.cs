#pragma warning disable CS1591
using Compression.Registry;
using FileSystem.Ext;

namespace Compression.Tests.Ext;

/// <summary>
/// Asking for holes has to give back a smaller image and the same files.
/// </summary>
/// <remarks>
/// <para>ext records a block a file does not occupy as a zero pointer in its
/// block map, and a reader hands back zeros for it. So a rebuild that notices
/// which blocks hold nothing but zeros can decline to allocate them and size the
/// volume for what is left — the file keeps its length and every byte of it, and
/// the volume is smaller by whatever the holes would have cost.</para>
///
/// <para>Two things have to hold together, and either alone is worthless: the
/// image has to actually shrink, and the files have to come back exactly as they
/// went in. A rebuild that shrinks by dropping data passes the first and fails
/// the second; one that changes nothing passes the second and fails the first.
/// </para>
/// </remarks>
[TestFixture]
public class ExtSparseRebuildTests {

  /// <summary>Files whose bulk is zeros, plus one that is solid throughout.</summary>
  private static Dictionary<string, byte[]> Contents() {
    var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);

    byte[] Make(int length, params (int At, int Run)[] solid) {
      var data = new byte[length];
      foreach (var (at, run) in solid)
        for (var i = at; i < Math.Min(length, at + run); ++i)
          data[i] = (byte)(i * 31 + 7 + (i >> 9));
      return data;
    }

    // Large enough that the volume is sized by its contents rather than by the
    // floor an auto-sized ext image never goes below — at four megabytes the
    // floor is the same either way and there is nothing to measure.
    for (var i = 0; i < 6; ++i)
      files[$"HOLEY{i}.BIN"] = Make(4_000_000, (0, 8_192), (3_980_000, 8_192));
    files["SOLID.BIN"] = Make(500_000, (0, 500_000));
    return files;
  }

  private static byte[] Build(Dictionary<string, byte[]> files, bool sparse, bool dedup = false) {
    var writer = new ExtWriter { MakeSparse = sparse, DeduplicateWithLinks = dedup };
    foreach (var (name, data) in files) writer.AddFile(name, data);

    using var image = new MemoryStream();
    writer.BuildToStreamingAutoSized(image, ExtWriter.ExtVersion.Ext2, journal: false,
      volumeLabel: null!, inodeSize: 128);
    return image.ToArray();
  }

  private static void AssertReadsBack(byte[] image, Dictionary<string, byte[]> expected, string what) {
    using var stream = new MemoryStream(image);
    var reader = new ExtReader(stream);
    foreach (var (name, want) in expected) {
      var entry = reader.Entries.FirstOrDefault(e =>
        !e.IsDirectory && string.Equals(Path.GetFileName(e.Name), name, StringComparison.Ordinal));
      Assert.That(entry, Is.Not.Null, $"{what}: '{name}' is not on the volume");

      var got = reader.Extract(entry!);
      Assert.That(got.Length, Is.EqualTo(want.Length), $"{what}: '{name}' changed length");
      Assert.That(got, Is.EqualTo(want).AsCollection, $"{what}: '{name}' changed contents");
    }
  }

  [Test, Category("Regression")]
  public void AskingForHoles_ShrinksTheImageAndKeepsEveryByte() {
    var files = Contents();

    var solid = Build(files, sparse: false);
    var holey = Build(files, sparse: true);

    // Both have to be volumes anyone can read, holes or no holes.
    AssertReadsBack(solid, files, "without holes");
    AssertReadsBack(holey, files, "with holes");

    // About 24 MB of the 24.5 MB written is zeros, so the saving should be most
    // of it rather than a rounding difference.
    Assert.That(holey.Length, Is.LessThan(solid.Length / 2),
      $"asking for holes saved {solid.Length - holey.Length} of {solid.Length} bytes, "
      + "which is not the bulk of a file set that is mostly zeros");
  }

  [Test, Category("Regression")]
  public void WithoutAsking_NothingChanges() {
    // The switch is a switch: a volume built without it must be allocated solid,
    // because a hole is a promise about what a reader will be given and not
    // everything that reads ext is this project.
    var files = Contents();
    var a = Build(files, sparse: false);
    var b = Build(files, sparse: false);

    Assert.That(b.Length, Is.EqualTo(a.Length));
    AssertReadsBack(a, files, "default");
  }

  [Test, Category("Regression")]
  public void TheRebuildHonoursTheSwitch() {
    var files = Contents();
    var source = Build(files, sparse: false);

    var descriptor = new ExtFormatDescriptor();
    Assert.That(descriptor.ReclaimSupport.HasFlag(LayoutReclaim.Sparse), Is.True,
      "ext can express a hole, and should say so");

    using var input = new MemoryStream(source);
    using var output = new MemoryStream();
    descriptor.RebuildStreaming(input, output, new LayoutRebuildOptions { MakeSparse = true });

    var rebuilt = output.ToArray();
    AssertReadsBack(rebuilt, files, "after the rebuild");
    Assert.That(rebuilt.Length, Is.LessThan(source.Length),
      "a rebuild asked for holes should give back a smaller volume");
  }

  /// <summary>Several names, one copy: identical files stored once.</summary>
  private static Dictionary<string, byte[]> Repeated() {
    // Past the floor an auto-sized ext image never goes below, so what is measured
    // is the saving and not the minimum.
    var body = new byte[3_000_000];
    for (var i = 0; i < body.Length; ++i) body[i] = (byte)(i * 17 + 3);

    var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
    for (var i = 0; i < 5; ++i) files[$"COPY{i}.BIN"] = (byte[])body.Clone();

    var other = new byte[3_000_000];
    for (var i = 0; i < other.Length; ++i) other[i] = (byte)(i * 29 + 11);
    files["OTHER.BIN"] = other;
    return files;
  }

  [Test, Category("Regression")]
  public void AskingForLinks_StoresOneCopyAndKeepsEveryName() {
    var files = Repeated();

    var copies = Build(files, sparse: false);
    var linked = Build(files, sparse: false, dedup: true);

    AssertReadsBack(copies, files, "without links");
    AssertReadsBack(linked, files, "with links");

    // Five of the six files are the same three megabytes, so four copies of it
    // should stop being paid for.
    Assert.That(linked.Length, Is.LessThan(copies.Length / 2),
      $"linking saved {copies.Length - linked.Length} of {copies.Length} bytes, which is not "
      + "the four copies that stopped being stored");
  }

  [Test, Category("Regression")]
  public void WithoutAskingForLinks_EachNameKeepsItsOwnCopy() {
    // A hard link is shared storage, and writing through one name is seen through
    // the others. That is a change in meaning, so it only happens when asked for.
    var files = Repeated();
    var plain = Build(files, sparse: false);
    var linked = Build(files, sparse: false, dedup: true);

    Assert.That(plain.Length, Is.GreaterThan(linked.Length));
    AssertReadsBack(plain, files, "default");
  }

  [Test, Category("Regression")]
  public void TheRebuildHonoursBothSwitches() {
    var files = Repeated();
    var source = Build(files, sparse: false);

    var descriptor = new ExtFormatDescriptor();
    Assert.That(descriptor.ReclaimSupport.HasFlag(LayoutReclaim.HardLinks), Is.True,
      "ext counts the names an inode answers to, and should say so");

    using var input = new MemoryStream(source);
    using var output = new MemoryStream();
    descriptor.RebuildStreaming(input, output,
      new LayoutRebuildOptions { MakeSparse = true, DeduplicateWithLinks = true });

    var rebuilt = output.ToArray();
    AssertReadsBack(rebuilt, files, "after the rebuild");
    Assert.That(rebuilt.Length, Is.LessThan(source.Length));
  }
}
