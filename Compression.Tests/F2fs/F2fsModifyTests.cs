using System.Buffers.Binary;
using System.Text;
using Compression.Registry;
using FileSystem.F2fs;

namespace Compression.Tests.F2fs;

/// <summary>
/// F2FS is R/W via log-structured append (<see cref="F2fsModifier"/>). Add lands new
/// data + node blocks in the open WARM_DATA/WARM_NODE current segments, updates
/// on-disk NAT/SIT, mirrors the NAT update in the compact summary block's NAT journal,
/// and stamps a fresh checkpoint into the alternate pack. Remove clears the inline
/// dentry, invalidates NAT, and clears SIT valid_map bits + wipes the bytes. When
/// the NAT journal would overflow, the operation falls back honestly with
/// <see cref="NotSupportedException"/> so the caller can rebuild from scratch.
/// </summary>
[TestFixture]
public class F2fsModifyTests {

  private static MemoryStream BuildImage(params (string Name, byte[] Data)[] files) {
    var w = new F2fsWriter();
    foreach (var (n, d) in files) w.AddFile(n, d);
    var ms = new MemoryStream();
    w.WriteTo(ms);
    ms.Position = 0;
    return ms;
  }

  private static Dictionary<string, byte[]> ReadAll(MemoryStream image) {
    image.Position = 0;
    var r = new F2fsReader(image);
    return r.Entries
            .Where(e => !e.IsDirectory)
            .ToDictionary(e => e.Name.Replace('\\', '/'), e => r.Extract(e));
  }

  // ── Given ──────────────────────────────────────────────────────────────
  // a fresh F2FS image with three root-level files (small inline payloads
  // that easily fit a single block each).
  // ── When ───────────────────────────────────────────────────────────────
  // a fourth root-level file is added via IArchiveModifiable.Add.
  // ── Then ──────────────────────────────────────────────────────────────
  // the reader sees all four files at their full names with content intact.
  [Test, Category("RoundTrip")]
  public void Add_SmallFile_RoundTrips() {
    using var img = BuildImage(
      ("alpha.txt", "A"u8.ToArray()),
      ("beta.txt", "BB"u8.ToArray()),
      ("gamma.txt", "CCC"u8.ToArray()));

    ((IArchiveModifiable)new F2fsFormatDescriptor()).Add(img,
      [ArchiveInputInfo.InMemory("delta.txt", "DDDD"u8.ToArray())]);

    var files = ReadAll(img);
    Assert.Multiple(() => {
      Assert.That(files, Has.Count.EqualTo(4));
      Assert.That(files["alpha.txt"], Is.EqualTo("A"u8.ToArray()));
      Assert.That(files["beta.txt"], Is.EqualTo("BB"u8.ToArray()));
      Assert.That(files["gamma.txt"], Is.EqualTo("CCC"u8.ToArray()));
      Assert.That(files["delta.txt"], Is.EqualTo("DDDD"u8.ToArray()));
    });
  }

  // ── Given ──────────────────────────────────────────────────────────────
  // a fresh F2FS image with three root-level files.
  // ── When ───────────────────────────────────────────────────────────────
  // one file is removed via IArchiveModifiable.Remove.
  // ── Then ──────────────────────────────────────────────────────────────
  // the reader sees the remaining two with intact content, and the removed
  // file's bytes have been wiped (forensic check).
  [Test, Category("RoundTrip")]
  public void Remove_SingleFile_RoundTrips() {
    var keep1 = "keep-one"u8.ToArray();
    var dropPayload = "SECRET-DROP-TARGET-XYZZY"u8.ToArray();
    var keep2 = "keep-two"u8.ToArray();
    using var img = BuildImage(
      ("keep1.txt", keep1),
      ("drop.txt", dropPayload),
      ("keep2.txt", keep2));

    ((IArchiveModifiable)new F2fsFormatDescriptor()).Remove(img, ["drop.txt"]);

    var files = ReadAll(img);
    Assert.Multiple(() => {
      Assert.That(files, Has.Count.EqualTo(2));
      Assert.That(files.ContainsKey("drop.txt"), Is.False);
      Assert.That(files["keep1.txt"], Is.EqualTo(keep1));
      Assert.That(files["keep2.txt"], Is.EqualTo(keep2));

      // Forensic: the dropped payload bytes are gone from the raw image (block was wiped).
      img.Position = 0;
      using var ms = new MemoryStream();
      img.CopyTo(ms);
      var rawBytes = ms.ToArray();
      var dropMarker = Encoding.UTF8.GetBytes("SECRET-DROP-TARGET-XYZZY");
      Assert.That(IndexOf(rawBytes, dropMarker), Is.EqualTo(-1),
        "Removed file's data block must be wiped from the image bytes.");
    });
  }

