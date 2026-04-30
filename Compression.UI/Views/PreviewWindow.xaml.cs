using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Compression.Analysis.Statistics;
using Compression.UI.Controls;

namespace Compression.UI.Views;

public partial class PreviewWindow : Window {
  private byte[] _data = [];
  private bool _hexMode;
  private bool _imageMode;
  private LazyHexLines? _hexLines;
  private LazyTextLines? _textLines;
  private int _bytesPerRow = 16;
  private bool _autoWidth = true;

  // Multi-frame image state. Frame array is materialised on TryRenderAsImage so
  // the decoder stream can be released; per-frame delays come from GIF graphic-
  // control extension metadata (`/grctlext/Delay`, hundredths of a second).
  // For non-GIF multi-frame inputs (multi-page TIFF, ICO with multiple sizes,
  // MPO stereoscopic JPEG), delays stay null and the timer never starts —
  // navigation is manual via prev/next.
  private System.Windows.Media.Imaging.BitmapSource[]? _frames;
  private int[]? _frameDelaysMs;
  private int _frameIndex;
  private System.Windows.Threading.DispatcherTimer? _animationTimer;
  private bool _isPlaying;


  public PreviewWindow() {
    InitializeComponent();
  }

  public void ShowData(string entryName, byte[] data, bool hex = false) {
    _data = data;
    _hexMode = hex;
    _hexLines = null;
    _textLines = null;
    StopAnimation();
    _frames = null;
    _frameDelaysMs = null;
    _frameIndex = 0;
    FrameNavPanel.Visibility = Visibility.Collapsed;
    FrameNavSeparator.Visibility = Visibility.Collapsed;
    Title = $"Preview \u2014 {entryName}";

    SizeLabel.Text = FormatSize(data.Length);
    SizeInfo.Text = FormatSize(data.Length);

    // Sniff for known image signatures BEFORE rendering \u2014 multi-MB image bytes
    // turn into millions of characters in the text/hex pipeline and lock the
    // UI thread. Render visually via WPF's native codecs instead.
    if (!hex && TryRenderAsImage(data)) {
      _imageMode = true;
      ImageMode.Visibility = Visibility.Visible;
      ImageMode.IsChecked = true;
      return;
    }

    _imageMode = false;
    ImageMode.Visibility = Visibility.Collapsed;
    if (hex)
      HexMode.IsChecked = true;
    RefreshContent();
  }

  private void RefreshContent() {
    if (_imageMode) {
      ContentBox.Visibility = Visibility.Collapsed;
      HexList.Visibility = Visibility.Collapsed;
      ImageScroll.Visibility = Visibility.Visible;
      EncodingBox.IsEnabled = false;
      return;
    }

    if (_hexMode) {
      ContentBox.Visibility = Visibility.Collapsed;
      ImageScroll.Visibility = Visibility.Collapsed;
      HexList.Visibility = Visibility.Visible;
      EncodingBox.IsEnabled = false;

      UpdateFrequencyPercentiles();
      _hexLines = new LazyHexLines(_data, _bytesPerRow);
      HexList.DataContext = _hexLines;
      SizeLabel.Text = FormatSize(_data.Length);
    }
    else {
      HexList.Visibility = Visibility.Collapsed;
      ImageScroll.Visibility = Visibility.Collapsed;
      ContentBox.Visibility = Visibility.Visible;
      EncodingBox.IsEnabled = true;

      _textLines = new LazyTextLines(_data, GetEncoding());
      ContentBox.DataContext = _textLines;
      SizeLabel.Text = FormatSize(_data.Length);
    }
  }

