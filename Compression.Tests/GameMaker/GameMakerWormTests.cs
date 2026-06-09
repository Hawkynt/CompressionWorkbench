#pragma warning disable CS1591
using System.Buffers.Binary;
using Compression.Registry;
using FileFormat.GameMaker;

namespace Compression.Tests.GameMaker;

/// <summary>
/// WORM contract tests for the GameMaker FORM container writer. Inputs named
/// <c>chunks/&lt;TAG&gt;.bin</c> are emitted as IFF-style chunks under a single
/// FORM root and must round-trip byte-for-byte through the descriptor's
/// listing path.
/// </summary>
[TestFixture]
public class GameMakerWormTests {

  private static byte[] CreateArchive(IEnumerable<(string Name, byte[] Data)> entries) {
    var d = new GameMakerFormatDescriptor();
    var inputs = entries.Select(e => ArchiveInputInfo.InMemory(e.Name, e.Data)).ToList();
    using var ms = new MemoryStream();
    d.Create(ms, inputs, new FormatCreateOptions());
    return ms.ToArray();
  }

  [Test, Category("HappyPath")]
  public void Capabilities_IncludeCanCreate() {
    var d = new GameMakerFormatDescriptor();
    Assert.That(d.Capabilities & FormatCapabilities.CanCreate, Is.EqualTo(FormatCapabilities.CanCreate));
    Assert.That(d, Is.InstanceOf<IArchiveCreatable>());
  }

  [Test, Category("HappyPath")]
  public void Create_EmitsFormMagicAndChunkTags() {
    var gen8 = new byte[] { 0x00, 0x01, 0x02, 0x03, 0x04 };
    var strg = new byte[] { 0xAA, 0xBB };
    var bytes = CreateArchive([
      ("chunks/GEN8.bin", gen8),
      ("chunks/STRG.bin", strg),
    ]);

    // FORM magic
    Assert.That(bytes[0], Is.EqualTo((byte)'F'));
    Assert.That(bytes[1], Is.EqualTo((byte)'O'));
    Assert.That(bytes[2], Is.EqualTo((byte)'R'));
    Assert.That(bytes[3], Is.EqualTo((byte)'M'));

    // FORM body size = 2 chunks × 8-byte header + 5 + 2 = 23
    var formSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4, 4));
    Assert.That(formSize, Is.EqualTo(2 * 8 + gen8.Length + strg.Length));

    // First chunk tag at offset 8
    Assert.That(bytes.AsSpan(8, 4).SequenceEqual("GEN8"u8), Is.True);
  }

  [Test, Category("HappyPath"), Category("RoundTrip")]
  public void Create_RoundTripsViaList() {
    var gen8 = Enumerable.Range(0, 0x40).Select(i => (byte)i).ToArray();
    var sond = new byte[] { 0x11, 0x22, 0x33, 0x44, 0x55 };
    var bytes = CreateArchive([
      ("chunks/GEN8.bin", gen8),
      ("chunks/SOND.bin", sond),
    ]);

    var d = new GameMakerFormatDescriptor();
    using var ms = new MemoryStream(bytes);
    var entries = d.List(ms, password: null);
    var names = entries.Select(e => e.Name).ToList();

    Assert.That(names, Does.Contain("chunks/GEN8.bin"));
    Assert.That(names, Does.Contain("chunks/SOND.bin"));
  }

  [Test, Category("EdgeCase")]
  public void Create_EmptyInputs_EmitsMinimalFormWithStubGen8() {
    var bytes = CreateArchive([]);

    Assert.That(bytes.AsSpan(0, 4).SequenceEqual("FORM"u8), Is.True);
    Assert.That(bytes.AsSpan(8, 4).SequenceEqual("GEN8"u8), Is.True);

    // Stub GEN8 chunk should be 0x40 zero bytes
    var chunkSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12, 4));
    Assert.That(chunkSize, Is.EqualTo(0x40));
  }

  [Test, Category("HappyPath")]
  public void Create_IgnoresDerivedSurfaceInputs() {
    // FULL.win / metadata.ini / strings.txt / textures/* / audio/* are derived
    // surfaces produced by the reader, not real chunks — they must be silently
    // ignored so we don't smuggle them in as bogus chunks.
    var bytes = CreateArchive([
      ("FULL.win", new byte[] { 1, 2, 3 }),
      ("metadata.ini", new byte[] { 4, 5, 6 }),
      ("strings.txt", new byte[] { 7 }),
      ("textures/0001.png", new byte[] { 8 }),
      ("chunks/GEN8.bin", new byte[] { 9, 10 }),
    ]);

    // Body should contain only one 8-byte chunk header + 2 bytes
    var formSize = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4, 4));
    Assert.That(formSize, Is.EqualTo(8 + 2));
    Assert.That(bytes.AsSpan(8, 4).SequenceEqual("GEN8"u8), Is.True);
  }
}
