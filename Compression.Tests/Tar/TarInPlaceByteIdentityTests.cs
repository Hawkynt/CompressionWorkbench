#pragma warning disable CS1591
using Compression.Registry;
using FileFormat.Tar;

namespace Compression.Tests.Tar;

/// <summary>
/// Locks the in-place contract for <see cref="TarModifier"/>: bytes that
/// existed before the trailing two zero blocks (the textbook "tar terminator")
/// are byte-identical after Add — the only writes happen at the old
/// terminator's position onward.
/// </summary>
[TestFixture]
public class TarInPlaceByteIdentityTests {

  // 512 bytes per block × 2 = 1024-byte trailing zero terminator that
  // TarWriter.Finish emits and TarModifier.AddFile overwrites.
  private const int BlockSize = 512;
  private const int TerminatorBytes = BlockSize * 2;

  [Test, Category("ByteIdentity")]
  public void AddFile_PreservesBytesBeforeOldTerminator() {
    var seed = BuildSeedTar(("seed.txt", "seed-content"u8.ToArray()));
    var oldBytes = seed.ToArray();
    var oldTerminatorOffset = oldBytes.Length - TerminatorBytes;

    using var ms = new MemoryStream();
    ms.Write(oldBytes);
    TarModifier.AddFile(ms, "added.txt", "appended-data"u8.ToArray());

    var newBytes = ms.ToArray();
    Assert.That(newBytes.Length, Is.GreaterThan(oldBytes.Length),
      "Add must grow the archive (new entry + new terminator).");

    // Invariant: [0, oldTerminatorOffset) byte-identical.
    AssertBytesEqual(oldBytes.AsSpan(0, oldTerminatorOffset),
      newBytes.AsSpan(0, oldTerminatorOffset),
      "Bytes before the old terminator must be untouched.");
  }

  [Test, Category("ByteIdentity")]
  public void AddFile_MultipleAppends_PreservesAllPriorEntryBytes() {
    var seed = BuildSeedTar(
      ("one.txt", "first"u8.ToArray()),
      ("two.txt", "second"u8.ToArray()));
    var oldBytes = seed.ToArray();
    var oldTerminatorOffset = oldBytes.Length - TerminatorBytes;

    using var ms = new MemoryStream();
    ms.Write(oldBytes);
    TarModifier.AddFile(ms, "three.txt", "third"u8.ToArray());

    var afterFirst = ms.ToArray();
    AssertBytesEqual(oldBytes.AsSpan(0, oldTerminatorOffset),
      afterFirst.AsSpan(0, oldTerminatorOffset),
      "First Add must not touch any pre-existing entry bytes.");

    var midTerminatorOffset = afterFirst.Length - TerminatorBytes;
    TarModifier.AddFile(ms, "four.txt", "fourth"u8.ToArray());

    var afterSecond = ms.ToArray();
    AssertBytesEqual(afterFirst.AsSpan(0, midTerminatorOffset),
      afterSecond.AsSpan(0, midTerminatorOffset),
      "Second Add must not touch any bytes written by the first Add.");
  }

  [Test, Category("ByteIdentity")]
  public void Listing_ReflectsNewStateAfterAdd() {
    using var ms = BuildSeedTar(("seed.txt", "seed-content"u8.ToArray()));
    TarModifier.AddFile(ms, "added.txt", "fresh"u8.ToArray());

    ms.Position = 0;
    var entries = new TarReader(ms).ReadAllNames();
    Assert.That(entries, Does.Contain("seed.txt"));
    Assert.That(entries, Does.Contain("added.txt"));
  }

  [Test, Category("ByteIdentity")]
  public void MutateThenExtract_PreservesCallerPayload() {
    using var ms = BuildSeedTar(("seed.txt", "seed-content"u8.ToArray()));
    var payload = new byte[1024];
    for (var i = 0; i < payload.Length; ++i) payload[i] = (byte)((i * 31 + 7) & 0xFF);
    TarModifier.AddFile(ms, "payload.bin", payload);

    ms.Position = 0;
    var r = new TarReader(ms);
    while (r.GetNextEntry() is { } e) {
      if (e.Name != "payload.bin") { r.Skip(); continue; }
      using var es = r.GetEntryStream();
      var read = new byte[e.Size];
      es.ReadExactly(read);
      Assert.That(read, Is.EqualTo(payload));
      return;
    }
    Assert.Fail("payload.bin not found after Add.");
  }

  [Test, Category("ByteIdentity")]
  public void Descriptor_AdvertisesCanModify_AndImplementsIArchiveModifiable() {
    var d = new TarFormatDescriptor();
    Assert.Multiple(() => {
      Assert.That(d.Capabilities & FormatCapabilities.CanModify, Is.EqualTo(FormatCapabilities.CanModify),
        "Descriptor must advertise CanModify.");
      Assert.That(d, Is.InstanceOf<IArchiveModifiable>(),
        "Descriptor must implement IArchiveModifiable.");
    });
  }

  // ── Helpers ────────────────────────────────────────────────────────

  private static MemoryStream BuildSeedTar(params (string Name, byte[] Data)[] entries) {
    var ms = new MemoryStream();
    var w = new TarWriter(ms, leaveOpen: true);
    foreach (var (name, data) in entries)
      w.AddEntry(new TarEntry { Name = name, Size = data.Length }, data);
    w.Finish();
    ms.Position = 0;
    return ms;
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

internal static class TarReaderExtensions {
  public static List<string> ReadAllNames(this TarReader r) {
    var names = new List<string>();
    while (r.GetNextEntry() is { } e) {
      names.Add(e.Name);
      r.Skip();
    }
    return names;
  }
}
