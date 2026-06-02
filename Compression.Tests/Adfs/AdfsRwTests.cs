using System.Text;
using Compression.Registry;
using FileSystem.Adfs;

namespace Compression.Tests.Adfs;

/// <summary>
/// R/W round-trip tests for the ADFS-L modifier. Covers in-place Add/Remove
/// against a freshly-built ADFS-L image, FSM check-byte parity after every
/// mutation, root directory check-byte recompute, free-region merging after
/// removal, and capacity bound (47-entry old-map root directory cap).
/// </summary>
[TestFixture]
public class AdfsRwTests {

  // ── Helpers ────────────────────────────────────────────────────────────

  private static MemoryStream BuildSeedImage(params (string Name, byte[] Data)[] files) {
    var w = new AdfsWriter();
    foreach (var (n, d) in files) w.AddFile(n, d);
    var img = w.Build();
    var ms = new MemoryStream();
    ms.Write(img, 0, img.Length);
    ms.Position = 0;
    return ms;
  }

  private static byte ComputeOldMapCheckByte(ReadOnlySpan<byte> sector) {
    uint sum = 0;
    for (var i = 0; i < 255; i++) {
      sum += sector[i];
      if (sum > 0xFF)
        sum = (sum + 1) & 0xFF;
    }
    return (byte)sum;
  }

  // ── HappyPath: Add then read ───────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Add_SingleFile_AppearsInListing() {
    using var img = BuildSeedImage(("SEED", "seed-data"u8.ToArray()));

    AdfsModifier.AddFile(img, "HELLO", "hello-data"u8.ToArray());