  /// <summary>
  /// If <paramref name="data"/> begins with a known image-format magic, decode
  /// it via WPF's <see cref="BitmapImage"/> (PNG/JPEG/BMP/GIF/TIFF) or
  /// <see cref="IconBitmapDecoder"/> (ICO/CUR) and bind the result to the
  /// <c>ImageView</c> control. Returns <c>false</c> for non-image bytes or
  /// unsupported variants (e.g. WebP \u2014 WPF has no built-in codec) so the
  /// caller falls back to the text/hex path.
  /// </summary>
  private bool TryRenderAsImage(byte[] data) {
    if (!IsKnownImageSignature(data, out var isIcon)) return false;
    try {
      using var ms = new MemoryStream(data);
      // Unified path: BitmapDecoder.Create auto-selects the right WPF codec for
      // PNG/JPEG/BMP/GIF/TIFF (multi-page); ICO/CUR need IconBitmapDecoder
      // explicitly because the auto-selector doesn't recognise their magic.
      // OnLoad forces full decode while the stream is open so we can release it.
      BitmapDecoder decoder = isIcon
        ? new IconBitmapDecoder(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad)
        : BitmapDecoder.Create(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);

      if (decoder.Frames.Count == 0) return false;

      // For GIF, raw decoder frames are partial sub-images with transparent
      // margins — disposal methods (1=keep, 2=clear, 3=restore-previous) are
      // NOT applied by WPF. Composite manually onto a logical-screen canvas.
      // Other multi-frame inputs (TIFF/ICO/MPO) are full-canvas frames already
      // and don't need compositing.
      BitmapSource[] frames;
      if (decoder is GifBitmapDecoder && decoder.Frames.Count > 1) {
        try {
          frames = CompositeGifFrames(decoder);
        } catch {
          frames = MaterializeRawFrames(decoder);
        }
      } else {
        frames = MaterializeRawFrames(decoder);
      }
      _frames = frames;
      _frameDelaysMs = isIcon ? null : ExtractGifDelays(decoder);
      // ICO/CUR: start on the largest frame so single-frame behaviour matches
      // the previous "best" pick; navigation still walks all sizes.
      _frameIndex = isIcon ? IndexOfLargest(frames) : 0;

      UpdateFrameDisplay();

      var multiFrame = frames.Length > 1;
      FrameNavPanel.Visibility = multiFrame ? Visibility.Visible : Visibility.Collapsed;
      FrameNavSeparator.Visibility = multiFrame ? Visibility.Visible : Visibility.Collapsed;

      // Auto-play only when per-frame delays were actually decoded (GIF). Other
      // multi-frame formats (TIFF/ICO/MPO) get manual navigation only.
      if (multiFrame && _frameDelaysMs is { Length: > 0 } && _frameDelaysMs[0] > 0)
        StartAnimation();

      ImageScroll.Visibility = Visibility.Visible;
      ContentBox.Visibility = Visibility.Collapsed;
      HexList.Visibility = Visibility.Collapsed;
      return true;
    } catch {
      // Codec error (corrupt JPEG, unsupported subformat). Fall through.
      ImageView.Source = null;
      _frames = null;
      _frameDelaysMs = null;
      return false;
    }
  }

  private static int IndexOfLargest(BitmapSource[] frames) {
    var best = 0;
    var bestArea = frames[0].PixelWidth * frames[0].PixelHeight;
    for (var i = 1; i < frames.Length; i++) {
      var area = frames[i].PixelWidth * frames[i].PixelHeight;
      if (area > bestArea) { best = i; bestArea = area; }
    }
    return best;
  }

  /// <summary>
  /// Reads per-frame delay metadata from a GIF decoder. Other formats return
  /// null (no animation timing). Delay is stored at the WPF metadata path
  /// <c>/grctlext/Delay</c> as a UInt16 in hundredths of a second; we convert
  /// to milliseconds and clamp 0 (some encoders omit) up to a sane minimum.
  /// </summary>
  private static int[]? ExtractGifDelays(BitmapDecoder decoder) {
    if (decoder is not GifBitmapDecoder) return null;
    try {
      var delays = new int[decoder.Frames.Count];
      for (var i = 0; i < decoder.Frames.Count; i++) {
        var meta = decoder.Frames[i].Metadata as BitmapMetadata;
        var raw = meta?.GetQuery("/grctlext/Delay");
        var hundredths = raw is ushort us ? us : raw is int ix ? ix : 0;
        // Browsers treat 0 / 1 / 2 as ~100 ms to avoid CPU-melting playback;
        // mirror that floor.
        delays[i] = hundredths < 3 ? 100 : hundredths * 10;
      }
      return delays;
    } catch {
      return null;
    }
  }

