#pragma warning disable CS1591
using System.Text;
using Compression.Registry;
using FileSystem.GsOs;

namespace Compression.Tests.GsOs;

/// <summary>
/// In-place modify tests for the GS/OS 2IMG descriptor. The 2IMG container
/// is a 64-byte header at offset 0 followed by a ProDOS payload; mutation
/// is delegated to <see cref="FileSystem.ProDos.ProDosModifier"/>, so the
/// 2IMG header bytes must stay byte-identical across Add/Remove operations.
/// </summary>
[TestFixture]
public class GsOsInPlaceModifyTests {

  private const int HeaderSize = 64;

  private static MemoryStream BuildBaselineImage(params (string Name, string Body)[] entries) {
    // Build an EMPTY 2IMG image first, then add files via the modifier.
    // (The ProDosWriter + ProDosModifier sibling pair shares the volume-dir
    // header byte layout for an empty volume but not for a writer-emitted
    // file entry; tunnelling the baseline files through the modifier avoids
    // that pre-existing layout mismatch.)
    var w = new GsOsWriter();
    var image = w.Build();
    var ms = new MemoryStream();
    ms.Write(image, 0, image.Length);
    ms.Position = 0;
    foreach (var (name, body) in entries)
      GsOsInPlaceModifier.AddFile(ms, name, Encoding.ASCII.GetBytes(body));
    return ms;
  }

  private static IReadOnlyList<string> ListProDosNames(Stream image) {
    image.Position = 0;
    using var r = new FileSystem.ProDos.ProDosReader(image);
    return r.Entries.Where(e => !e.IsDirectory).Select(e => e.Name).ToList();
  }

