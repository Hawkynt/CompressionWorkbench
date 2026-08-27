#pragma warning disable CS1591
using System.Buffers.Binary;
using FileSystem.BcacheFs;
using static FileSystem.BcacheFs.BcacheFsFormat;

namespace Compression.Tests.BcacheFs;

[TestFixture]
public class BcacheFsRawKeyTests {
  [Test, Category("Btree")]
  public void CurrentKey_RoundTripsVersionWhiteoutPositionAndValue() {
    var bytes = new byte[56];
    bytes[0] = 7; // 56 bytes / 8
    bytes[1] = 0x80 | KeyFormatCurrent;
    bytes[2] = (byte)BcacheFsKeyType.ExtentWhiteout;
    BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(4), 0x0123456789ABCDEFUL);
    BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), 0x10203040);
    BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), 77);
    WriteBpos(bytes.AsSpan(20), new Bpos(123, 456, 789));
    for (var i = BkeyBytes; i < bytes.Length; ++i) bytes[i] = (byte)(i * 11);

    Assert.That(BcacheFsRawKeyCodec.TryDecode(bytes, null, out var key, out var error),
      Is.True, error);
    Assert.That(key, Is.Not.Null);

    Assert.Multiple(() => {
      Assert.That(key!.NeedsWhiteout, Is.True);
      Assert.That(key.Type, Is.EqualTo(BcacheFsKeyType.ExtentWhiteout));
      Assert.That(key.Version.Lo, Is.EqualTo(0x0123456789ABCDEFUL));
      Assert.That(key.Version.Hi, Is.EqualTo(0x10203040));
      Assert.That(key.Size, Is.EqualTo(77));
      Assert.That(key.Position, Is.EqualTo(new Bpos(123, 456, 789)));
      Assert.That(key.EncodedBytes, Is.EqualTo(bytes).AsCollection);
      Assert.That(key.EncodeCurrent(), Is.EqualTo(bytes).AsCollection);
    });
  }

  [Test, Category("Btree")]
  public void PackedKey_DecodesFieldsFromHighWordsWithOffsets() {
    var format = new BcacheFsKeyFormat(
      KeyU64s: 2,
      FieldCount: BcacheFsKeyFormat.FieldCountCurrent,
      Bits: [8, 8, 8, 8, 8, 8],
      Offsets: [100, 200, 300, 400, 500, 600]);
    var bytes = new byte[16];
    bytes[0] = 2;
    bytes[1] = 0; // KEY_FORMAT_LOCAL_BTREE
    bytes[2] = (byte)BcacheFsKeyType.Set;

    // Little-endian bcachefs consumes packed fields from the MSB of the final
    // key word downward: inode, offset, snapshot, size, version_hi, version_lo.
    const ulong packed =
      (1UL << 56) |
      (2UL << 48) |
      (3UL << 40) |
      (4UL << 32) |
      (5UL << 24) |
      (6UL << 16);
    BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(8), packed);

    Assert.That(BcacheFsRawKeyCodec.TryDecode(bytes, format, out var key, out var error),
      Is.True, error);
    Assert.That(key, Is.Not.Null);
    Assert.Multiple(() => {
      Assert.That(key!.IsPacked, Is.True);
      Assert.That(key.Position, Is.EqualTo(new Bpos(101, 202, 303)));
      Assert.That(key.Size, Is.EqualTo(404));
      Assert.That(key.Version.Hi, Is.EqualTo(505));
      Assert.That(key.Version.Lo, Is.EqualTo(606));
      Assert.That(key.Value, Is.Empty);
      Assert.That(key.EncodedBytes, Is.EqualTo(bytes).AsCollection);
    });
  }

  [Test, Category("Btree")]
  public void PackedKey_RejectsFieldOverflowInsteadOfThrowing() {
    var format = new BcacheFsKeyFormat(
      KeyU64s: 1,
      FieldCount: BcacheFsKeyFormat.FieldCountCurrent,
      Bits: [0, 0, 0, 0, 0, 0],
      Offsets: [0, 0, (ulong)uint.MaxValue + 1, 0, 0, 0]);
    var bytes = new byte[8];
    bytes[0] = 1;
    bytes[1] = 0;
    bytes[2] = (byte)BcacheFsKeyType.Set;

    Assert.That(BcacheFsRawKeyCodec.TryDecode(bytes, format, out _, out var error), Is.False);
    Assert.That(error, Does.Contain("invalid packed bcachefs key"));
  }

  [Test, Category("Btree")]
  public void UnknownKeyType_IsPreservedInsteadOfDiscarded() {
    var bytes = new byte[BkeyBytes + 8];
    bytes[0] = (byte)(bytes.Length / 8);
    bytes[1] = KeyFormatCurrent;
    bytes[2] = 0xFE;
    WriteBpos(bytes.AsSpan(20), new Bpos(1, 2, 3));
    bytes[BkeyBytes] = 0xA5;

    Assert.That(BcacheFsRawKeyCodec.TryDecode(bytes, null, out var key, out var error),
      Is.True, error);
    Assert.That(key!.Type, Is.Null);
    Assert.That(key.RawType, Is.EqualTo(0xFE));
    Assert.That(key.EncodeCurrent(), Is.EqualTo(bytes).AsCollection);
  }

  [Test, Category("Btree")]
  public void PackedKey_RequiresItsNodeFormat() {
    var bytes = new byte[8];
    bytes[0] = 1;
    bytes[1] = 0;
    bytes[2] = (byte)BcacheFsKeyType.Set;

    Assert.That(BcacheFsRawKeyCodec.TryDecode(bytes, null, out _, out var error), Is.False);
    Assert.That(error, Does.Contain("node-local bkey_format"));
  }

  [Test, Category("Btree")]
  public void KeyVersion_OrdersHighWordBeforeLowWord() {
    Assert.Multiple(() => {
      Assert.That(new BcacheFsKeyVersion(ulong.MaxValue, 0)
        .CompareTo(new BcacheFsKeyVersion(0, 1)), Is.LessThan(0));
      Assert.That(new BcacheFsKeyVersion(10, 1)
        .CompareTo(new BcacheFsKeyVersion(9, 1)), Is.GreaterThan(0));
    });
  }
}