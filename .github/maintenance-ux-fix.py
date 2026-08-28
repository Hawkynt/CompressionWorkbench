from pathlib import Path

path = Path('.github/maintenance-ux.py')
text = path.read_text(encoding='utf-8')
text = text.replace(
    '    var cancellationToken = BeginMaintenanceOperation("Defragment", staged: false);\\n',
    '    var stagedDefrag = defragmentable.GetType().GetMethod(nameof(IArchiveDefragmentable.Defragment), [typeof(Stream)]) == null;\\n'
    '    var cancellationToken = BeginMaintenanceOperation("Defragment", staged: stagedDefrag);\\n')
text = text.replace(
    '    using var archive = new MemoryStream(new byte[4096], writable: true);\\n'
    '    var descriptor = new FakeDescriptor();',
    '    using var archive = new MemoryStream();\\n'
    '    archive.Write(new byte[4096]);\\n'
    '    archive.Position = 0;\\n'
    '    var descriptor = new FakeDescriptor();')
text = text.replace(
    '    // Trivial case: 0 or 1 files cannot benefit from regrouping\\n'
    '    if (entries.Count <= 1) {',
    '    cancellationToken.ThrowIfCancellationRequested();\\n\\n'
    '    // Trivial case: 0 or 1 files cannot benefit from regrouping\\n'
    '    if (entries.Count <= 1) {')
text = text.replace(
    '    CollectionAssert.Contains(phases, "scanning");\\n'
    '    CollectionAssert.Contains(phases, "reading");\\n'
    '    CollectionAssert.Contains(phases, "writing");',
    '    Assert.That(phases, Does.Contain("scanning"));\\n'
    '    Assert.That(phases, Does.Contain("reading"));\\n'
    '    Assert.That(phases, Does.Contain("writing"));')
text = text.replace(
    '    CollectionAssert.AreEqual(original, archive.ToArray(),\\n'
    '      "A cancelled staged rebuild must never overwrite the source stream.");',
    '    Assert.That(archive.ToArray(), Is.EqualTo(original),\\n'
    '      "A cancelled staged rebuild must never overwrite the source stream.");')
path.write_text(text, encoding='utf-8')
Path(__file__).unlink(missing_ok=True)
