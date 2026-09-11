using System.Buffers.Binary;
using Compression.Registry;
using FileFormat.Nrg;

namespace Compression.Tests.Nrg;

[TestFixture]
public class NrgAuthoringTests {
  private const int CookedSectorSize = 2048;
  private const int AudioSectorSize = 2352;

  [Test, Category("RoundTrip")]
  public void Write_MixedModeDao_PreservesAudioAndFindsDataTrack() {
    var audio = Pattern(AudioSectorSize * 3, 17);
    var iso = BuildIso("SECOND.BIN", "second-track"u8.ToArray());
    var disc = new NrgDiscDefinition([
      new NrgSessionDefinition([
        new NrgTrackDefinition(NrgTrackMode.Audio, audio) {
          PregapSectors = 150,
          Isrc = "USABC2600001",
        },
        new NrgTrackDefinition(NrgTrackMode.Mode1, iso),
      ]) { Mcn = "1234567890123" },
    ]);

    var image = Write(disc);
    var chunks = ReadChunks(image);
    Assert.That(chunks.Select(static chunk => chunk.Id), Is.EqualTo(new[] { "CUEX", "DAOX", "SINF", "MTYP", "END!" }));

    var cuex = chunks.Single(static chunk => chunk.Id == "CUEX").Payload;
    Assert.That(cuex.Length, Is.EqualTo(48), "lead-in + two indices per track + lead-out");
    // The program area starts at LBA 0 with the first track's index 1, so the 150-sector
    // pregap in front of it occupies the negative LBAs the lead-in also lives in.
    var audioSectors = audio.Length / AudioSectorSize;
    AssertCue(cuex, 0, adrCtl: 0x01, track: 0x00, index: 0x00, lba: -150);
    AssertCue(cuex, 1, adrCtl: 0x01, track: 0x01, index: 0x00, lba: -150);
    AssertCue(cuex, 2, adrCtl: 0x01, track: 0x01, index: 0x01, lba: 0);
    AssertCue(cuex, 3, adrCtl: 0x41, track: 0x02, index: 0x00, lba: audioSectors);
    AssertCue(cuex, 4, adrCtl: 0x41, track: 0x02, index: 0x01, lba: audioSectors);
    AssertCue(cuex, 5, adrCtl: 0x41, track: 0xAA, index: 0x01, lba: audioSectors + iso.Length / CookedSectorSize);

    var daox = chunks.Single(static chunk => chunk.Id == "DAOX").Payload;
    Assert.That(daox.Length, Is.EqualTo(22 + 2 * 42));
    Assert.Multiple(() => {
      Assert.That(BinaryPrimitives.ReadUInt32BigEndian(daox), Is.EqualTo((uint)daox.Length));
      Assert.That(System.Text.Encoding.ASCII.GetString(daox, 4, 13), Is.EqualTo("1234567890123"));
      Assert.That(daox[20], Is.EqualTo(1));
      Assert.That(daox[21], Is.EqualTo(2));
    });

    var audioRecord = daox.AsSpan(22, 42);
    var dataRecord = daox.AsSpan(64, 42);
    var audioPregap = checked((long)BinaryPrimitives.ReadUInt64BigEndian(audioRecord[18..]));
    var audioStart = checked((long)BinaryPrimitives.ReadUInt64BigEndian(audioRecord[26..]));
    var audioEnd = checked((long)BinaryPrimitives.ReadUInt64BigEndian(audioRecord[34..]));
    var dataStart = checked((long)BinaryPrimitives.ReadUInt64BigEndian(dataRecord[26..]));
    var dataEnd = checked((long)BinaryPrimitives.ReadUInt64BigEndian(dataRecord[34..]));

    Assert.That(BinaryPrimitives.ReadUInt16BigEndian(audioRecord[12..]), Is.EqualTo(AudioSectorSize));
    Assert.That(audioRecord[14], Is.EqualTo((byte)NrgTrackMode.Audio));
    Assert.That(System.Text.Encoding.ASCII.GetString(audioRecord[..12]), Is.EqualTo("USABC2600001"));
    Assert.That(BinaryPrimitives.ReadUInt16BigEndian(dataRecord[12..]), Is.EqualTo(CookedSectorSize));
    Assert.That(dataRecord[14], Is.EqualTo((byte)NrgTrackMode.Mode1));
    Assert.Multiple(() => {
      Assert.That(audioPregap, Is.Zero);
      Assert.That(audioStart - audioPregap, Is.EqualTo(150L * AudioSectorSize));
      Assert.That(audioEnd - audioStart, Is.EqualTo(audio.Length));
      Assert.That(dataStart, Is.EqualTo(audioEnd));
      Assert.That(dataEnd - dataStart, Is.EqualTo(iso.Length));
    });

    Assert.That(image.AsSpan(checked((int)audioStart), audio.Length).ToArray(), Is.EqualTo(audio));
    Assert.That(image.AsSpan(checked((int)dataStart + 16 * CookedSectorSize), 6).ToArray(),
      Is.EqualTo(new byte[] { 1, (byte)'C', (byte)'D', (byte)'0', (byte)'0', (byte)'1' }));

    using var stream = new MemoryStream(image, writable: false);
    using var reader = new NrgReader(stream);
    var entry = reader.Entries.Single(e => !e.IsDirectory && e.Name.Equals("SECOND.BIN", StringComparison.OrdinalIgnoreCase));
    Assert.That(reader.Extract(entry), Is.EqualTo("second-track"u8.ToArray()));
  }

