using Compression.NativeUI.Controls;
using Compression.NativeUI.Theming;
using Compression.NativeUI.ViewModels;
using Hawkynt.NativeForms;

namespace Compression.NativeUI.Views;

/// <summary>The thumbnail view: large icons, with pictures shrunk into them as they are decoded.</summary>
internal sealed partial class MainForm {
  /// <summary>Files larger than this keep their icon; decoding them for a 96-pixel square is not worth the wait.</summary>
  internal const long ThumbnailSourceLimit = 16L * 1024 * 1024;

  /// <summary>How many entries of one folder get a thumbnail; the rest keep their icon.</summary>
  internal const int ThumbnailsPerFolder = 500;

  private ImageList? _thumbnails;
  // Keyed by entry path, not by the entry object: a reread of the same folder makes new entry
  // objects, and keying by object decoded every picture again and grew the image list each time.
  private readonly Dictionary<string, string> _thumbnailKeys = [];
  private object? _thumbnailFolder;
  private int _thumbnailGeneration;

  /// <summary>Whether the list shows thumbnails rather than details.</summary>
  internal bool ThumbnailsShown => this._entries.View == ListViewView.LargeIcon;

  /// <summary>Switches between the details list and the thumbnail view.</summary>
  internal void ShowThumbnails(bool on) {
    this._entries.View = on ? ListViewView.LargeIcon : ListViewView.Details;
    this.RefreshEntries();
  }

  /// <summary>The image key an entry is drawn with in the current view.</summary>
  private string ImageKeyFor(ArchiveEntryViewModel entry)
    => this.ThumbnailsShown && this._thumbnailKeys.TryGetValue(entry.Path, out var key) ? key : entry.IconKey;

  /// <summary>
  /// Starts decoding thumbnails for the entries on show, one at a time off the UI thread. A newer
  /// listing supersedes an older one: its results are dropped as they arrive.
  /// </summary>
  private void StartThumbnails() {
    var generation = ++this._thumbnailGeneration;
    if (!this.ThumbnailsShown) return;

    // A new folder starts a new image list, so moving around does not grow one without end.
    var folder = (object?)this._model.CurrentLocation ?? this._model.ArchivePath;
    if (this._thumbnails is null || !Equals(folder, this._thumbnailFolder)) {
      this._thumbnails = IconSet.CreateImageList(Thumbnail.Size);
      this._thumbnailKeys.Clear();
      this._thumbnailFolder = folder;
      this._entries.LargeImageList = this._thumbnails;
    }

    var pending = this._model.Entries
      .Where(e => !e.IsDirectory && !e.IsParentEntry && !e.IsEncrypted && !this._thumbnailKeys.ContainsKey(e.Path))
      .Where(e => this._model.IsBrowsingOsFolder || e.OriginalSize <= ThumbnailSourceLimit)
      .Take(ThumbnailsPerFolder)
      .ToList();
    if (pending.Count == 0) return;

    // Where to read from is decided now, on the UI thread; by the time the decoder reaches an entry
    // the shell may be showing somewhere else.
    var source = this._model.CapturePreviewSource();
    Task.Run(() => {
      foreach (var entry in pending) {
        if (generation != Volatile.Read(ref this._thumbnailGeneration)) return;

        var pixels = this.DecodeThumbnail(entry, source);
        if (pixels is null) continue;

        this.BeginInvoke(() => this.AddThumbnail(generation, entry, pixels));
      }
    });
  }

  private int[]? DecodeThumbnail(ArchiveEntryViewModel entry, PreviewSource source) {
    try {
      if (source.BrowsingDisk && new FileInfo(entry.Path).Length > ThumbnailSourceLimit) return null;

      var (data, _) = this._model.ReadForPreview(entry, source);
      if (data is null || !PreviewImageDecoder.TryDecode(data, entry.Name, out var picture) || picture.Frames.Count == 0)
        return null;

      return Thumbnail.Fit(picture.Frames[0], picture.Width, picture.Height);
    } catch (Exception) {
      // A picture that cannot be decoded keeps its icon; a thumbnail is never worth an error.
      return null;
    }
  }

  private void AddThumbnail(int generation, ArchiveEntryViewModel entry, int[] pixels) {
    if (generation != this._thumbnailGeneration || this._thumbnails is not { } list) return;

    var key = "thumb:" + this._thumbnailKeys.Count;
    list.Add(key, pixels);
    this._thumbnailKeys[entry.Path] = key;

    foreach (var item in this._entries.Items)
      if (ReferenceEquals(item.Tag, entry)) {
        item.ImageKey = key;
        break;
      }

    this._entries.Invalidate();
  }
}
