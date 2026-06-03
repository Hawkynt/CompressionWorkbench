using System.Buffers.Binary;
using Compression.Registry;
using FileSystem.Apfs;

namespace Compression.Tests.Apfs;

/// <summary>
/// APFS in-place leaf mutation tests. The descriptor implements
/// <see cref="IArchiveModifiable"/> via <c>ApfsModifier</c>, which:
/// (1) advances the transaction id, (2) recomputes per-block Fletcher-64 on every
/// touched block, (3) inserts / deletes records into the existing FS-tree leaf when
/// they still fit, and (4) throws a documented <see cref="NotSupportedException"/>
/// when a tree split would be required. These tests pin all three behaviours and the
/// resulting round-trip through <see cref="ApfsReader"/>.
/// </summary>
[TestFixture]
public class ApfsModifyTests {

  private const int SmallImage = 4 * 1024 * 1024;

  private static MemoryStream BuildImage(params (string Name, byte[] Data)[] files) {
    var w = new ApfsWriter();
    w.SetMinImageSize(SmallImage);
    foreach (var (n, d) in files) w.AddFile(n, d);
    var ms = new MemoryStream();
    ms.Write(w.Build());
    ms.Position = 0;
    return ms;
  }

  private static Dictionary<string, byte[]> ReadAll(MemoryStream image) {
    image.Position = 0;
    var r = new ApfsReader(image, leaveOpen: true);
    return r.Entries.Where(e => !e.IsDirectory)
                    .ToDictionary(e => e.Name.Replace('\\', '/'), e => r.Extract(e));
  }

  // ── Given / When / Then: add a small file ─────────────────────────────

  /// <summary>
  /// Given an image with three small files, when a fourth small file is added,
  /// then all four files round-trip through the reader with byte-exact content.
  /// </summary>
  [Test, Category("RoundTrip")]
  public void Add_SmallFile_NoSplit_RoundTrips() {
    using var img = BuildImage(
      ("alpha.bin", "alpha"u8.ToArray()),
      ("beta.bin", "beta-content"u8.ToArray()),
      ("gamma.bin", "gamma-data"u8.ToArray()));

    var addPayload = "delta-payload"u8.ToArray();
    ((IArchiveModifiable)new ApfsFormatDescriptor()).Add(img,
      [ArchiveInputInfo.InMemory("delta.bin", addPayload)]);

    var files = ReadAll(img);
    Assert.That(files, Has.Count.EqualTo(4));
    Assert.That(files["alpha.bin"], Is.EqualTo("alpha"u8.ToArray()));
    Assert.That(files["beta.bin"], Is.EqualTo("beta-content"u8.ToArray()));
    Assert.That(files["gamma.bin"], Is.EqualTo("gamma-data"u8.ToArray()));
    Assert.That(files["delta.bin"], Is.EqualTo(addPayload));
  }

  // ── Given / When / Then: remove a single file ─────────────────────────

  /// <summary>
  /// Given an image with four files, when one is removed in place, then the reader
  /// sees three files with byte-exact content for the survivors and the dropped
  /// file's data blocks are zeroed (no forensic recovery).
  /// </summary>
  [Test, Category("RoundTrip")]
  public void Remove_SingleFile_NoSplit_RoundTrips() {
    var dropPayload = "this-must-be-wiped"u8.ToArray();
    using var img = BuildImage(
      ("keep1.bin", "k1"u8.ToArray()),
      ("drop.bin", dropPayload),
      ("keep2.bin", "k2-bytes"u8.ToArray()),
      ("keep3.bin", "k3-content"u8.ToArray()));

    ((IArchiveModifiable)new ApfsFormatDescriptor()).Remove(img, ["drop.bin"]);

    var files = ReadAll(img);
    Assert.That(files, Has.Count.EqualTo(3));
    Assert.That(files.ContainsKey("drop.bin"), Is.False);
    Assert.That(files["keep1.bin"], Is.EqualTo("k1"u8.ToArray()));
    Assert.That(files["keep2.bin"], Is.EqualTo("k2-bytes"u8.ToArray()));
    Assert.That(files["keep3.bin"], Is.EqualTo("k3-content"u8.ToArray()));

    // No forensic recovery: the wiped payload's bytes must not appear in the image.
    img.Position = 0;
    var raw = img.ToArray();
    Assert.That(System.Text.Encoding.UTF8.GetString(raw), Does.Not.Contain("this-must-be-wiped"),
      "Removed file's plaintext must be wiped from the image.");
  }

  // ── Given / When / Then: leaf full → split succeeds ───────────────────