  private static BitmapSource[] MaterializeRawFrames(BitmapDecoder decoder) {
    var result = new BitmapSource[decoder.Frames.Count];
    for (var i = 0; i < decoder.Frames.Count; i++) {
      var f = decoder.Frames[i];
      if (!f.IsFrozen) f.Freeze();
      result[i] = f;
    }
    return result;
  }

  /// <summary>
  /// Composites GIF frames onto a logical-screen canvas applying disposal
  /// methods. Raw <see cref="GifBitmapDecoder"/> frames are partial sub-images
  /// at <c>/imgdesc/Left,Top</c> with transparent margins; disposal codes
  /// (0/1=keep, 2=clear-to-bg, 3=restore-previous) are not applied. Without
  /// this pass, frames showing only a sprite delta against the previous frame
  /// render as that sprite plus transparency — losing the static background.
  /// </summary>
  private static BitmapSource[] CompositeGifFrames(BitmapDecoder decoder) {
    var (screenW, screenH) = ResolveScreenSize(decoder);
    if (screenW <= 0 || screenH <= 0) return MaterializeRawFrames(decoder);

    var canvas = new System.Windows.Media.Imaging.WriteableBitmap(
      screenW, screenH, 96, 96, System.Windows.Media.PixelFormats.Pbgra32, null);
    byte[]? snapshot = null;
    Int32Rect? prevRect = null;
    var prevDisposal = 0;

    var result = new BitmapSource[decoder.Frames.Count];
    for (var i = 0; i < decoder.Frames.Count; i++) {
      var frame = decoder.Frames[i];
      var (left, top) = ReadFramePos(frame);
      var disposal = ReadFrameDisposal(frame);

      // Apply *previous* frame's disposal before drawing this one.
      if (i > 0 && prevRect.HasValue) {
        if (prevDisposal == 2) ClearRect(canvas, prevRect.Value);
        else if (prevDisposal == 3 && snapshot != null) RestoreSnapshot(canvas, snapshot);
      }

      // If THIS frame has disposal 3 the next iteration will need to roll
      // back to canvas-before-this-frame; capture pre-draw state now.
      snapshot = disposal == 3 ? SnapshotCanvas(canvas) : null;

      var rect = ClampToCanvas(left, top, frame.PixelWidth, frame.PixelHeight, screenW, screenH);
      if (rect.Width > 0 && rect.Height > 0)
        BlendFrameOntoCanvas(canvas, frame, rect, left, top);

      result[i] = SnapshotAsImage(canvas);
      prevRect = rect;
      prevDisposal = disposal;
    }
    return result;
  }

  private static (int W, int H) ResolveScreenSize(BitmapDecoder decoder) {
    int w = 0, h = 0;
    try {
      var meta = decoder.Metadata as BitmapMetadata;
      if (meta?.GetQuery("/logscrdesc/Width") is ushort lw) w = lw;
      if (meta?.GetQuery("/logscrdesc/Height") is ushort lh) h = lh;
    } catch { /* metadata access can throw on malformed GIFs */ }
    if (w > 0 && h > 0) return (w, h);
    // Fallback: union of frame bounding boxes.
    foreach (var f in decoder.Frames) {
      var (l, t) = ReadFramePos(f);
      w = Math.Max(w, l + f.PixelWidth);
      h = Math.Max(h, t + f.PixelHeight);
    }
    return (w, h);
  }

