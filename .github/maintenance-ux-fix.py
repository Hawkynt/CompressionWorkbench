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
text = text.replace(
    "xaml = read('Compression.UI/Views/DefragmentWindow.xaml')\n",
    "xaml = read('Compression.UI/Views/DefragmentWindow.xaml')\n"
    "xaml = xaml.replace('ResizeMode=\"CanResizeWithGrip\">', 'ResizeMode=\"CanResizeWithGrip\" Closing=\"OnWindowClosing\">')\n"
    "xaml = xaml.replace('Text=\"Defrag support:\"', 'Text=\"Maintenance support:\"')\n")
text = text.replace(
    'Green = source read head; orange = staged-target write head. The existing archive remains unchanged until the verified target is committed.',
    'Green = source read head; orange = staged-target write head, projected onto the same chart for progress (not physical offset equivalence). The existing archive remains unchanged until the verified target is committed.')
text = text.replace(
    '''  private void OnClose(object sender, RoutedEventArgs e) {\n    if (this._operationCts != null) {''',
    '''  private void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e) {\n    if (this._operationCts == null) return;\n    e.Cancel = true;\n    OnCancelOperation(this, new RoutedEventArgs());\n  }\n\n  private void OnClose(object sender, RoutedEventArgs e) {\n    if (this._operationCts != null) {''')
path.write_text(text, encoding='utf-8')
Path(__file__).unlink(missing_ok=True)