  [Test, Category("RoundTrip")]
  public void Write_MultipleSessions_UsesCdLeadOutAndLeadInSpacing() {
    var firstIso = BuildIso("FIRST.BIN", "first"u8.ToArray());
    var secondIso = BuildIso("SECOND.BIN", "second"u8.ToArray());
    var image = Write(new NrgDiscDefinition([
      new NrgSessionDefinition([
        new NrgTrackDefinition(NrgTrackMode.Mode1, firstIso),
      ]),
      new NrgSessionDefinition([
        new NrgTrackDefinition(NrgTrackMode.Mode1, secondIso),
      ]),
    ]));

    var chunks = ReadChunks(image);
    Assert.That(chunks.Select(static chunk => chunk.Id), Is.EqualTo(new[] {
      "CUEX", "DAOX", "SINF", "CUEX", "DAOX", "SINF", "MTYP", "END!",
    }));

    var cueSessions = chunks.Where(static chunk => chunk.Id == "CUEX").Select(static chunk => chunk.Payload).ToArray();
    var firstLeadOut = CueLba(cueSessions[0], 3);
    var secondLeadIn = CueLba(cueSessions[1], 0);
    var secondTrackIndex0 = CueLba(cueSessions[1], 1);
    var secondTrackIndex1 = CueLba(cueSessions[1], 2);

    Assert.Multiple(() => {
      Assert.That(secondLeadIn, Is.EqualTo(firstLeadOut + 6750), "first session has the long CD lead-out");
      Assert.That(secondTrackIndex0, Is.EqualTo(secondLeadIn + 4500), "next session starts after its lead-in");
      Assert.That(secondTrackIndex1, Is.EqualTo(secondTrackIndex0));
      Assert.That(cueSessions[0][9], Is.EqualTo(0x01), "first session uses track 1 BCD");
      Assert.That(cueSessions[1][9], Is.EqualTo(0x02), "track numbering remains global across sessions");
    });

    var sinf = chunks.Where(static chunk => chunk.Id == "SINF").ToArray();
    Assert.That(sinf, Has.Length.EqualTo(2));
    Assert.That(BinaryPrimitives.ReadUInt32BigEndian(sinf[0].Payload), Is.EqualTo(1));
    Assert.That(BinaryPrimitives.ReadUInt32BigEndian(sinf[1].Payload), Is.EqualTo(1));
  }

  [Test, Category("RoundTrip")]
  public void Write_CdText_WritesRaw18BytePacks() {
    var cdText = Pattern(36, 91);
    var image = Write(new NrgDiscDefinition([
      new NrgSessionDefinition([
        new NrgTrackDefinition(NrgTrackMode.Audio, Pattern(AudioSectorSize, 33)),
      ]),
    ]) { CdText = cdText });

    var chunks = ReadChunks(image);
    Assert.That(chunks.Select(static chunk => chunk.Id), Is.EqualTo(new[] { "CUEX", "DAOX", "SINF", "CDTX", "MTYP", "END!" }));
    Assert.That(chunks.Single(static chunk => chunk.Id == "CDTX").Payload, Is.EqualTo(cdText));
  }

  [Test, Category("Boundary")]
  public void Write_RejectsMisalignedAudioTrack() {
    using var output = new MemoryStream();
    var disc = new NrgDiscDefinition([
      new NrgSessionDefinition([
        new NrgTrackDefinition(NrgTrackMode.Audio, new byte[AudioSectorSize + 1]),
      ]),
    ]);

    Assert.Throws<ArgumentException>(() => NrgWriter.Write(output, disc));
  }

  [Test, Category("Boundary")]
  public void Write_RejectsMoreThan99Tracks() {
    var tracks = Enumerable.Range(0, 100)
      .Select(static _ => new NrgTrackDefinition(NrgTrackMode.Audio, new byte[AudioSectorSize]))
      .ToArray();
    using var output = new MemoryStream();
    Assert.Throws<ArgumentException>(() => NrgWriter.Write(output, new NrgDiscDefinition([
      new NrgSessionDefinition(tracks),
    ])));
  }

