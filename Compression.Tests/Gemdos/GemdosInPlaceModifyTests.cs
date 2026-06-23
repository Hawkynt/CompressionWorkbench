#pragma warning disable CS1591
using System.Text;
using Compression.Registry;
using FileSystem.Gemdos;

namespace Compression.Tests.Gemdos;

/// <summary>
/// In-place modify tests for the GEMDOS (Atari ST FAT12) descriptor.
/// The modifier round-trips through the existing GEMDOS reader so the
/// 0x60 BRA.S jump byte at offset 0 is preserved across mutation, while
/// per-file Add/Remove behaviour matches the FAT12 in-place semantics.
/// </summary>
[TestFixture]
public class GemdosInPlaceModifyTests {

  private static MemoryStream BuildBaselineImage(params (string Name, string Body)[] entries) {
    var w = new GemdosWriter();
    foreach (var (name, body) in entries)
      w.AddFile(name, Encoding.ASCII.GetBytes(body));
    var image = w.Build(totalSectors: 1440);
    return new MemoryStream(image, writable: true) { Capacity = image.Length };
  }

  private static IReadOnlyList<string> ListNames(Stream archive) {
    archive.Position = 0;
    using var r = new GemdosReader(archive);
    return r.Entries.Where(e => !e.IsDirectory).Select(e => e.Name).ToList();
  }

  private static byte[] ReadEntry(Stream archive, string name) {
    archive.Position = 0;
    using var r = new GemdosReader(archive);
    var e = r.Entries.Single(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
    return r.Extract(e);
  }

  [Test, Category("RoundTrip")]
  public void Add_IsGenuineInPlace_PreservesJumpByteBootSectorAndExistingFile() {
    using var image = BuildBaselineImage(("KEEP.TXT", new string('K', 2500)));
    var before = image.ToArray();

    GemdosInPlaceModifier.AddFiles(image, [("NEW.TXT", Encoding.ASCII.GetBytes("fresh bytes"))]);
    var after = image.ToArray();

    Assert.Multiple(() => {
      Assert.That(after.Length, Is.EqualTo(before.Length), "in-place add must not resize the image");
      Assert.That(after[0], Is.EqualTo(before[0]), "GEMDOS 0x60 BRA.S jump byte must be preserved");
      Assert.That(after.AsSpan(0, 512).SequenceEqual(before.AsSpan(0, 512)), Is.True,
        "boot sector must be byte-identical after an in-place add");
      Assert.That(Encoding.ASCII.GetString(ReadEntry(image, "KEEP.TXT")), Is.EqualTo(new string('K', 2500)));
      Assert.That(Encoding.ASCII.GetString(ReadEntry(image, "NEW.TXT")), Is.EqualTo("fresh bytes"));
    });
  }

  // ── Add ─────────────────────────────────────────────────────────────────

  [Test, Category("RoundTrip")]
  public void Add_NewFile_AppearsViaReader() {
    using var image = BuildBaselineImage(("ALPHA.TXT", "AAA"));
    GemdosInPlaceModifier.AddFiles(image, [("BETA.TXT", Encoding.ASCII.GetBytes("BBB"))]);
    Assert.That(ListNames(image), Does.Contain("BETA.TXT"));
    Assert.That(Encoding.ASCII.GetString(ReadEntry(image, "BETA.TXT")), Is.EqualTo("BBB"));
  }

  [Test, Category("RoundTrip")]
  public void Add_PreservesGemdosJumpByte() {
    using var image = BuildBaselineImage(("A.TXT", "AAA"));
    GemdosInPlaceModifier.AddFiles(image, [("B.TXT", Encoding.ASCII.GetBytes("BBB"))]);
    image.Position = 0;
    var first = image.ReadByte();
    Assert.That(first, Is.EqualTo(GemdosBpb.GemdosJump),
      "0x60 BRA.S jump byte must survive Add — it's what marks the image as GEMDOS.");
  }

  [Test, Category("RoundTrip")]
  public void Add_ExistingFiles_StillReadback() {
    using var image = BuildBaselineImage(("KEEP.TXT", "KEEP-ME"), ("OTHER.TXT", "other"));
    var beforeKeep = ReadEntry(image, "KEEP.TXT");

    GemdosInPlaceModifier.AddFiles(image, [("NEW.TXT", new byte[] { 1, 2, 3 })]);

    var afterKeep = ReadEntry(image, "KEEP.TXT");
    Assert.That(afterKeep, Is.EqualTo(beforeKeep), "Original file content must survive Add.");
  }

  // ── Remove ──────────────────────────────────────────────────────────────

  [Test, Category("RoundTrip")]
  public void Remove_NamedFile_Disappears() {
    using var image = BuildBaselineImage(("A.TXT", "AAA"), ("B.TXT", "BBB"));
    GemdosInPlaceModifier.RemoveFiles(image, ["A.TXT"]);
    Assert.That(ListNames(image), Does.Not.Contain("A.TXT"));
    Assert.That(ListNames(image), Does.Contain("B.TXT"));
  }

  [Test, Category("RoundTrip")]
  public void Remove_PreservesGemdosJumpByte() {
    using var image = BuildBaselineImage(("A.TXT", "AAA"), ("B.TXT", "BBB"));
    GemdosInPlaceModifier.RemoveFiles(image, ["A.TXT"]);
    image.Position = 0;
    Assert.That(image.ReadByte(), Is.EqualTo(GemdosBpb.GemdosJump));
  }

  [Test, Category("RoundTrip")]
  public void Remove_WipesPayloadBytes() {
    using var image = BuildBaselineImage(("SECRET.TXT", "MARKER-XYZZY-GEMDOS-WIPE-CHECK"));
    GemdosInPlaceModifier.RemoveFiles(image, ["SECRET.TXT"]);
    var asAscii = Encoding.ASCII.GetString(image.ToArray());
    Assert.That(asAscii, Does.Not.Contain("MARKER-XYZZY-GEMDOS-WIPE-CHECK"),
      "Removed file's payload bytes must be wiped from the image.");
  }

  [Test, Category("RoundTrip")]
  public void Remove_UnknownName_NoOp() {
    using var image = BuildBaselineImage(("KEEP.TXT", "KEEP"));
    GemdosInPlaceModifier.RemoveFiles(image, ["GHOST.TXT"]);
    Assert.That(ListNames(image), Does.Contain("KEEP.TXT"));
  }

  // ── Image-size preservation ─────────────────────────────────────────────

  [Test, Category("RoundTrip")]
  public void Add_DoesNotShrinkImage() {
    using var image = BuildBaselineImage(("A.TXT", "AAA"));
    var sizeBefore = image.Length;
    GemdosInPlaceModifier.AddFiles(image, [("B.TXT", new byte[] { 9, 9, 9 })]);
    Assert.That(image.Length, Is.EqualTo(sizeBefore),
      "Add must preserve the GEMDOS image's outer sector count.");
  }

  // ── Descriptor interface routing ────────────────────────────────────────

  [Test, Category("Spec")]
  public void Descriptor_AdvertisesCanModify_AndImplementsInterface() {
    var d = new GemdosFormatDescriptor();
    Assert.That(d, Is.InstanceOf<IArchiveModifiable>());
    // Genuine in-place R/W: add/remove edit the FAT12 structures directly (FatModifier /
    // FatRemover). The verb works in place; rebuild is only a structural fallback.
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.True);
  }

