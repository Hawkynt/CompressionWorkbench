using FileSystem.OneFs;

namespace Compression.Tests.OneFs;

/// <summary>
/// Dell KB 000022978 explicitly decodes an IDI tuple of the form
/// <c>devid,Lnum,block-address:length</c>. These tests pin that documented text
/// representation only; they do not infer the proprietary on-disk ifs_baddr_t layout.
/// </summary>
[TestFixture]
public sealed class OneFsDiagnosticAddressTests {

  [Test, Category("Interop")]
  public void TryParse_ParsesDellPublishedIdiAddress() {
    Assert.That(
      OneFsDiagnosticBlockAddress.TryParse("6,3,669847150592:8192", out var address),
      Is.True);

    Assert.Multiple(() => {
      Assert.That(address.Device, Is.EqualTo(new OneFsDeviceIdentity(6, 3)));
      Assert.That(address.ByteOffset, Is.EqualTo(669847150592L));
      Assert.That(address.Length, Is.EqualTo(8192));
      Assert.That(address.IsFilesystemBlockAligned, Is.True);
      Assert.That(address.BlockIndex, Is.EqualTo(81768451L));
      Assert.That(address.BlockCount, Is.EqualTo(1));
      Assert.That(address.ToString(), Is.EqualTo("6,3,669847150592:8192"));
    });
  }

  [Test, Category("Interop")]
  public void TryParse_PreservesDellPublished512ByteInodeAddressWithoutPromotingItToADataBlock() {
    Assert.That(
      OneFsDiagnosticBlockAddress.TryParse("92,14,524557565440:512", out var address),
      Is.True);

    Assert.Multiple(() => {
      Assert.That(address.Device, Is.EqualTo(new OneFsDeviceIdentity(92, 14)));
      Assert.That(address.ByteOffset, Is.EqualTo(524557565440L));
      Assert.That(address.Length, Is.EqualTo(512));
      Assert.That(address.IsFilesystemBlockAligned, Is.False);
      Assert.That(address.BlockIndex, Is.Null,
        "A 512-byte inode address need not start on an 8 KiB filesystem-block boundary.");
      Assert.That(address.BlockCount, Is.Null);
    });
  }

  [Test, Category("Interop")]
  public void IdentifiedDeviceSet_ReadsDellPublished512ByteInodeExtentExactly() {
    const long inodeOffset = 524557565440L;
    var source = new SparsePatternStream(inodeOffset + 4096, position: 37);
    var identity = new OneFsDeviceIdentity(92, 14);
    var set = OneFsDeviceSet.Open([source]).WithDeviceIdentities([identity]);
    Assert.That(
      OneFsDiagnosticBlockAddress.TryParse("92,14,524557565440:512", out var address),
      Is.True);

    var destination = new byte[512];
    set.ReadDiagnosticExtent(address, destination);

    var expected = Enumerable.Range(0, destination.Length)
      .Select(index => SparsePatternStream.Pattern(inodeOffset + index))
      .ToArray();
    Assert.Multiple(() => {
      Assert.That(destination, Is.EqualTo(expected));
      Assert.That(source.Position, Is.EqualTo(37), "Diagnostic reads must restore the caller-visible stream position.");
      Assert.That(
        () => set.ReadDiagnosticBlock(address, new byte[OneFsReader.PhysicalBlockSize]),
        Throws.TypeOf<ArgumentException>(),
        "A published 512-byte inode extent must not be silently rounded to an 8 KiB block.");
    });
  }

  [TestCase("")]
  [TestCase("6")]
  [TestCase("6,3")]
  [TestCase("6,3,4")]
  [TestCase("6,3,4:")]
  [TestCase("6,3,-1:8192")]
  [TestCase("-1,3,0:8192")]
  [TestCase("6,-1,0:8192")]
  [TestCase("6,3,0:0")]
  [TestCase("6,3,0:-1")]
  [TestCase("6,3,0:8192:1")]
  [TestCase("6,3,0:8192,1")]
  [Category("Malformed")]
  public void TryParse_RejectsMalformedDiagnosticAddress(string text)
    => Assert.That(OneFsDiagnosticBlockAddress.TryParse(text, out _), Is.False);

  [Test, Category("EdgeCase")]
  public void AlignmentHelpers_DoNotNormalizeUnalignedDiagnosticData() {
    var address = new OneFsDiagnosticBlockAddress(new OneFsDeviceIdentity(1, 9), 8193, 8191);

    Assert.Multiple(() => {
      Assert.That(address.IsFilesystemBlockAligned, Is.False);
      Assert.That(address.BlockIndex, Is.Null);
      Assert.That(address.BlockCount, Is.Null);
      Assert.That(address.ToString(), Is.EqualTo("1,9,8193:8191"));
    });
  }