  [Test, Category("Boundary")]
  public void Write_RejectsInvalidMcnIsrcAndCdText() {
    using var output = new MemoryStream();
    Assert.Multiple(() => {
      Assert.Throws<ArgumentException>(() => NrgWriter.Write(output, new NrgDiscDefinition([
        new NrgSessionDefinition([
          new NrgTrackDefinition(NrgTrackMode.Audio, new byte[AudioSectorSize]),
        ]) { Mcn = "123" },
      ])));

      Assert.Throws<ArgumentException>(() => NrgWriter.Write(output, new NrgDiscDefinition([
        new NrgSessionDefinition([
          new NrgTrackDefinition(NrgTrackMode.Audio, new byte[AudioSectorSize]) { Isrc = "short" },
        ]),
      ])));

      Assert.Throws<ArgumentException>(() => NrgWriter.Write(output, new NrgDiscDefinition([
        new NrgSessionDefinition([
          new NrgTrackDefinition(NrgTrackMode.Audio, new byte[AudioSectorSize]),
        ]),
      ]) { CdText = new byte[17] }));
    });
  }

  [Test, Category("Regression")]
  public void MultiTrack_FileMutationAndDefragRefuseWithoutChangingImage() {
    var descriptor = new NrgFormatDescriptor();
    var source = Write(new NrgDiscDefinition([
      new NrgSessionDefinition([
        new NrgTrackDefinition(NrgTrackMode.Audio, Pattern(AudioSectorSize, 4)),
        new NrgTrackDefinition(NrgTrackMode.Mode1, BuildIso("KEEP.BIN", "keep"u8.ToArray())),
      ]),
    ]));

    using var image = new MemoryStream();
    image.Write(source);
    image.Position = 0;

    Assert.Throws<NotSupportedException>(() => ((IArchiveModifiable)descriptor).Add(image, [
      ArchiveInputInfo.InMemory("NOPE.BIN", "nope"u8.ToArray()),
    ]));
    Assert.That(image.ToArray(), Is.EqualTo(source));

    image.Position = 0;
    Assert.Throws<NotSupportedException>(() => ((IArchiveDefragmentable)descriptor).Defragment(image));
    Assert.That(image.ToArray(), Is.EqualTo(source));
  }

  [Test, Category("Regression")]
  public void MultiTrack_ShrinkCopiesThroughByteIdentical() {
    var descriptor = new NrgFormatDescriptor();
    var source = Write(new NrgDiscDefinition([
      new NrgSessionDefinition([
        new NrgTrackDefinition(NrgTrackMode.Audio, Pattern(AudioSectorSize * 2, 19)),
        new NrgTrackDefinition(NrgTrackMode.Mode1, BuildIso("KEEP.BIN", "keep"u8.ToArray())),
      ]),
    ]));

    using var input = new MemoryStream(source, writable: false);
    using var output = new MemoryStream();
    ((IArchiveShrinkable)descriptor).Shrink(input, output);
    Assert.That(output.ToArray(), Is.EqualTo(source));
  }

  private static byte[] BuildIso(string name, byte[] data) {
    var writer = new FileSystem.Iso.IsoWriter();
    writer.AddFile(name, data);
    return writer.Build();
  }

  private static byte[] Write(NrgDiscDefinition disc) {
    using var output = new MemoryStream();
    NrgWriter.Write(output, disc);
    return output.ToArray();
  }

  private static byte[] Pattern(int length, int multiplier)
    => Enumerable.Range(0, length).Select(i => unchecked((byte)(i * multiplier + 3))).ToArray();

  private sealed record Chunk(string Id, byte[] Payload);

  private static List<Chunk> ReadChunks(byte[] image) {
    Assert.That(image.AsSpan(image.Length - 12, 4).ToArray(), Is.EqualTo("NER5"u8.ToArray()));
    var position = checked((int)BinaryPrimitives.ReadUInt64BigEndian(image.AsSpan(image.Length - 8)));
    var footerOffset = image.Length - 12;
    var chunks = new List<Chunk>();
    while (position <= footerOffset - 8) {
      var id = System.Text.Encoding.ASCII.GetString(image, position, 4);
      var length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(image.AsSpan(position + 4, 4)));
      Assert.That(position + 8L + length, Is.LessThanOrEqualTo(footerOffset));
      var payload = image.AsSpan(position + 8, length).ToArray();
      chunks.Add(new Chunk(id, payload));
      position += 8 + length;
      if (id == "END!")
        break;
    }
    Assert.That(position, Is.EqualTo(footerOffset));
    return chunks;
  }

  private static void AssertCue(byte[] payload, int entryIndex, byte adrCtl, byte track, byte index, int lba) {
    var offset = entryIndex * 8;
    Assert.Multiple(() => {
      Assert.That(payload[offset], Is.EqualTo(adrCtl));
      Assert.That(payload[offset + 1], Is.EqualTo(track));
      Assert.That(payload[offset + 2], Is.EqualTo(index));
      Assert.That(CueLba(payload, entryIndex), Is.EqualTo(lba));
    });
  }

  private static int CueLba(byte[] payload, int entryIndex)
    => unchecked((int)BinaryPrimitives.ReadUInt32BigEndian(payload.AsSpan(entryIndex * 8 + 4, 4)));
}