  [Test, Category("RoundTrip")]
  public void Descriptor_AddViaInterface_AndRemove() {
    var d = new GemdosFormatDescriptor();
    using var image = BuildBaselineImage(("A.TXT", "AAA"));
    ((IArchiveModifiable)d).Add(image,
      [ArchiveInputInfo.InMemory("VIAIF.TXT", Encoding.ASCII.GetBytes("via-if"))]);
    Assert.That(ListNames(image), Does.Contain("VIAIF.TXT"));

    ((IArchiveModifiable)d).Remove(image, ["VIAIF.TXT"]);
    Assert.That(ListNames(image), Does.Not.Contain("VIAIF.TXT"));
  }

  // ── Mutate-then-extract roundtrip ───────────────────────────────────────

  [Test, Category("RoundTrip")]
  public void MutateThenExtract_Roundtrip() {
    using var image = BuildBaselineImage(("KEEP.TXT", "kept"));
    GemdosInPlaceModifier.AddFiles(image, [("ADD1.TXT", Encoding.ASCII.GetBytes("payload-1"))]);
    GemdosInPlaceModifier.AddFiles(image, [("ADD2.TXT", Encoding.ASCII.GetBytes("payload-2"))]);
    GemdosInPlaceModifier.RemoveFiles(image, ["KEEP.TXT"]);

    var names = ListNames(image);
    Assert.That(names, Does.Contain("ADD1.TXT"));
    Assert.That(names, Does.Contain("ADD2.TXT"));
    Assert.That(names, Does.Not.Contain("KEEP.TXT"));
    Assert.That(Encoding.ASCII.GetString(ReadEntry(image, "ADD1.TXT")), Is.EqualTo("payload-1"));
    Assert.That(Encoding.ASCII.GetString(ReadEntry(image, "ADD2.TXT")), Is.EqualTo("payload-2"));
  }
}