  /// <summary>
  /// Given an image whose FS-tree leaf is packed near capacity with many small files,
  /// when an additional file is added that would overflow the leaf, then the modifier
  /// now performs a top-down B-tree split rather than throwing: it rebuilds the tree
  /// with the extra leaf and adds an internal index node, and the survivor record set
  /// (including the new file) round-trips through the reader.
  /// </summary>
  [Test, Category("RoundTrip")]
  public void Add_RequiresSplit_NowSucceeds() {
    var initial = new List<(string, byte[])>();
    for (var i = 0; i < 100; i++)
      initial.Add(($"file_with_a_reasonably_long_name_to_stress_packing_{i:000}.dat",
        new byte[16]));
    using var img = BuildImage([.. initial]);

    ((IArchiveModifiable)new ApfsFormatDescriptor()).Add(img,
      [ArchiveInputInfo.InMemory("the_final_straw_that_breaks_the_leaf.dat",
        new byte[16])]);

    var files = ReadAll(img);
    Assert.That(files, Has.Count.EqualTo(101));
    Assert.That(files.ContainsKey("the_final_straw_that_breaks_the_leaf.dat"), Is.True);
  }

  // ── Spec-level invariants ────────────────────────────────────────────

  /// <summary>
  /// Given a freshly built image, when a file is added in place, then the FS-tree
  /// leaf, NXSB, NXSB copy, APSB, and checkpoint map blocks all carry valid
  /// Fletcher-64 checksums, the nx_next_xid advances strictly past the original xid,
  /// and the FS leaf's o_xid reflects the new transaction.
  /// </summary>
  [Test, Category("Spec")]
  public void Add_AdvancesXidAndRestampsTouchedBlocks() {
    using var img = BuildImage(("first.bin", "f1"u8.ToArray()));
    var originalBytes = img.ToArray();
    var originalNextXid = BinaryPrimitives.ReadUInt64LittleEndian(originalBytes.AsSpan(96, 8));

    ((IArchiveModifiable)new ApfsFormatDescriptor()).Add(img,
      [ArchiveInputInfo.InMemory("second.bin", "s2"u8.ToArray())]);

    var mutated = img.ToArray();
    var nx = mutated.AsSpan(0, 4096);
    var nxCopy = mutated.AsSpan(2 * 4096, 4096);
    var apsb = mutated.AsSpan(5 * 4096, 4096);
    var fsLeaf = mutated.AsSpan(8 * 4096, 4096);
    var chkMap = mutated.AsSpan(1 * 4096, 4096);

    Assert.That(ApfsFletcher64.Verify(nx), Is.True, "NXSB Fletcher-64 must verify after mutation.");
    Assert.That(ApfsFletcher64.Verify(nxCopy), Is.True, "NXSB copy Fletcher-64 must verify.");
    Assert.That(ApfsFletcher64.Verify(apsb), Is.True, "APSB Fletcher-64 must verify.");
    Assert.That(ApfsFletcher64.Verify(fsLeaf), Is.True, "FS-tree leaf Fletcher-64 must verify.");
    Assert.That(ApfsFletcher64.Verify(chkMap), Is.True, "Checkpoint map Fletcher-64 must verify.");

    var newNextXid = BinaryPrimitives.ReadUInt64LittleEndian(nx[96..]);
    Assert.That(newNextXid, Is.GreaterThan(originalNextXid),
      "nx_next_xid must strictly increase per mutation.");
    var fsLeafXid = BinaryPrimitives.ReadUInt64LittleEndian(fsLeaf[16..]);
    Assert.That(fsLeafXid, Is.GreaterThanOrEqualTo(originalNextXid),
      "Modified FS-tree leaf's o_xid must reflect the new transaction.");
  }

  /// <summary>
  /// Given an image with two files, when one is added and then removed, then the
  /// reader sees the original two and the image is structurally valid (all touched
  /// blocks still carry valid Fletcher-64 checksums).
  /// </summary>
  [Test, Category("RoundTrip")]
  public void AddThenRemove_RestoresLogicalState() {
    using var img = BuildImage(
      ("a.txt", "AAA"u8.ToArray()),
      ("b.txt", "BBB"u8.ToArray()));

    var desc = (IArchiveModifiable)new ApfsFormatDescriptor();
    desc.Add(img, [ArchiveInputInfo.InMemory("c.txt", "CCC"u8.ToArray())]);
    desc.Remove(img, ["c.txt"]);

    var files = ReadAll(img);
    Assert.That(files, Has.Count.EqualTo(2));
    Assert.That(files["a.txt"], Is.EqualTo("AAA"u8.ToArray()));
    Assert.That(files["b.txt"], Is.EqualTo("BBB"u8.ToArray()));

    var bytes = img.ToArray();
    Assert.That(ApfsFletcher64.Verify(bytes.AsSpan(0, 4096)), Is.True);
    Assert.That(ApfsFletcher64.Verify(bytes.AsSpan(8 * 4096, 4096)), Is.True);
  }
}
