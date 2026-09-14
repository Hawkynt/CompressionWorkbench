using System.Buffers.Binary;
using FileFormat.Nrg;

namespace Compression.Tests.Nrg;

[TestFixture]
public class NrgAbsoluteMultisessionTests {
  private const int IsoSectorSize = 2048;

  [Test, Category("Regression")]
  public void LaterSession_AbsoluteIsoExtents_AreResolvedAgainstTrackIndexOne() {
    var image = Write(new NrgDiscDefinition([
      new NrgSessionDefinition([
        new NrgTrackDefinition(NrgTrackMode.Mode1, BuildIso("FIRST.BIN", "first"u8.ToArray())),
      ]),
      new NrgSessionDefinition([
        new NrgTrackDefinition(NrgTrackMode.Mode1, BuildIso("SECOND.BIN", "second-session"u8.ToArray())),
      ]),
    ]));

    NrgTrackInfo secondTrack;
    using (var initialStream = new MemoryStream(image, writable: false))
    using (var initialReader = new NrgReader(initialStream)) {
      secondTrack = initialReader.Tracks.Single(static track => track.SessionNumber == 2);
      Assert.That(secondTrack.Index1Lba, Is.GreaterThan(0));
    }

    RewriteSecondIsoToAbsoluteExtents(image, secondTrack);

    using var stream = new MemoryStream(image, writable: false);
    using var reader = new NrgReader(stream);
    var entry = reader.Entries.Single(entry =>
      entry.SessionNumber == 2 && entry.Name.Equals("SECOND.BIN", StringComparison.OrdinalIgnoreCase));

    Assert.Multiple(() => {
      Assert.That(entry.FullPath, Is.EqualTo(".nrg/session-02/track-02/iso/SECOND.BIN").IgnoreCase);
      Assert.That(entry.StartLba, Is.GreaterThan(secondTrack.Index1Lba!.Value));
      Assert.That(reader.Extract(entry), Is.EqualTo("second-session"u8.ToArray()));
    });
  }

  private static void RewriteSecondIsoToAbsoluteExtents(byte[] image, NrgTrackInfo track) {
    var baseLba = track.Index1Lba ?? throw new AssertionException("Second session has no index-1 LBA.");
    if (baseLba <= 0)
      throw new AssertionException("Second session index-1 LBA must be positive.");

    var pvdOffset = checked((int)(track.DataOffset + 16L * track.SectorSize + track.UserDataOffset));
    var pvd = image.AsSpan(pvdOffset, IsoSectorSize);
    Assert.That(pvd[0], Is.EqualTo(1));
    Assert.That(pvd.Slice(1, 5).SequenceEqual("CD001"u8), Is.True);

    var relativeRootLba = BinaryPrimitives.ReadUInt32LittleEndian(pvd.Slice(158, 4));
    var rootSize = BinaryPrimitives.ReadUInt32LittleEndian(pvd.Slice(166, 4));
    WriteBothEndianExtent(pvd.Slice(158, 8), checked(relativeRootLba + (uint)baseLba));

    var consumed = 0L;
    while (consumed < rootSize) {
      var sectorIndex = consumed / IsoSectorSize;
      var sectorOffset = checked((int)(
        track.DataOffset + ((long)relativeRootLba + sectorIndex) * track.SectorSize + track.UserDataOffset));
      var sector = image.AsSpan(sectorOffset, IsoSectorSize);
      var withinSector = 0;

      while (withinSector < IsoSectorSize && consumed + withinSector < rootSize) {
        var recordLength = sector[withinSector];
        if (recordLength == 0)
          break;
        if (recordLength < 34 || withinSector + recordLength > IsoSectorSize)
          throw new AssertionException("Generated ISO contains an invalid root-directory record.");

        var record = sector.Slice(withinSector, recordLength);
        var idLength = record[32];
        var isDotEntry = idLength == 1 && (record[33] == 0x00 || record[33] == 0x01);
        if (!isDotEntry) {
          var relativeExtent = BinaryPrimitives.ReadUInt32LittleEndian(record.Slice(2, 4));
          WriteBothEndianExtent(record.Slice(2, 8), checked(relativeExtent + (uint)baseLba));
        }

        withinSector += recordLength;
      }

      consumed += IsoSectorSize;
    }
  }

  private static void WriteBothEndianExtent(Span<byte> destination, uint value) {
    BinaryPrimitives.WriteUInt32LittleEndian(destination[..4], value);
    BinaryPrimitives.WriteUInt32BigEndian(destination[4..8], value);
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
}
