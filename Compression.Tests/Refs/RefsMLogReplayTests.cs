using FileSystem.Refs;

namespace Compression.Tests.Refs;

[TestFixture]
public sealed class RefsMLogReplayTests {
  [Test, Category("ErrorHandling")]
  public void LaterUnsupportedOpcode_LeavesTargetUntouched() {
    var target = new RecordingTarget();
    var records = new[] {
      LogRecord(1, RefsRedoOpcode.UpdateRow),
      LogRecord(2, RefsRedoOpcode.ReservedUnhandled),
    };

    Assert.Throws<NotSupportedException>(() => RefsMLogRestarter.ReplaySelected(records, target));
    Assert.That(target.Applied, Is.Empty);
  }

  [Test, Category("ErrorHandling")]
  public void LaterUndecodedPayload_LeavesTargetUntouched() {
    var target = new RecordingTarget { RejectLsn = 2 };
    var records = new[] {
      LogRecord(1, RefsRedoOpcode.UpdateRow),
      LogRecord(2, RefsRedoOpcode.InsertRow),
    };

    Assert.Throws<NotSupportedException>(() => RefsMLogRestarter.ReplaySelected(records, target));
    Assert.That(target.Applied, Is.Empty);
    Assert.That(target.Preflighted, Is.EqualTo(new ulong[] { 1, 2 }));
  }

  [Test, Category("HappyPath")]
  public void ReplaySelected_AppliesOnlyRequestedRecoveryWindowInOrder() {
    var target = new RecordingTarget();
    var records = new[] {
      LogRecord(0x00000001_00000001, RefsRedoOpcode.UpdateRow),
      LogRecord(0x00000001_00000002, RefsRedoOpcode.InsertRow),
      LogRecord(0x00000001_00000003, RefsRedoOpcode.DeleteRow),
    };

    var count = RefsMLogRestarter.ReplaySelected(records, target, 0x00000001_00000002);

    Assert.Multiple(() => {
      Assert.That(count, Is.EqualTo(2));
      Assert.That(target.Preflighted, Is.EqualTo(new ulong[] {
        0x00000001_00000002,
        0x00000001_00000003,
      }));
      Assert.That(target.Applied, Is.EqualTo(target.Preflighted));
    });
  }

  private static RefsMLogRecoveryRecord LogRecord(ulong lsn, RefsRedoOpcode opcode)
    => new(0, new RefsMLogDataRecord(
      FormatMagic: 0,
      Lsn: lsn,
      PreviousLsn: 0,
      EntryChecksum: 0,
      EntryHeaderOffset: 0,
      PayloadOffset: 0,
      RedoRecords: [RefsRedoRecord.Create(opcode, 0x600, [1])]),
      []);

  private sealed class RecordingTarget : IRefsRedoTarget {
    public ulong? RejectLsn { get; init; }
    public List<ulong> Preflighted { get; } = [];
    public List<ulong> Applied { get; } = [];

    public void Preflight(ulong lsn, RefsRedoRecord record) {
      this.Preflighted.Add(lsn);
      if (lsn == this.RejectLsn)
        throw new NotSupportedException("The target does not decode this payload.");
    }

    public void Apply(ulong lsn, RefsRedoRecord record) => this.Applied.Add(lsn);
  }
}
