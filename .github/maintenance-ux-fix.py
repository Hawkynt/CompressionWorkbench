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
path.write_text(text, encoding='utf-8')
Path(__file__).unlink(missing_ok=True)