  private static byte[] ReadProDosEntry(Stream image, string name) {
    image.Position = 0;
    using var r = new FileSystem.ProDos.ProDosReader(image);
    var e = r.Entries.Single(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
    return r.Extract(e);
  }

  private static byte[] HeaderBytes(MemoryStream image) {
    var arr = image.ToArray();
    var buf = new byte[HeaderSize];
    Buffer.BlockCopy(arr, 0, buf, 0, HeaderSize);
    return buf;
  }

  // ── Add ─────────────────────────────────────────────────────────────────

  [Test, Category("RoundTrip")]
  public void Add_NewFile_ReadsBackThroughProDos() {
    using var image = BuildBaselineImage(("ALPHA", "AAA"));
    GsOsInPlaceModifier.AddFile(image, "BETA", Encoding.ASCII.GetBytes("BBB"));
    Assert.That(ListProDosNames(image), Does.Contain("BETA"));
    Assert.That(Encoding.ASCII.GetString(ReadProDosEntry(image, "BETA")), Is.EqualTo("BBB"));
  }

  [Test, Category("RoundTrip")]
  public void Add_PreservesHeaderBytes_ByteForByte() {
    using var image = BuildBaselineImage(("ALPHA", "AAA"));
    var headerBefore = HeaderBytes(image);

    GsOsInPlaceModifier.AddFile(image, "BETA", Encoding.ASCII.GetBytes("BBB"));

    var headerAfter = HeaderBytes(image);
    Assert.That(headerAfter, Is.EqualTo(headerBefore),
      "2IMG header bytes 0..63 must be byte-identical after Add.");
  }

  [Test, Category("RoundTrip")]
  public void Add_PreservesExistingFile() {
    using var image = BuildBaselineImage(("KEEP", "KEEPME"));
    var keepBefore = ReadProDosEntry(image, "KEEP");
    GsOsInPlaceModifier.AddFile(image, "NEW", new byte[] { 9, 9, 9 });
    var keepAfter = ReadProDosEntry(image, "KEEP");
    Assert.That(keepAfter, Is.EqualTo(keepBefore));
  }

  // ── Remove ──────────────────────────────────────────────────────────────

  [Test, Category("RoundTrip")]
  public void Remove_NamedFile_Disappears() {
    using var image = BuildBaselineImage(("A", "AAA"), ("B", "BBB"));
    Assert.That(GsOsInPlaceModifier.RemoveFile(image, "A"), Is.True);
    Assert.That(ListProDosNames(image), Does.Not.Contain("A"));
    Assert.That(ListProDosNames(image), Does.Contain("B"));
  }

  [Test, Category("RoundTrip")]
  public void Remove_PreservesHeaderBytes_ByteForByte() {
    using var image = BuildBaselineImage(("A", "AAA"), ("B", "BBB"));
    var headerBefore = HeaderBytes(image);
    GsOsInPlaceModifier.RemoveFile(image, "A");
    var headerAfter = HeaderBytes(image);
    Assert.That(headerAfter, Is.EqualTo(headerBefore),
      "2IMG header bytes 0..63 must be byte-identical after Remove.");
  }

  [Test, Category("RoundTrip")]
  public void Remove_UnknownName_ReturnsFalse() {
    using var image = BuildBaselineImage(("A", "AAA"));
    Assert.That(GsOsInPlaceModifier.RemoveFile(image, "GHOST"), Is.False);
  }

  // ── Non-ProDOS payload guard ───────────────────────────────────────────

  [Test, Category("Spec")]
  public void Add_NonProDosPayload_Rejected() {
    using var image = BuildBaselineImage(("A", "AAA"));
    // Patch the image_format field at offset 12 to DOS 3.3 (value 0).
    image.Position = 12;
    image.Write(new byte[] { 0, 0, 0, 0 }, 0, 4);
    Assert.Throws<NotSupportedException>(() =>
      GsOsInPlaceModifier.AddFile(image, "BETA", new byte[] { 1 }));
  }

  // ── Descriptor interface routing ────────────────────────────────────────

  [Test, Category("Spec")]
  public void Descriptor_AdvertisesCanCreate_IsWormNotRw_AndImplementsInterfaces() {
    var d = new GsOsFormatDescriptor();
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanCreate), Is.True);
    // Add/Remove rebuild the inner volume via ProDosWriter (read-all -> re-create) =
    // WORM, not in-place R/W: CanModify must not be advertised.
    Assert.That(d.Capabilities.HasFlag(FormatCapabilities.CanModify), Is.False);
    Assert.That(d, Is.InstanceOf<IArchiveCreatable>());
    Assert.That(d, Is.InstanceOf<IArchiveModifiable>());
  }

  [Test, Category("RoundTrip")]
  public void Descriptor_Create_ThenList_RoundTrips() {
    var d = new GsOsFormatDescriptor();
    using var output = new MemoryStream();
    ((IArchiveCreatable)d).Create(output,
      [ArchiveInputInfo.InMemory("HELLO", Encoding.ASCII.GetBytes("hello-gsos"))],
      new FormatCreateOptions());
    output.Position = 0;
    var entries = d.List(output, null);
    Assert.That(entries.Select(e => e.Name), Does.Contain("HELLO"));
  }

  [Test, Category("RoundTrip")]
  public void Descriptor_AddViaInterface_AndRemove() {
    var d = new GsOsFormatDescriptor();
    using var image = BuildBaselineImage(("A", "AAA"));
    ((IArchiveModifiable)d).Add(image,
      [ArchiveInputInfo.InMemory("VIAIF", Encoding.ASCII.GetBytes("via-if"))]);
    Assert.That(ListProDosNames(image), Does.Contain("VIAIF"));

    ((IArchiveModifiable)d).Remove(image, ["VIAIF"]);
    Assert.That(ListProDosNames(image), Does.Not.Contain("VIAIF"));
  }

  // ── Mutate-then-extract roundtrip ───────────────────────────────────────

  [Test, Category("RoundTrip")]
  public void MutateThenExtract_Roundtrip() {
    using var image = BuildBaselineImage(("KEEP", "kept"));
    GsOsInPlaceModifier.AddFile(image, "ADD1", Encoding.ASCII.GetBytes("payload-1"));
    GsOsInPlaceModifier.AddFile(image, "ADD2", Encoding.ASCII.GetBytes("payload-2"));
    GsOsInPlaceModifier.RemoveFile(image, "KEEP");

    var names = ListProDosNames(image);
    Assert.That(names, Does.Contain("ADD1"));
    Assert.That(names, Does.Contain("ADD2"));
    Assert.That(names, Does.Not.Contain("KEEP"));
    Assert.That(Encoding.ASCII.GetString(ReadProDosEntry(image, "ADD1")), Is.EqualTo("payload-1"));
    Assert.That(Encoding.ASCII.GetString(ReadProDosEntry(image, "ADD2")), Is.EqualTo("payload-2"));
  }
}