  private static (int Left, int Top) ReadFramePos(BitmapFrame frame) {
    try {
      var meta = frame.Metadata as BitmapMetadata;
      var l = meta?.GetQuery("/imgdesc/Left") is ushort lx ? lx : 0;
      var t = meta?.GetQuery("/imgdesc/Top") is ushort tx ? tx : 0;
      return (l, t);
    } catch { return (0, 0); }
  }

  private static int ReadFrameDisposal(BitmapFrame frame) {
    try {
      var meta = frame.Metadata as BitmapMetadata;
      return meta?.GetQuery("/grctlext/Disposal") is byte d ? d : 0;
    } catch { return 0; }
  }

  private static Int32Rect ClampToCanvas(int left, int top, int w, int h, int canvasW, int canvasH) {
    if (left < 0) { w += left; left = 0; }
    if (top < 0) { h += top; top = 0; }
    if (left + w > canvasW) w = canvasW - left;
    if (top + h > canvasH) h = canvasH - top;
    if (w < 0) w = 0;
    if (h < 0) h = 0;
    return new Int32Rect(left, top, w, h);
  }

  /// <summary>
  /// Alpha-blends a single frame's pixels onto the canvas in Pbgra32. GIF
  /// transparency is binary: a fully-transparent frame pixel keeps the canvas
  /// pixel underneath, an opaque frame pixel replaces it.
  /// </summary>
  private static void BlendFrameOntoCanvas(
    System.Windows.Media.Imaging.WriteableBitmap canvas,
    BitmapFrame frame, Int32Rect canvasRect, int frameOffsetX, int frameOffsetY) {
    var src = frame.Format == System.Windows.Media.PixelFormats.Pbgra32
      ? (BitmapSource)frame
      : new System.Windows.Media.Imaging.FormatConvertedBitmap(
          frame, System.Windows.Media.PixelFormats.Pbgra32, null, 0);
    var srcStride = frame.PixelWidth * 4;
    var srcPixels = new byte[srcStride * frame.PixelHeight];
    src.CopyPixels(srcPixels, srcStride, 0);

    var dstStride = canvasRect.Width * 4;
    var dstPixels = new byte[dstStride * canvasRect.Height];
    canvas.CopyPixels(canvasRect, dstPixels, dstStride, 0);

    // Crop offset into the source frame when canvasRect was clamped at
    // negative left/top: srcStartX/Y compensates for those trimmed pixels.
    var srcStartX = canvasRect.X - frameOffsetX;
    var srcStartY = canvasRect.Y - frameOffsetY;
    for (var y = 0; y < canvasRect.Height; y++) {
      var srcRowOffset = ((srcStartY + y) * srcStride) + (srcStartX * 4);
      var dstRowOffset = y * dstStride;
      for (var x = 0; x < canvasRect.Width; x++) {
        var fi = srcRowOffset + (x * 4);
        var ci = dstRowOffset + (x * 4);
        var alpha = srcPixels[fi + 3];
        if (alpha == 0) continue;
        dstPixels[ci + 0] = srcPixels[fi + 0];
        dstPixels[ci + 1] = srcPixels[fi + 1];
        dstPixels[ci + 2] = srcPixels[fi + 2];
        dstPixels[ci + 3] = srcPixels[fi + 3];
      }
    }
    canvas.WritePixels(canvasRect, dstPixels, dstStride, 0);
  }

  private static void ClearRect(System.Windows.Media.Imaging.WriteableBitmap canvas, Int32Rect rect) {
    if (rect.Width <= 0 || rect.Height <= 0) return;
    var stride = rect.Width * 4;
    var zeros = new byte[stride * rect.Height];
    canvas.WritePixels(rect, zeros, stride, 0);
  }

