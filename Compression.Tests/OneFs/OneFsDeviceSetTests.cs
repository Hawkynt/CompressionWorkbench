using FileSystem.OneFs;

namespace Compression.Tests.OneFs;

/// <summary>
/// Tests the multi-device bootstrap model without inventing proprietary OneFS
/// bytes. Dell publishes the physical block/cylinder-group geometry, so these
/// tests assert only length-derived facts and fail if inspection touches payload.
/// </summary>
[TestFixture]
public sealed class OneFsDeviceSetTests {

  [Test, Category("HappyPath")]
  public void Open_ReportsHeterogeneousDeviceGeometryWithoutReadingPayload() {
    var first = new GeometryOnlyStream(2L * OneFsReader.CylinderGroupSize, position: 123);
    var secondLength = OneFsReader.CylinderGroupSize + 3L * OneFsReader.PhysicalBlockSize + 17;
    var second = new GeometryOnlyStream(secondLength, position: 456);

    var set = OneFsDeviceSet.Open([first, second]);

    Assert.Multiple(() => {
      Assert.That(set.Devices, Has.Count.EqualTo(2));
      Assert.That(set.TotalImageSize, Is.EqualTo(first.Length + second.Length));
      Assert.That(set.TotalCompleteBlockCount,
        Is.EqualTo(first.Length / OneFsReader.PhysicalBlockSize + second.Length / OneFsReader.PhysicalBlockSize));
      Assert.That(set.TotalCompleteCylinderGroupCount, Is.EqualTo(3));
      Assert.That(set.AllDevicesBlockAligned, Is.False);
      Assert.That(set.AllDevicesCylinderGroupAligned, Is.False);

      Assert.That(set.Devices[0], Is.EqualTo(new OneFsDeviceGeometry(
        0,
        first.Length,
        2L * OneFsReader.BlocksPerCylinderGroup,
        0,
        2,
        0)));
      Assert.That(set.Devices[1], Is.EqualTo(new OneFsDeviceGeometry(
        1,
        second.Length,
        OneFsReader.BlocksPerCylinderGroup + 3L,
        17,
        1,
        3 * OneFsReader.PhysicalBlockSize + 17)));

      Assert.That(first.Position, Is.EqualTo(123));
      Assert.That(second.Position, Is.EqualTo(456));
      Assert.That(first.ReadCalls, Is.Zero);
      Assert.That(second.ReadCalls, Is.Zero);
      Assert.That(set.GetDeviceStream(0), Is.SameAs(first));
      Assert.That(set.GetDeviceStream(1), Is.SameAs(second));
    });
  }

  [Test, Category("HappyPath")]
  public void Open_AllowsDifferentWholeCylinderGroupCounts() {
    var small = new GeometryOnlyStream(OneFsReader.CylinderGroupSize);
    var large = new GeometryOnlyStream(7L * OneFsReader.CylinderGroupSize);

    var set = OneFsDeviceSet.Open([small, large]);

    Assert.Multiple(() => {
      Assert.That(set.Devices.Select(device => device.CompleteCylinderGroupCount), Is.EqualTo(new long[] { 1, 7 }));
      Assert.That(set.AllDevicesBlockAligned, Is.True);
      Assert.That(set.AllDevicesCylinderGroupAligned, Is.True);
    });
  }

  [Test, Category("Malformed")]
  public void Open_RejectsMissingOrInvalidCandidateDevices() {
    var duplicate = new GeometryOnlyStream(OneFsReader.PhysicalBlockSize);

    Assert.Multiple(() => {
      Assert.That(() => OneFsDeviceSet.Open([]), Throws.TypeOf<ArgumentException>());
      Assert.That(() => OneFsDeviceSet.Open([new GeometryOnlyStream(0)]), Throws.TypeOf<InvalidDataException>());
      Assert.That(() => OneFsDeviceSet.Open([new GeometryOnlyStream(1, canRead: false)]), Throws.TypeOf<ArgumentException>());
      Assert.That(() => OneFsDeviceSet.Open([new GeometryOnlyStream(1, canSeek: false)]), Throws.TypeOf<ArgumentException>());
      Assert.That(
        () => OneFsDeviceSet.Open([duplicate, duplicate]),
        Throws.TypeOf<ArgumentException>(),
        "One stream object cannot safely represent two independent OneFS members with separate cursors/locks.");
    });
  }

  [Test, Category("EdgeCase")]
  public void Open_UsesCheckedAggregateGeometry() {
    var enormous = new GeometryOnlyStream(long.MaxValue);
    var oneByte = new GeometryOnlyStream(1);

    Assert.That(() => OneFsDeviceSet.Open([enormous, oneByte]), Throws.TypeOf<OverflowException>());
    Assert.Multiple(() => {
      Assert.That(enormous.ReadCalls, Is.Zero);
      Assert.That(oneByte.ReadCalls, Is.Zero);
    });
  }

  private sealed class GeometryOnlyStream : Stream {
    private readonly long _length;
    private readonly bool _canRead;
    private readonly bool _canSeek;
    private long _position;

    internal GeometryOnlyStream(long length, long position = 0, bool canRead = true, bool canSeek = true) {
      this._length = length;
      this._position = position;
      this._canRead = canRead;
      this._canSeek = canSeek;
    }

    internal int ReadCalls { get; private set; }

    public override bool CanRead => this._canRead;
    public override bool CanSeek => this._canSeek;
    public override bool CanWrite => false;
    public override long Length => this._length;

    public override long Position {
      get => this._position;
      set => this._position = value;
    }

    public override void Flush() { }

    public override int Read(byte[] buffer, int offset, int count) {
      ++this.ReadCalls;
      throw new InvalidOperationException("Geometry inspection must not read candidate OneFS payload bytes.");
    }

    public override long Seek(long offset, SeekOrigin origin)
      => throw new InvalidOperationException("Geometry inspection must not seek candidate OneFS payload bytes.");

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
  }
}
