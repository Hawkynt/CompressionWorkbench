#pragma warning disable CS1591
using Compression.Registry;
using FileFormat.Pak;

namespace Compression.Tests.Pak;

/// <summary>
/// Locks the in-place contract for <see cref="PakInPlaceModifier"/>: PAK
/// shares the ARC binary layout (entry-chain terminated by a 2-byte
/// end-of-archive marker), so Add overwrites only the old EOA marker and
/// re-writes a fresh one after the new entry. Bytes before the old EOA
/// are byte-identical after the operation.
/// </summary>
[TestFixture]
public class PakInPlaceModifyTests {

  // ARC/PAK end-of-archive marker: 0x1A 0x00.
  private const int EoaMarkerBytes = 2;

  [Test, Category("ByteIdentity")]
  public void AddFile_PreservesBytesBeforeOldEoaMarker() {
    var seed = BuildSeedPak(("seed.txt", "seed-content"u8.ToArray()));
    var oldBytes = seed.ToArray();
    var oldEoaOffset = oldBytes.Length - EoaMarkerBytes;

    using var ms = new MemoryStream();
    ms.Write(oldBytes);
    PakInPlaceModifier.AddFile(ms, "added.txt", "appended"u8.ToArray());

    var newBytes = ms.ToArray();
    Assert.That(newBytes.Length, Is.GreaterThan(oldBytes.Length),
      "Add must grow the archive (new entry + fresh EOA).");

    AssertBytesEqual(oldBytes.AsSpan(0, oldEoaOffset),
      newBytes.AsSpan(0, oldEoaOffset),
      "Bytes before the old end-of-archive marker must be untouched.");
  }

  [Test, Category("ByteIdentity")]
  public void AddFile_MultipleAppends_PreservesAllPriorEntryBytes() {
    var seed = BuildSeedPak(
      ("one.txt", "first"u8.ToArray()),
      ("two.txt", "second"u8.ToArray()));
    var oldBytes = seed.ToArray();
    var oldEoaOffset = oldBytes.Length - EoaMarkerBytes;

    using var ms = new MemoryStream();
    ms.Write(oldBytes);
    PakInPlaceModifier.AddFile(ms, "three.txt", "third"u8.ToArray());

    var afterFirst = ms.ToArray();
    AssertBytesEqual(oldBytes.AsSpan(0, oldEoaOffset),
      afterFirst.AsSpan(0, oldEoaOffset),
      "First Add must not touch any pre-existing entry bytes.");

    var midEoaOffset = afterFirst.Length - EoaMarkerBytes;
    PakInPlaceModifier.AddFile(ms, "four.txt", "fourth"u8.ToArray());

    var afterSecond = ms.ToArray();
    AssertBytesEqual(afterFirst.AsSpan(0, midEoaOffset),
      afterSecond.AsSpan(0, midEoaOffset),
      "Second Add must not touch any bytes written by the first Add.");
  }

  [Test, Category("RoundTrip")]
  public void AddFile_ReadsBack() {
    using var ms = BuildSeedPak(("seed.txt", "seed-content"u8.ToArray()));
    PakInPlaceModifier.AddFile(ms, "added.txt", "hello-pak"u8.ToArray());

    ms.Position = 0;
    var entries = ReadAll(ms);
    Assert.That(entries["added.txt"], Is.EqualTo("hello-pak"));
    Assert.That(entries["seed.txt"], Is.EqualTo("seed-content"));
  }

  [Test, Category("RoundTrip")]
  public void RemoveFile_DropsEntry() {
    using var ms = BuildSeedPak(("seed.txt", "seed-content"u8.ToArray()));
    PakInPlaceModifier.AddFile(ms, "victim.txt", "delete-me"u8.ToArray());
    PakInPlaceModifier.AddFile(ms, "keeper.txt", "keep-me"u8.ToArray());
    Assert.That(PakInPlaceModifier.RemoveFile(ms, "victim.txt"), Is.True);

    ms.Position = 0;
    var entries = ReadAll(ms);
    Assert.That(entries.ContainsKey("victim.txt"), Is.False);
    Assert.That(entries.ContainsKey("keeper.txt"), Is.True);
    Assert.That(entries["keeper.txt"], Is.EqualTo("keep-me"));
    Assert.That(entries["seed.txt"], Is.EqualTo("seed-content"));
  }

  [Test, Category("RoundTrip")]
  public void RemoveFile_NotFound_ReturnsFalse() {
    using var ms = BuildSeedPak(("seed.txt", "seed-content"u8.ToArray()));
    Assert.That(PakInPlaceModifier.RemoveFile(ms, "ghost.txt"), Is.False);
  }

  [Test, Category("RoundTrip")]
  public void MutateThenExtract_PreservesCallerPayload() {
    using var ms = BuildSeedPak(("seed.txt", "seed-content"u8.ToArray()));
    var payload = new byte[1024];
    for (var i = 0; i < payload.Length; ++i) payload[i] = (byte)((i * 11 + 1) & 0xFF);
    PakInPlaceModifier.AddFile(ms, "payload.bin", payload);

    ms.Position = 0;
    var entries = ReadAll(ms);
    Assert.That(entries.ContainsKey("payload.bin"), Is.True);
    Assert.That(System.Text.Encoding.Latin1.GetBytes(entries["payload.bin"]), Is.EqualTo(payload));
  }

  [Test, Category("HappyPath")]
  public void Descriptor_AdvertisesCanModify_AndImplementsIArchiveModifiable() {
    var d = new PakFormatDescriptor();
    Assert.Multiple(() => {
      Assert.That(d.Capabilities & FormatCapabilities.CanModify, Is.EqualTo(FormatCapabilities.CanModify),
        "Descriptor must advertise CanModify.");
      Assert.That(d, Is.InstanceOf<IArchiveModifiable>(),
        "Descriptor must implement IArchiveModifiable.");
    });
  }

  [Test, Category("HappyPath")]
  public void Descriptor_AddViaInterface_UsesInPlacePath() {
    using var ms = BuildSeedPak(("seed.txt", "seed-content"u8.ToArray()));
    var tmp = Path.GetTempFileName();
    try {
      File.WriteAllBytes(tmp, "via-if"u8.ToArray());
      ((IArchiveModifiable)new PakFormatDescriptor()).Add(ms,
        [new ArchiveInputInfo(tmp, "viaif.txt", false)]);

      ms.Position = 0;
      var entries = ReadAll(ms);
      Assert.That(entries["viaif.txt"], Is.EqualTo("via-if"));
    } finally { File.Delete(tmp); }
  }

  // ── Helpers ────────────────────────────────────────────────────────

  private static MemoryStream BuildSeedPak(params (string Name, byte[] Data)[] entries) {
    var ms = new MemoryStream();
    var w = new PakWriter(ms);
    foreach (var (name, data) in entries)
      w.AddEntry(name, data);
    w.Finish();
    ms.Position = 0;
    var copy = new MemoryStream();
    ms.CopyTo(copy);
    copy.Position = 0;
    return copy;
  }

  private static Dictionary<string, string> ReadAll(Stream s) {
    s.Position = 0;
    var r = new PakReader(s);
    var result = new Dictionary<string, string>();
    while (r.GetNextEntry() is { } e) {
      var data = r.ReadEntryData();
      result[e.FileName] = System.Text.Encoding.Latin1.GetString(data);
    }
    return result;
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
