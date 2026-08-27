using FileSystem.Refs;

namespace Compression.Tests.Refs;

[TestFixture]
public sealed class RefsNativeCowValidationTests {
  [Test, Category("HappyPath")]
  public void RedoPreflight_AcceptsSingleTransaction() {
    var records = new[] {
      RefsRedoRecord.Create(
        RefsRedoOpcode.OpenTableFromTablePath,
        0x600,
        new byte[] { 1, 2 },
        flags: RefsRedoFlags.TransactionStart),
      RefsRedoRecord.Create(
        RefsRedoOpcode.UpdateRow,
        0x600,
        new byte[] { 3, 4 }),
    };

    Assert.DoesNotThrow(() => RefsNativeCowValidation.ValidateRedoTransaction(records));
  }

  [Test, Category("ErrorHandling")]
  public void RedoPreflight_RequiresTransactionStartOnFirstRecord() {
    var records = new[] {
      RefsRedoRecord.Create(RefsRedoOpcode.UpdateRow, 0x600, new byte[] { 1 }),
    };

    Assert.Throws<InvalidDataException>(() => RefsNativeCowValidation.ValidateRedoTransaction(records));
  }

  [Test, Category("ErrorHandling")]
  public void RedoPreflight_RejectsSecondTransactionStart() {
    var records = new[] {
      RefsRedoRecord.Create(
        RefsRedoOpcode.OpenTableFromTablePath,
        0x600,
        Array.Empty<byte>(),
        flags: RefsRedoFlags.TransactionStart),
      RefsRedoRecord.Create(
        RefsRedoOpcode.UpdateRow,
        0x600,
        Array.Empty<byte>(),
        flags: RefsRedoFlags.TransactionStart),
    };

    Assert.Throws<InvalidDataException>(() => RefsNativeCowValidation.ValidateRedoTransaction(records));
  }

  [Test, Category("ErrorHandling")]
  public void RedoPreflight_RejectsPerRecordCommitBit() {
    var records = new[] {
      RefsRedoRecord.Create(
        RefsRedoOpcode.UpdateRow,
        0x600,
        Array.Empty<byte>(),
        flags: RefsRedoFlags.TransactionStart | RefsRedoFlags.CommitMarker),
    };

    Assert.Throws<InvalidDataException>(() => RefsNativeCowValidation.ValidateRedoTransaction(records));
  }

  [Test, Category("ErrorHandling")]
  public void RedoPreflight_RejectsReservedOpcode() {
    var records = new[] {
      RefsRedoRecord.Create(
        RefsRedoOpcode.ReservedUnhandled,
        0x600,
        Array.Empty<byte>(),
        flags: RefsRedoFlags.TransactionStart),
    };

    Assert.Throws<NotSupportedException>(() => RefsNativeCowValidation.ValidateRedoTransaction(records));
  }

  [Test, Category("ErrorHandling")]
  public void RedoPreflight_RejectsUnknownOpcode() {
    var records = new[] {
      RefsRedoRecord.Create(
        (RefsRedoOpcode)0xFFFF,
        0x600,
        Array.Empty<byte>(),
        flags: RefsRedoFlags.TransactionStart),
    };

    Assert.Throws<NotSupportedException>(() => RefsNativeCowValidation.ValidateRedoTransaction(records));
  }

  [Test, Category("ErrorHandling")]
  public void RedoPreflight_RejectsTransactionLargerThanOneMLogBlock() {
    var records = new[] {
      RefsRedoRecord.Create(
        RefsRedoOpcode.UpdateRow,
        0x600,
        new byte[RefsMLogCodec.LogBlockSize],
        flags: RefsRedoFlags.TransactionStart),
    };

    Assert.Throws<InvalidOperationException>(() => RefsNativeCowValidation.ValidateRedoTransaction(records));
  }
}
