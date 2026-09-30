using System.Collections.ObjectModel;
using System.Collections.Specialized;

namespace Compression.NativeUI.ViewModels;

/// <summary>
/// The entries on show, with a way to replace them all under one notification. A listing clears the
/// collection and adds every row; notifying per row made every listener redo its work once per entry.
/// </summary>
internal sealed class EntryCollection : ObservableCollection<ArchiveEntryViewModel> {
  private int _deferred;
  private bool _pending;

  /// <summary>Holds change notifications until the returned scope ends, then raises one reset.</summary>
  public IDisposable Defer() {
    ++this._deferred;
    return new Scope(this);
  }

  protected override void OnCollectionChanged(NotifyCollectionChangedEventArgs e) {
    if (this._deferred > 0) {
      this._pending = true;
      return;
    }

    base.OnCollectionChanged(e);
  }

  private void Release() {
    if (--this._deferred > 0 || !this._pending) return;

    this._pending = false;
    base.OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
  }

  private sealed class Scope(EntryCollection owner) : IDisposable {
    private bool _done;

    public void Dispose() {
      if (this._done) return;
      this._done = true;
      owner.Release();
    }
  }
}