  private static byte[] SnapshotCanvas(System.Windows.Media.Imaging.WriteableBitmap canvas) {
    var stride = canvas.PixelWidth * 4;
    var bytes = new byte[stride * canvas.PixelHeight];
    canvas.CopyPixels(bytes, stride, 0);
    return bytes;
  }

  private static void RestoreSnapshot(
    System.Windows.Media.Imaging.WriteableBitmap canvas, byte[] snapshot) {
    var stride = canvas.PixelWidth * 4;
    canvas.WritePixels(new Int32Rect(0, 0, canvas.PixelWidth, canvas.PixelHeight), snapshot, stride, 0);
  }

  private static BitmapSource SnapshotAsImage(System.Windows.Media.Imaging.WriteableBitmap canvas) {
    var stride = canvas.PixelWidth * 4;
    var bytes = new byte[stride * canvas.PixelHeight];
    canvas.CopyPixels(bytes, stride, 0);
    var clone = BitmapSource.Create(
      canvas.PixelWidth, canvas.PixelHeight, 96, 96,
      System.Windows.Media.PixelFormats.Pbgra32, null, bytes, stride);
    clone.Freeze();
    return clone;
  }

  private void UpdateFrameDisplay() {
    if (_frames == null || _frames.Length == 0) return;
    if ((uint)_frameIndex >= (uint)_frames.Length) _frameIndex = 0;
    ImageView.Source = _frames[_frameIndex];
    FrameIndexLabel.Text = _frames.Length > 1
      ? $"Frame {_frameIndex + 1} / {_frames.Length}"
      : "";
  }

  private int CurrentDelayMs() {
    if (_frameDelaysMs is { Length: > 0 } d && _frameIndex < d.Length)
      return d[_frameIndex] > 0 ? d[_frameIndex] : 100;
    return 100;
  }

  private void StartAnimation() {
    if (_frames == null || _frames.Length < 2) return;
    if (_animationTimer == null) {
      _animationTimer = new System.Windows.Threading.DispatcherTimer();
      _animationTimer.Tick += OnAnimationTick;
    }
    _animationTimer.Interval = TimeSpan.FromMilliseconds(CurrentDelayMs());
    _animationTimer.Start();
    _isPlaying = true;
    PlayPauseButton.Content = "\u23f8";
  }

  private void StopAnimation() {
    _animationTimer?.Stop();
    _isPlaying = false;
    if (PlayPauseButton != null) PlayPauseButton.Content = "\u25b6";
  }

  private void OnAnimationTick(object? sender, EventArgs e) {
    if (_frames == null) return;
    _frameIndex = (_frameIndex + 1) % _frames.Length;
    UpdateFrameDisplay();
    if (_animationTimer != null)
      _animationTimer.Interval = TimeSpan.FromMilliseconds(CurrentDelayMs());
  }

  private void OnPrevFrame(object sender, RoutedEventArgs e) {
    if (_frames == null) return;
    StopAnimation();
    _frameIndex = (_frameIndex - 1 + _frames.Length) % _frames.Length;
    UpdateFrameDisplay();
  }

  private void OnNextFrame(object sender, RoutedEventArgs e) {
    if (_frames == null) return;
    StopAnimation();
    _frameIndex = (_frameIndex + 1) % _frames.Length;
    UpdateFrameDisplay();
  }

  private void OnPlayPause(object sender, RoutedEventArgs e) {
    if (_isPlaying) StopAnimation();
    else StartAnimation();
  }

  private void OnWindowKeyDown(object sender, System.Windows.Input.KeyEventArgs e) {
    if (_frames == null || _frames.Length < 2) return;
    switch (e.Key) {
      case System.Windows.Input.Key.Left:
        OnPrevFrame(this, e);
        e.Handled = true;
        break;
      case System.Windows.Input.Key.Right:
        OnNextFrame(this, e);
        e.Handled = true;
        break;
      case System.Windows.Input.Key.Space:
        OnPlayPause(this, e);
        e.Handled = true;
        break;
    }
  }