  [Test, Category("HappyPath")]
  public void IdentifiedDeviceSet_ResolvesAndReadsDiagnosticBlock() {
    var firstBytes = BuildBlocks(2, seed: 11);
    var secondBytes = BuildBlocks(2, seed: 73);
    using var first = new MemoryStream(firstBytes, writable: false);
    using var second = new MemoryStream(secondBytes, writable: false);
    first.Position = 5;
    second.Position = 7;

    var set = OneFsDeviceSet.Open([first, second]).WithDeviceIdentities([
      new OneFsDeviceIdentity(1, 4),
      new OneFsDeviceIdentity(6, 3),
    ]);
    Assert.That(OneFsDiagnosticBlockAddress.TryParse("6,3,8192:8192", out var address), Is.True);

    var destination = new byte[OneFsReader.PhysicalBlockSize];
    set.ReadDiagnosticBlock(address, destination);

    Assert.Multiple(() => {
      Assert.That(set.IdentifiedDevices, Has.Count.EqualTo(2));
      Assert.That(set.TryResolveDevice(new OneFsDeviceIdentity(6, 3), out var deviceIndex), Is.True);
      Assert.That(deviceIndex, Is.EqualTo(1));
      Assert.That(destination,
        Is.EqualTo(secondBytes.AsSpan(OneFsReader.PhysicalBlockSize, OneFsReader.PhysicalBlockSize).ToArray()));
      Assert.That(first.Position, Is.EqualTo(5));
      Assert.That(second.Position, Is.EqualTo(7));
    });
  }

  [Test, Category("Malformed")]
  public void IdentifiedDeviceSet_FailsClosedForAmbiguousUnmappedOrOutOfRangeAddresses() {
    using var first = new MemoryStream(BuildBlocks(2, seed: 1), writable: false);
    using var second = new MemoryStream(BuildBlocks(2, seed: 2), writable: false);
    var set = OneFsDeviceSet.Open([first, second]);
    var identity = new OneFsDeviceIdentity(6, 3);

    Assert.Multiple(() => {
      Assert.That(
        () => set.WithDeviceIdentities([identity]),
        Throws.TypeOf<ArgumentException>());
      Assert.That(
        () => set.WithDeviceIdentities([identity, identity]),
        Throws.TypeOf<ArgumentException>());

      var unmapped = new OneFsDiagnosticBlockAddress(identity, 0, OneFsReader.PhysicalBlockSize);
      Assert.That(
        () => set.ReadDiagnosticBlock(unmapped, new byte[OneFsReader.PhysicalBlockSize]),
        Throws.TypeOf<KeyNotFoundException>());

      var mapped = set.WithDeviceIdentities([identity, null]);
      var multiBlock = new OneFsDiagnosticBlockAddress(identity, 0, 2L * OneFsReader.PhysicalBlockSize);
      Assert.That(
        () => mapped.ReadDiagnosticBlock(multiBlock, new byte[OneFsReader.PhysicalBlockSize]),
        Throws.TypeOf<ArgumentException>());

      var beyondEnd = new OneFsDiagnosticBlockAddress(identity, first.Length - 256, 512);
      Assert.That(
        () => mapped.ReadDiagnosticExtent(beyondEnd, new byte[512]),
        Throws.TypeOf<ArgumentOutOfRangeException>());

      var tooLargeForSpan = new OneFsDiagnosticBlockAddress(identity, 0, (long)int.MaxValue + 1);
      Assert.That(
        () => mapped.ReadDiagnosticExtent(tooLargeForSpan, []),
        Throws.TypeOf<ArgumentException>());
    });
  }

  private static byte[] BuildBlocks(int count, int seed) {
    var result = new byte[checked(count * OneFsReader.PhysicalBlockSize)];
    for (var i = 0; i < result.Length; ++i)
      result[i] = unchecked((byte)(seed + i * 13 + i / OneFsReader.PhysicalBlockSize * 31));
    return result;
  }

  private sealed class SparsePatternStream(long length, long position = 0) : Stream {
    private long _position = position;

    internal static byte Pattern(long absoluteOffset)
      => unchecked((byte)(absoluteOffset * 13 + 7));

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => length;

    public override long Position {
      get => this._position;
      set {
        if (value < 0)
          throw new ArgumentOutOfRangeException(nameof(value));
        this._position = value;
      }
    }

    public override void Flush() { }

    public override int Read(byte[] buffer, int offset, int count)
      => this.Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer) {
      if (this._position >= length)
        return 0;

      var count = (int)Math.Min(buffer.Length, length - this._position);
      for (var index = 0; index < count; ++index)
        buffer[index] = Pattern(this._position + index);
      this._position += count;
      return count;
    }

    public override long Seek(long offset, SeekOrigin origin) {
      var next = origin switch {
        SeekOrigin.Begin => offset,
        SeekOrigin.Current => checked(this._position + offset),
        SeekOrigin.End => checked(length + offset),
        _ => throw new ArgumentOutOfRangeException(nameof(origin)),
      };
      if (next < 0)
        throw new IOException("Attempted to seek before the beginning of the sparse test stream.");
      this._position = next;
      return next;
    }

    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
  }
}