  // ── Given ──────────────────────────────────────────────────────────────
  // a fresh F2FS image with one seed file.
  // ── When ───────────────────────────────────────────────────────────────
  // 50 root-level files are added one at a time — enough to overflow the
  // 38-entry NAT journal capacity in the compact summary block.
  // ── Then ──────────────────────────────────────────────────────────────
  // every Add succeeds (post-leaf scope: journal overflow falls through to
  // the canonical on-disk NAT entry rather than throwing), and the reader
  // sees all 51 files at their full names.
  [Test, Category("RoundTrip")]
  public void Add_PastJournalCapacity_SucceedsViaOnDiskNat() {
    using var img = BuildImage(("seed.txt", "s"u8.ToArray()));

    var desc = (IArchiveModifiable)new F2fsFormatDescriptor();
    for (var i = 0; i < 50; ++i)
      desc.Add(img, [ArchiveInputInfo.InMemory($"f{i}.txt", new byte[] { (byte)i })]);

    var files = ReadAll(img);
    Assert.Multiple(() => {
      Assert.That(files, Has.Count.EqualTo(51));
      Assert.That(files["seed.txt"], Is.EqualTo("s"u8.ToArray()));
      for (var i = 0; i < 50; ++i)
        Assert.That(files[$"f{i}.txt"], Is.EqualTo(new byte[] { (byte)i }),
          $"file f{i}.txt content intact past journal-overflow boundary");
    });
  }

  // ── Given ──────────────────────────────────────────────────────────────
  // a fresh F2FS image with one root file.
  // ── When ───────────────────────────────────────────────────────────────
  // a file is added, then removed by name.
  // ── Then ──────────────────────────────────────────────────────────────
  // the image returns to its original logical contents (one file).
  [Test, Category("RoundTrip")]
  public void Add_ThenRemove_LeavesOriginal() {
    using var img = BuildImage(("alpha.txt", "A"u8.ToArray()));

    var m = (IArchiveModifiable)new F2fsFormatDescriptor();
    m.Add(img, [ArchiveInputInfo.InMemory("temp.txt", "T"u8.ToArray())]);
    m.Remove(img, ["temp.txt"]);

    var files = ReadAll(img);
    Assert.Multiple(() => {
      Assert.That(files, Has.Count.EqualTo(1));
      Assert.That(files["alpha.txt"], Is.EqualTo("A"u8.ToArray()));
    });
  }

  // ── Given ──────────────────────────────────────────────────────────────
  // a fresh F2FS image.
  // ── When ───────────────────────────────────────────────────────────────
  // an Add operation runs.
  // ── Then ──────────────────────────────────────────────────────────────
  // the checkpoint version in the alternate CP pack has been bumped past
  // the previous active pack's version (alternating-checkpoint design).
  [Test, Category("HappyPath")]
  public void Add_BumpsCheckpointVersionInAlternatePack() {
    using var img = BuildImage(("seed.txt", "s"u8.ToArray()));
    img.Position = 0;
    using var msBefore = new MemoryStream();
    img.CopyTo(msBefore);
    var before = msBefore.ToArray();

    // SB tells us where CP packs live.
    var cpBlk = (int)BinaryPrimitives.ReadUInt32LittleEndian(before.AsSpan(0x400 + 76));
    var cp0Off = cpBlk * 4096;
    var cp1Off = (cpBlk + 512) * 4096;
    var ver0Before = BinaryPrimitives.ReadUInt64LittleEndian(before.AsSpan(cp0Off));
    var ver1Before = BinaryPrimitives.ReadUInt64LittleEndian(before.AsSpan(cp1Off));
    var activeBefore = Math.Max(ver0Before, ver1Before);

    ((IArchiveModifiable)new F2fsFormatDescriptor()).Add(img,
      [ArchiveInputInfo.InMemory("new.txt", "n"u8.ToArray())]);

    img.Position = 0;
    using var msAfter = new MemoryStream();
    img.CopyTo(msAfter);
    var after = msAfter.ToArray();
    var ver0After = BinaryPrimitives.ReadUInt64LittleEndian(after.AsSpan(cp0Off));
    var ver1After = BinaryPrimitives.ReadUInt64LittleEndian(after.AsSpan(cp1Off));
    var activeAfter = Math.Max(ver0After, ver1After);

    Assert.That(activeAfter, Is.GreaterThan(activeBefore),
      "Active checkpoint version must advance after an Add operation.");
  }

  private static int IndexOf(byte[] hay, byte[] needle) {
    for (var i = 0; i <= hay.Length - needle.Length; ++i) {
      var match = true;
      for (var j = 0; j < needle.Length; ++j) {
        if (hay[i + j] != needle[j]) { match = false; break; }
      }
      if (match) return i;
    }
    return -1;
  }
}