  protected override void OnClosed(EventArgs e) {
    StopAnimation();
    _frames = null;
    base.OnClosed(e);
  }

  /// <summary>
  /// Recognises image formats that WPF's BitmapImage (or IconBitmapDecoder)
  /// can render natively. WebP is intentionally NOT included \u2014 WPF lacks a
  /// built-in WebP codec on stock Windows. Returns <c>true</c> for renderable
  /// signatures and sets <paramref name="isIcon"/> for ICO/CUR.
  /// </summary>
  private static bool IsKnownImageSignature(ReadOnlySpan<byte> data, out bool isIcon) {
    isIcon = false;
    if (data.Length < 4) return false;
    // PNG: 89 50 4E 47 0D 0A 1A 0A
    if (data.Length >= 8 && data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47
        && data[4] == 0x0D && data[5] == 0x0A && data[6] == 0x1A && data[7] == 0x0A)
      return true;
    // JPEG: FF D8 FF
    if (data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF) return true;
    // GIF: "GIF8"
    if (data[0] == 0x47 && data[1] == 0x49 && data[2] == 0x46 && data[3] == 0x38) return true;
    // BMP: "BM"
    if (data[0] == 0x42 && data[1] == 0x4D) return true;
    // TIFF LE: II*\0
    if (data[0] == 0x49 && data[1] == 0x49 && data[2] == 0x2A && data[3] == 0x00) return true;
    // TIFF BE: MM\0*
    if (data[0] == 0x4D && data[1] == 0x4D && data[2] == 0x00 && data[3] == 0x2A) return true;
    // ICO / CUR: 00 00 01 00 / 00 00 02 00
    if (data[0] == 0x00 && data[1] == 0x00 && data[3] == 0x00
        && (data[2] == 0x01 || data[2] == 0x02)) {
      isIcon = true;
      return true;
    }
    // WebP: RIFF????WEBP \u2014 WPF lacks a built-in codec, deliberately fall through to hex.
    return false;
  }

  private void OnModeChanged(object sender, RoutedEventArgs e) {
    if (!IsLoaded) return;
    _imageMode = ImageMode.IsChecked == true;
    _hexMode = HexMode.IsChecked == true;
    _hexLines = null;
    _textLines = null;
    if (_hexMode && _autoWidth) RecalcAutoWidth();
    RefreshContent();
  }

  private void OnEncodingChanged(object sender, SelectionChangedEventArgs e) {
    if (!IsLoaded) return;
    if (!_hexMode) {
      _textLines = null;
      RefreshContent();
    }
  }

  private void OnWrapChanged(object sender, RoutedEventArgs e) {
    if (!IsLoaded) return;
    // Best-effort wrap on currently-realized rows. Virtualized rows recycled
    // off-screen reset to the ItemTemplate default (NoWrap) on re-realization;
    // a fully persistent wrap would need an ElementName binding + bool→wrap
    // converter — punted until a user actually asks for it.
    var wrap = WrapToggle.IsChecked == true ? TextWrapping.Wrap : TextWrapping.NoWrap;
    foreach (var item in FindVisualChildren<TextBlock>(ContentBox))
      item.TextWrapping = wrap;
  }

  private static IEnumerable<T> FindVisualChildren<T>(System.Windows.DependencyObject root) where T : System.Windows.DependencyObject {
    if (root == null) yield break;
    var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
    for (var i = 0; i < count; i++) {
      var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
      if (child is T t) yield return t;
      foreach (var nested in FindVisualChildren<T>(child)) yield return nested;
    }
  }

