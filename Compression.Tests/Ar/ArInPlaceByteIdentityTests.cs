#pragma warning disable CS1591
using Compression.Registry;
using FileFormat.Ar;

namespace Compression.Tests.Ar;

/// <summary>
/// Locks the in-place contract for <see cref="ArModifier"/>: bytes that
/// existed before the Add call are byte-identical afterwards. AR has no
/// trailer marker — Add simply appends at EOF after a header-chain walk,
/// so the entire pre-existing byte range is the invariant.
/// </summary>
[TestFixture]
public class ArInPlaceByteIdentityTests {

  [Test, Category("ByteIdentity")]
  public void AddFile_PreservesAllPreExistingBytes() {
    var seedBytes = BuildSeedAr(("seed.txt", "seed-content"u8.ToArray())).ToArray();

    using var ms = new MemoryStream();
    ms.Write(seedBytes);
    ArModifier.AddFile(ms, "added.txt", "appended-data"u8.ToArray());

    var newBytes = ms.ToArray();
    Assert.That(newBytes.Length, Is.GreaterThan(seedBytes.Length),
      "Add must grow the archive (new entry appended).");

    // Invariant: [0, seedBytes.Length) byte-identical — AR appends at EOF only.
    AssertBytesEqual(seedBytes, newBytes.AsSpan(0, seedBytes.Length),
      "All pre-existing bytes must be untouched.");
  }

  [Test, Category("ByteIdentity")]
  public void AddFile_MultipleAppends_PreservesAllPriorEntryBytes() {
    var seedBytes = BuildSeedAr(
      ("one.txt", "first"u8.ToArray()),
      ("two.txt", "second"u8.ToArray())).ToArray();

    using var ms = new MemoryStream();
    ms.Write(seedBytes);
    ArModifier.AddFile(ms, "three.txt", "third"u8.ToArray());
    var afterFirst = ms.ToArray();
    AssertBytesEqual(seedBytes, afterFirst.AsSpan(0, seedBytes.Length),
      "First Add must not touch any pre-existing bytes.");

    var firstAddLen = afterFirst.Length;
    ArModifier.AddFile(ms, "four.txt", "fourth"u8.ToArray());
    var afterSecond = ms.ToArray();
    AssertBytesEqual(afterFirst, afterSecond.AsSpan(0, firstAddLen),
      "Second Add must not touch any bytes written by the first Add.");
  }

  [Test, Category("ByteIdentity")]
  public void AddFile_OddSizePadding_PreservesPriorBytes() {
    var seedBytes = BuildSeedAr(("seed.txt", "seed-content"u8.ToArray())).ToArray();

    using var ms = new MemoryStream();
    ms.Write(seedBytes);
    // Odd-length input triggers the 0x0A pad byte in AR; ensure that does not
    // bleed into the pre-existing region.
    ArModifier.AddFile(ms, "odd.txt", "abc"u8.ToArray()); // 3 bytes

    AssertBytesEqual(seedBytes, ms.ToArray().AsSpan(0, seedBytes.Length),
      "Pre-existing bytes must be untouched even when the appended entry needs alignment padding.");
  }

  [Test, Category("ByteIdentity")]
  public void Listing_ReflectsNewStateAfterAdd() {
    using var ms = BuildSeedAr(("seed.txt", "seed-content"u8.ToArray()));
    ArModifier.AddFile(ms, "added.txt", "fresh"u8.ToArray());

    ms.Position = 0;
    var names = new ArReader(ms).Entries.Select(e => e.Name).ToList();
    Assert.That(names, Does.Contain("seed.txt"));
    Assert.That(names, Does.Contain("added.txt"));
  }

  [Test, Category("ByteIdentity")]
  public void MutateThenExtract_PreservesCallerPayload() {
    using var ms = BuildSeedAr(("seed.txt", "seed-content"u8.ToArray()));
    var payload = new byte[1024];
    for (var i = 0; i < payload.Length; ++i) payload[i] = (byte)((i * 23 + 5) & 0xFF);
    ArModifier.AddFile(ms, "payload.bin", payload);

    ms.Position = 0;
    var found = new ArReader(ms).Entries.FirstOrDefault(e => e.Name == "payload.bin");
    Assert.That(found, Is.Not.Null, "payload.bin not found after Add.");
    Assert.That(found!.Data, Is.EqualTo(payload));
  }

  [Test, Category("ByteIdentity")]
  public void Descriptor_AdvertisesCanModify_AndImplementsIArchiveModifiable() {
    var d = new ArFormatDescriptor();
    Assert.Multiple(() => {
      Assert.That(d.Capabilities & FormatCapabilities.CanModify, Is.EqualTo(FormatCapabilities.CanModify),
        "Descriptor must advertise CanModify.");
      Assert.That(d, Is.InstanceOf<IArchiveModifiable>(),
        "Descriptor must implement IArchiveModifiable.");
    });
  }

  // ── Helpers ────────────────────────────────────────────────────────

  private static MemoryStream BuildSeedAr(params (string Name, byte[] Data)[] entries) {
    var ms = new MemoryStream();
    using (var w = new ArWriter(ms, leaveOpen: true)) {
      w.Write(entries.Select(e => new ArEntry { Name = e.Name, Data = e.Data }).ToList());
    }
    ms.Position = 0;
    var copy = new MemoryStream();
    ms.CopyTo(copy);
    copy.Position = 0;
    return copy;
  }

  private static void AssertBytesEqual(ReadOnlySpan<byte> expected, ReadOnlySpan<byte> actual, string message) {
    if (expected.Length != actual.Length)
      Assert.Fail($"{message} (length: expected {expected.Length}, got {actual.Length})");
    for (var i = 0; i < expected.Length; ++i) {
      if (expected[i] != actual[i])
        Assert.Fail($"{message} (first difference at offset {i}: expected 0x{expected[i]:X2}, got 0x{actual[i]:X2})");
    }
  }
}