    img.Position = 0;
    using var r = new AdfsReader(img);
    Assert.That(r.Entries.Select(e => e.Name), Is.EquivalentTo(new[] { "SEED", "HELLO" }));
    var hello = r.Entries.First(e => e.Name == "HELLO");
    Assert.That(Encoding.ASCII.GetString(r.Extract(hello)), Is.EqualTo("hello-data"));
  }

  [Test, Category("HappyPath")]
  public void Add_MultipleFiles_AllRoundTrip() {
    using var img = BuildSeedImage();

    var payloads = new (string Name, byte[] Data)[] {
      ("ALPHA", "alpha-content"u8.ToArray()),
      ("BRAVO", MakePayload(600, seed: 1)),
      ("CHARLIE", MakePayload(1500, seed: 2)),
      ("DELTA", MakePayload(50, seed: 3)),
    };
    foreach (var (n, d) in payloads) AdfsModifier.AddFile(img, n, d);

    img.Position = 0;
    using var r = new AdfsReader(img);
    Assert.That(r.Entries, Has.Count.EqualTo(4));
    foreach (var (n, d) in payloads) {
      var e = r.Entries.First(x => x.Name == n);
      Assert.That(r.Extract(e), Is.EqualTo(d), $"payload mismatch for {n}");
    }
  }

  // ── HappyPath: Remove then read ────────────────────────────────────────

  [Test, Category("HappyPath")]
  public void Remove_ExistingFile_VanishesFromListing() {
    using var img = BuildSeedImage(
      ("A", "aaa"u8.ToArray()),
      ("B", "bbb"u8.ToArray()),
      ("C", "ccc"u8.ToArray()));

    var removed = AdfsModifier.RemoveFile(img, "B");
    Assert.That(removed, Is.True);

    img.Position = 0;
    using var r = new AdfsReader(img);
    Assert.That(r.Entries.Select(e => e.Name), Is.EquivalentTo(new[] { "A", "C" }));
    Assert.That(Encoding.ASCII.GetString(r.Extract(r.Entries.First(e => e.Name == "A"))), Is.EqualTo("aaa"));
    Assert.That(Encoding.ASCII.GetString(r.Extract(r.Entries.First(e => e.Name == "C"))), Is.EqualTo("ccc"));
  }

  [Test, Category("HappyPath")]
  public void Remove_NonExistent_ReturnsFalse() {
    using var img = BuildSeedImage(("A", "aaa"u8.ToArray()));
    var removed = AdfsModifier.RemoveFile(img, "NOPE");
    Assert.That(removed, Is.False);

    img.Position = 0;
    using var r = new AdfsReader(img);
    Assert.That(r.Entries.Select(e => e.Name), Is.EquivalentTo(new[] { "A" }));
  }

  // ── HappyPath: Add → Remove → Add cycle ────────────────────────────────

  [Test, Category("HappyPath")]
  public void AddRemoveAdd_FreeListMerges_NewFileLandsInRecycledSpace() {
    using var img = BuildSeedImage();

    var big = MakePayload(2 * 256 + 10, seed: 42);  // spans 3 sectors
    AdfsModifier.AddFile(img, "BIG", big);

    // The big file should have landed at sector 7 (first data sector).
    img.Position = 0;
    using var r1 = new AdfsReader(img);
    var bigEntry = r1.Entries.First(e => e.Name == "BIG");
    Assert.That(bigEntry.StartSector, Is.EqualTo(7));

    // Remove it; free list should merge back to a single region from sector 7.
    Assert.That(AdfsModifier.RemoveFile(img, "BIG"), Is.True);

    // Add another file of the same shape; first-fit puts it back at sector 7.
    var also = MakePayload(2 * 256 + 10, seed: 99);
    AdfsModifier.AddFile(img, "ALSO", also);

    img.Position = 0;
    using var r2 = new AdfsReader(img);
    var alsoEntry = r2.Entries.First(e => e.Name == "ALSO");
    Assert.That(alsoEntry.StartSector, Is.EqualTo(7),
      "after free+alloc the recycled region must be reusable at the same start sector");
    Assert.That(r2.Extract(alsoEntry), Is.EqualTo(also));
  }

  // ── Round-trip via the descriptor IArchiveModifiable surface ───────────

  [Test, Category("RoundTrip")]
  public void Descriptor_AddRemove_ViaIArchiveModifiable_RoundTrips() {
    var desc = new AdfsFormatDescriptor();
    Assert.That(desc, Is.InstanceOf<IArchiveModifiable>());
    Assert.That(desc.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.True);

    using var img = BuildSeedImage(("KEEP", "keep-me"u8.ToArray()));

    var tmpA = Path.GetTempFileName();
    var tmpB = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmpA, "alpha-bytes"u8.ToArray());
      File.WriteAllBytes(tmpB, MakePayload(800, seed: 7));

      ((IArchiveModifiable)desc).Add(img, [
        new ArchiveInputInfo(tmpA, "ADDA", false),
        new ArchiveInputInfo(tmpB, "ADDB", false),
      ]);

      img.Position = 0;
      var entries = desc.List(img, null);
      Assert.That(entries.Select(e => e.Name), Is.EquivalentTo(new[] { "KEEP", "ADDA", "ADDB" }));

      ((IArchiveModifiable)desc).Remove(img, new[] { "KEEP" });

      img.Position = 0;
      var afterRemove = desc.List(img, null);
      Assert.That(afterRemove.Select(e => e.Name), Is.EquivalentTo(new[] { "ADDA", "ADDB" }));

      img.Position = 0;
      var bytesA = desc.ExtractEntryToMemory(img, "ADDA", null);
      Assert.That(Encoding.ASCII.GetString(bytesA), Is.EqualTo("alpha-bytes"));
    } finally {
      File.Delete(tmpA);
      File.Delete(tmpB);
    }
  }

  // ── Spec: FSM check-byte parity after mutation ─────────────────────────

  [Test, Category("Spec")]
  public void Add_RecomputesFsmCheckBytes_BothSectorsValid() {
    using var img = BuildSeedImage();
    AdfsModifier.AddFile(img, "F1", MakePayload(300, seed: 1));

    var bytes = img.ToArray();
    var s0 = bytes.AsSpan(0, 256);
    var s1 = bytes.AsSpan(256, 256);
    Assert.That(s0[0xFF], Is.EqualTo(ComputeOldMapCheckByte(s0)),
      "sector 0 check byte must equal the rotate-and-add recomputation");
    Assert.That(s1[0xFF], Is.EqualTo(ComputeOldMapCheckByte(s1)),
      "sector 1 check byte must equal the rotate-and-add recomputation");
    Assert.That(s0[0xFE], Is.EqualTo(s1[0xFE]),
      "FSM end pointers must match between sector 0 and sector 1");
  }

  [Test, Category("Spec")]
  public void Remove_RecomputesFsmCheckBytes_BothSectorsValid() {
    using var img = BuildSeedImage(
      ("ONE", MakePayload(400, seed: 1)),
      ("TWO", MakePayload(600, seed: 2)),
      ("THREE", MakePayload(800, seed: 3)));

    Assert.That(AdfsModifier.RemoveFile(img, "TWO"), Is.True);

    var bytes = img.ToArray();
    var s0 = bytes.AsSpan(0, 256);
    var s1 = bytes.AsSpan(256, 256);
    Assert.That(s0[0xFF], Is.EqualTo(ComputeOldMapCheckByte(s0)));
    Assert.That(s1[0xFF], Is.EqualTo(ComputeOldMapCheckByte(s1)));
    Assert.That(s0[0xFE], Is.EqualTo(s1[0xFE]));

    // The freed region for TWO (3 sectors) should now appear as a free region.
    // Decoding sector 0 entries: each (start, len) pair occupies 3 bytes in each sector.
    var endPtr = s0[0xFE];
    Assert.That(endPtr % 3, Is.EqualTo(0));
  }

  [Test, Category("Spec")]
  public void Remove_FreeRegions_MergeWithSuccessor() {
    // Layout after build (sectors): FSM[0,1] dir[2..6] ONE[7..7] TWO[8..8] free[9..2559]
    using var img = BuildSeedImage(
      ("ONE", new byte[200]),  // 1 sector at 7
      ("TWO", new byte[200])); // 1 sector at 8

    // After removing TWO, the new free region (sector 8, length 1) must merge
    // with the existing trailing free region (sector 9..end) into one region.
    Assert.That(AdfsModifier.RemoveFile(img, "TWO"), Is.True);

    var bytes = img.ToArray();
    var endPtr = bytes[0xFE];
    Assert.That(endPtr, Is.EqualTo(3),
      "after merge there must be exactly one free region (3 bytes of entry data, end pointer = 3)");
    // The merged region starts at sector 8 (formerly TWO's sector).
    var freeStart = bytes[0] | (bytes[1] << 8) | (bytes[2] << 16);
    Assert.That(freeStart, Is.EqualTo(8));
  }

  // ── Spec: Directory check byte parity after mutation ───────────────────

  [Test, Category("Spec")]
  public void Add_RecomputesDirectoryCheckByte() {
    using var img = BuildSeedImage();
    AdfsModifier.AddFile(img, "DIRTY", "x"u8.ToArray());

    var bytes = img.ToArray();
    var dir = bytes.AsSpan(0x200, 1280);
    byte expected = 0;
    for (var i = 0; i < 0x4FD; i++) expected ^= dir[i];
    Assert.That(dir[0x4FD], Is.EqualTo(expected),
      "directory check byte at 0x4FD must equal XOR over bytes 0..0x4FC");
  }

  // ── Spec: existing reader accepts post-mutation images ─────────────────

  [Test, Category("Spec")]
  public void Add_ImageStillParsesThroughExistingReader() {
    using var img = BuildSeedImage(("PRE", "pre"u8.ToArray()));
    AdfsModifier.AddFile(img, "POST", "post"u8.ToArray());

    img.Position = 0;
    using var r = new AdfsReader(img);
    Assert.That(r.DirectoryMagic, Is.EqualTo("Hugo"));
    Assert.That(r.SectorSize, Is.EqualTo(256));
    Assert.That(r.Entries.Select(e => e.Name), Is.EquivalentTo(new[] { "PRE", "POST" }));
  }

  [Test, Category("Spec")]
  public void Remove_ImageStillParsesThroughExistingReader() {
    using var img = BuildSeedImage(
      ("FIRST", "f"u8.ToArray()),
      ("SECOND", "s"u8.ToArray()),
      ("THIRD", "t"u8.ToArray()));
    Assert.That(AdfsModifier.RemoveFile(img, "SECOND"), Is.True);

    img.Position = 0;
    using var r = new AdfsReader(img);
    Assert.That(r.DirectoryMagic, Is.EqualTo("Hugo"));
    Assert.That(r.Entries, Has.Count.EqualTo(2));
    Assert.That(r.Entries.Select(e => e.Name), Is.EquivalentTo(new[] { "FIRST", "THIRD" }));
  }

  // ── Boundary: capacity bound (47-entry old-map root cap) ───────────────

  [Test, Category("Boundary")]
  public void Add_PastRootDirCap_Throws() {
    using var img = BuildSeedImage();
    for (var i = 0; i < 47; i++)
      AdfsModifier.AddFile(img, $"F{i:D2}", new byte[1]);

    Assert.Throws<InvalidOperationException>(() =>
      AdfsModifier.AddFile(img, "OVERFLOW", new byte[1]));
  }

  // ── Boundary: zero-byte file ───────────────────────────────────────────

  [Test, Category("Boundary")]
  public void Add_ZeroByteFile_RoundTrips() {
    using var img = BuildSeedImage();
    AdfsModifier.AddFile(img, "EMPTY", []);

    img.Position = 0;
    using var r = new AdfsReader(img);
    var e = r.Entries.First(x => x.Name == "EMPTY");
    Assert.That(e.Size, Is.EqualTo(0));
    Assert.That(r.Extract(e), Is.Empty);
  }

  // ── Error: image too small to be ADFS-L ────────────────────────────────

  [Test, Category("ErrorHandling")]
  public void Add_NonAdfsLSize_Throws() {
    using var ms = new MemoryStream(new byte[1024]);
    Assert.Throws<InvalidDataException>(() => AdfsModifier.AddFile(ms, "X", new byte[1]));
  }

  // ── Forensic wipe: removed file's data is zeroed ───────────────────────

  [Test, Category("Spec")]
  public void Remove_WipesDataSectors_NoForensicTrace() {
    var secret = new byte[256];
    for (var i = 0; i < secret.Length; i++) secret[i] = 0xAA;
    using var img = BuildSeedImage(("SECRET", secret));

    img.Position = 0;
    using (var r = new AdfsReader(img)) {
      var e = r.Entries.First(x => x.Name == "SECRET");
      Assert.That(e.StartSector, Is.EqualTo(7));
    }

    Assert.That(AdfsModifier.RemoveFile(img, "SECRET"), Is.True);

    var bytes = img.ToArray();
    var sector7 = bytes.AsSpan(7 * 256, 256);
    foreach (var b in sector7)
      Assert.That(b, Is.EqualTo(0), "removed file's data sector must be zeroed");
  }

  private static byte[] MakePayload(int length, int seed) {
    var rnd = new Random(seed);
    var buf = new byte[length];
    rnd.NextBytes(buf);
    return buf;
  }
}