  private void OnBytesPerRowChanged(object sender, SelectionChangedEventArgs e) {
    if (!IsLoaded) return;
    var selected = (BytesPerRowBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Auto";
    if (selected == "Auto") {
      _autoWidth = true;
      RecalcAutoWidth();
    }
    else if (int.TryParse(selected, out var bpr) && bpr > 0) {
      _autoWidth = false;
      _bytesPerRow = bpr;
    }
    _hexLines = null;
    if (_hexMode) RefreshContent();
  }

  private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e) {
    if (!IsLoaded || !_hexMode || !_autoWidth) return;
    var oldBpr = _bytesPerRow;
    RecalcAutoWidth();
    if (_bytesPerRow != oldBpr) {
      _hexLines = null;
      RefreshContent();
    }
  }

  private void RecalcAutoWidth() {
    // Estimate: each byte takes ~3 chars in hex + 1 char in ASCII + offset (10) + separators
    // Monospace char width ~ 7.2px at font size 12
    var availableWidth = HexList.ActualWidth > 50 ? HexList.ActualWidth - 40 : ActualWidth - 60;
    if (availableWidth <= 0) availableWidth = 800;
    const double charWidth = 7.2;
    // Per byte: "XX " (3 chars hex) + 1 char ascii = 4 chars. Plus offset (10 chars) + gaps
    var charsPerByte = 4.0;
    var fixedChars = 13.0; // offset + separators
    var maxBytes = (int)((availableWidth / charWidth - fixedChars) / charsPerByte);
    _bytesPerRow = Math.Max(8, maxBytes);
  }

  private bool _analyzeMode;

  public void ShowData(string entryName, byte[] data, bool hex, bool analyzeMode) {
    _analyzeMode = analyzeMode;
    ShowData(entryName, data, hex);
    if (analyzeMode) {
      StatsToggle.IsChecked = true;
      // Force show stats immediately even if window isn't fully loaded yet
      ApplyStatsVisibility(true);
    }
  }

  private void OnStatsToggled(object sender, RoutedEventArgs e) {
    ApplyStatsVisibility(StatsToggle.IsChecked == true);
  }

  private void ApplyStatsVisibility(bool show) {
    if (show) {
      SplitterColumn.Width = new GridLength(4);
      StatsSplitter.Visibility = Visibility.Visible;
      StatsColumn.Width = new GridLength(280);
      StatsPanel.Visibility = Visibility.Visible;
      if (_data.Length > 0)
        StatsControl.Data = _data;
    }
    else {
      SplitterColumn.Width = new GridLength(0);
      StatsSplitter.Visibility = Visibility.Collapsed;
      StatsColumn.Width = new GridLength(0);
      StatsPanel.Visibility = Visibility.Collapsed;
    }
  }

  private Encoding GetEncoding() {
    var selected = (EncodingBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "UTF-8";
    return selected switch {
      "ASCII" => Encoding.ASCII,
      "Latin-1" => Encoding.Latin1,
      "UTF-16 LE" => Encoding.Unicode,
      _ => Encoding.UTF8,
    };
  }

  private void UpdateFrequencyPercentiles() {
    if (_data.Length == 0) return;
    var freq = BinaryStatistics.ComputeByteFrequency(_data);

    // Build sorted frequency list for percentile mapping
    var sorted = new long[256];
    Array.Copy(freq, sorted, 256);
    Array.Sort(sorted);

    var percentiles = new byte[256];
    for (var i = 0; i < 256; i++) {
      var rank = Array.BinarySearch(sorted, freq[i]);
      // Handle duplicates: find first occurrence
      while (rank > 0 && sorted[rank - 1] == freq[i]) rank--;
      percentiles[i] = (byte)(rank * 255 / 255);
    }

    HexLineControl.FrequencyPercentiles = percentiles;
    HexLineControl.ColorizeHex = _analyzeMode;
  }

  private static string FormatSize(long bytes) => bytes switch {
    < 1024 => $"{bytes} bytes",
    < 1024 * 1024 => $"{bytes / 1024.0:F1} KB ({bytes:N0} bytes)",
    _ => $"{bytes / (1024.0 * 1024):F1} MB ({bytes:N0} bytes)",
  };
}
