using System.Drawing;
using System.Text;
using Compression.Analysis.Statistics;
using Compression.NativeUI.Controls;
using Compression.NativeUI.Theming;
using Hawkynt.NativeForms;
using Hawkynt.NativeForms.Drawing;

namespace Compression.NativeUI.Views;

/// <summary>
/// Shows one entry's bytes as a picture, as text, or as a hex dump, with an optional statistics
/// side panel. Multi-frame images get playback controls.
/// </summary>
internal sealed class PreviewWindow : Form {
  private const int ToolbarHeight = 30;
  private const int StatusHeight = 26;
  private const int StatsPanelWidth = 280;

  private static readonly Font MonoFont = new("Cascadia Mono", 9f, FontStyle.Regular);

  private readonly ToolStrip _toolbar = new();
  private readonly ToolStripButton _imageMode = new("Image") { CheckOnClick = true, Visible = false };
  private readonly ToolStripButton _textMode = new("Text") { CheckOnClick = true, Checked = true };
  private readonly ToolStripButton _hexMode = new("Hex") { CheckOnClick = true };
  private readonly ToolStripButton _wrapToggle = new() { CheckOnClick = true, ToolTipText = "Toggle word wrap" };
  private readonly ToolStripButton _statsToggle = new() { CheckOnClick = true, ToolTipText = "Toggle statistics panel" };
  private readonly ComboBox _encodingBox = new() { DropDownStyle = ComboBoxStyle.DropDownList };
  private readonly ComboBox _bytesPerRowBox = new() { DropDownStyle = ComboBoxStyle.DropDownList };
  private readonly ToolStripStatusLabel _sizeInfo = new();

  private readonly SplitContainer _split = new() { FixedPanel = FixedPanel.Panel2 };
  private readonly VirtualRowView _rows = new();
  private readonly PictureBox _picture = new() { SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(0x20, 0x20, 0x20) };
  private readonly StatisticsControl _stats = new() { CompactMode = true };

  private readonly StatusStrip _status = new();
  private readonly ToolStripStatusLabel _sizeLabel = new();
  private readonly ToolStripSeparator _frameSeparator = new() { Visible = false };
  private readonly ToolStripButton _prevFrame = new("◀") { Visible = false, ToolTipText = "Previous frame (Left arrow)" };
  private readonly ToolStripButton _playPause = new("▶") { Visible = false, ToolTipText = "Play / pause animation (Space)" };
  private readonly ToolStripButton _nextFrame = new("▶▶") { Visible = false, ToolTipText = "Next frame (Right arrow)" };
  private readonly ToolStripStatusLabel _frameIndexLabel = new() { Visible = false };

  private readonly HexRowRenderer _hexRenderer = new(MonoFont);
  private readonly Hawkynt.NativeForms.Timer _animationTimer = new();

  private byte[] _data = [];
  private bool _hexMode1;
  private bool _imageMode1;
  private bool _analyzeMode;
  private LazyTextLines? _textLines;
  private int _bytesPerRow = 16;
  private bool _autoWidth = true;
  private bool _loaded;

  private DecodedImage? _decoded;
  private IImage[]? _frames;
  private int _frameIndex;
  private bool _isPlaying;

  public PreviewWindow() {
    this.Text = "Preview";
    this.ClientSize = new(960, 640);
    this.MinimumSize = new(500, 350);
    this.StartPosition = FormStartPosition.CenterParent;

    this.BuildToolbar();
    this.BuildContent();
    this.BuildStatusBar();

    this._animationTimer.Tick += this.OnAnimationTick;
    this.Resize += (_, _) => this.LayoutChildren();
    this.SizeChanged += (_, _) => this.OnWindowSizeChanged();
    this.FormClosed += (_, _) => this.StopAnimation();

    this.LayoutChildren();
    this._loaded = true;
  }

  private void BuildToolbar() {
    // The three view modes are mutually exclusive, like the WPF radio group.
    foreach (var button in new[] { this._imageMode, this._textMode, this._hexMode }) {
      var captured = button;
      button.CheckedChanged += (_, _) => {
        if (!captured.Checked) return;
        foreach (var other in new[] { this._imageMode, this._textMode, this._hexMode })
          if (!ReferenceEquals(other, captured)) other.Checked = false;
        this.OnModeChanged();
      };
    }

    this._wrapToggle.Image = Images.Icon(IconKeys.ViewText, 14);
    this._wrapToggle.CheckedChanged += (_, _) => this.RefreshContent();
    this._statsToggle.Image = Images.Icon(IconKeys.Analyze, 14);
    this._statsToggle.CheckedChanged += (_, _) => this.ApplyStatsVisibility(this._statsToggle.Checked);

    this._encodingBox.Items.AddRange(["UTF-8", "ASCII", "Latin-1", "UTF-16 LE"]);
    this._encodingBox.SelectedIndex = 0;
    this._encodingBox.Width = 120;
    this._encodingBox.SelectedIndexChanged += (_, _) => {
      if (!this._loaded || this._hexMode1) return;
      this._textLines = null;
      this.RefreshContent();
    };

    this._bytesPerRowBox.Items.AddRange(["Auto", "8", "16", "32", "64"]);
    this._bytesPerRowBox.SelectedIndex = 0;
    this._bytesPerRowBox.Width = 60;
    this._bytesPerRowBox.SelectedIndexChanged += (_, _) => this.OnBytesPerRowChanged();

    this._toolbar.Items.AddRange([
      this._imageMode, this._textMode, this._hexMode,
      new ToolStripSeparator(),
      this._wrapToggle,
      new ToolStripSeparator(),
      new ToolStripControlHost(this._encodingBox),
      new ToolStripSeparator(),
      new ToolStripStatusLabel("Bytes/row:"),
      new ToolStripControlHost(this._bytesPerRowBox),
      new ToolStripSeparator(),
      this._statsToggle,
      new ToolStripSeparator(),
      this._sizeInfo,
    ]);

    this.Controls.Add(this._toolbar);
  }

  private void BuildContent() {
    this._rows.RowHeight = 16;
    this._rows.RenderRow = this.RenderRow;
    this._picture.Visible = false;

    this._split.Panel1.Controls.AddRange(this._rows, this._picture);
    this._split.Panel2.Controls.Add(this._stats);
    this._split.Panel2Collapsed = true;
    this._rows.Dock = DockStyle.Fill;
    this._picture.Dock = DockStyle.Fill;
    this._stats.Dock = DockStyle.Fill;

    this.Controls.Add(this._split);
  }

  private void BuildStatusBar() {
    this._prevFrame.Click += (_, _) => this.StepFrame(-1);
    this._nextFrame.Click += (_, _) => this.StepFrame(+1);
    this._playPause.Click += (_, _) => {
      if (this._isPlaying) this.StopAnimation();
      else this.StartAnimation();
    };

    this._status.Items.AddRange([
      this._sizeLabel, this._frameSeparator,
      this._prevFrame, this._playPause, this._nextFrame, this._frameIndexLabel,
    ]);
    this.Controls.Add(this._status);
  }

  private void LayoutChildren() {
    this._toolbar.Bounds = new(0, 0, this.ClientSize.Width, ToolbarHeight);
    this._status.Bounds = new(0, this.ClientSize.Height - StatusHeight, this.ClientSize.Width, StatusHeight);
    this._split.Bounds = new(0, ToolbarHeight, this.ClientSize.Width, Math.Max(0, this.ClientSize.Height - ToolbarHeight - StatusHeight));
    if (!this._split.Panel2Collapsed)
      this._split.SplitterDistance = Math.Max(100, this._split.Width - StatsPanelWidth);
  }

  /// <summary>Shows <paramref name="data"/>, picking the image renderer when the bytes look like one.</summary>
  public void ShowData(string entryName, byte[] data, bool hex = false) {
    this._data = data;
    this._hexMode1 = hex;
    this._textLines = null;
    this.StopAnimation();
    this._frames = null;
    this._decoded = null;
    this._frameIndex = 0;
    this.SetFrameNavVisible(false);

    this.Text = $"Preview — {entryName}";
    this._sizeLabel.Text = FormatSize(data.Length);
    this._sizeInfo.Text = FormatSize(data.Length);

    // Sniff for a known image signature BEFORE rendering: multi-megabyte image bytes become
    // millions of characters in the text/hex pipeline and lock up the UI thread.
    if (!hex && this.TryRenderAsImage(data)) {
      this._imageMode1 = true;
      this._imageMode.Visible = true;
      this._imageMode.Checked = true;
      return;
    }

    this._imageMode1 = false;
    this._imageMode.Visible = false;
    if (hex) this._hexMode.Checked = true;
    else this._textMode.Checked = true;
    this.RefreshContent();
  }

  /// <summary>Shows <paramref name="data"/> with the statistics panel already open.</summary>
  public void ShowData(string entryName, byte[] data, bool hex, bool analyzeMode) {
    this._analyzeMode = analyzeMode;
    this.ShowData(entryName, data, hex);
    if (!analyzeMode) return;
    this._statsToggle.Checked = true;
    this.ApplyStatsVisibility(true);
  }

  private void RefreshContent() {
    if (this._imageMode1) {
      this._rows.Visible = false;
      this._picture.Visible = true;
      this._encodingBox.Enabled = false;
      return;
    }

    this._picture.Visible = false;
    this._rows.Visible = true;

    if (this._hexMode1) {
      this._encodingBox.Enabled = false;
      this.UpdateFrequencyPercentiles();

      var lines = new LazyHexLines(this._data, this._bytesPerRow);
      this._hexRenderer.Lines = lines;
      this._rows.RowCount = lines.Count;
      this._rows.ContentWidth = this._hexRenderer.MeasureRowWidth(
        this._bytesPerRow, this._data.Length > 0xFFFFFF ? 8 : this._data.Length > 0xFFFF ? 6 : 4);
    } else {
      this._encodingBox.Enabled = true;
      this._hexRenderer.Lines = null;

      this._textLines = new(this._data, this.GetEncoding());
      this._rows.RowCount = this._textLines.Count;
      this._rows.ContentWidth = 2000;
    }

    this._sizeLabel.Text = FormatSize(this._data.Length);
    this._rows.Invalidate();
  }

  private void RenderRow(RowPaintContext context) {
    if (this._hexMode1) {
      this._hexRenderer.Render(context);
      return;
    }

    if (this._textLines is not { } lines || context.Index >= lines.Count) return;
    var color = context.Selected ? DefaultTheme.Instance.SelectionText : DefaultTheme.Instance.ControlText;
    context.Graphics.DrawText(lines[context.Index], MonoFont, color, context.Bounds, ContentAlignment.MiddleLeft);
  }

  private void OnModeChanged() {
    if (!this._loaded) return;
    this._imageMode1 = this._imageMode.Checked;
    this._hexMode1 = this._hexMode.Checked;
    this._textLines = null;
    if (this._hexMode1 && this._autoWidth) this.RecalcAutoWidth();
    this.RefreshContent();
  }

  private void OnBytesPerRowChanged() {
    if (!this._loaded) return;

    var selected = this._bytesPerRowBox.SelectedItem as string ?? "Auto";
    if (selected == "Auto") {
      this._autoWidth = true;
      this.RecalcAutoWidth();
    } else if (int.TryParse(selected, out var bpr) && bpr > 0) {
      this._autoWidth = false;
      this._bytesPerRow = bpr;
    }

    if (this._hexMode1) this.RefreshContent();
  }

  private void OnWindowSizeChanged() {
    this.LayoutChildren();
    if (!this._loaded || !this._hexMode1 || !this._autoWidth) return;

    var previous = this._bytesPerRow;
    this.RecalcAutoWidth();
    if (this._bytesPerRow != previous) this.RefreshContent();
  }

  /// <summary>
  /// Picks the widest byte count whose row still fits the viewport. Each byte costs three
  /// characters in the hex column plus one in the ASCII column, on top of the offset and separators.
  /// </summary>
  private void RecalcAutoWidth() {
    var charWidth = this._hexRenderer.CharWidth > 0 ? this._hexRenderer.CharWidth : 7;
    var available = this._rows.Width > 50 ? this._rows.Width - 40 : this.ClientSize.Width - 60;
    if (available <= 0) available = 800;

    const double CharsPerByte = 4.0;
    const double FixedChars = 13.0;
    var maxBytes = (int)(((double)available / charWidth - FixedChars) / CharsPerByte);
    this._bytesPerRow = Math.Max(8, maxBytes);
  }

  private void ApplyStatsVisibility(bool show) {
    this._split.Panel2Collapsed = !show;
    if (show && this._data.Length > 0) this._stats.Data = this._data;
    this.LayoutChildren();
  }

  private Encoding GetEncoding() => (this._encodingBox.SelectedItem as string) switch {
    "ASCII" => Encoding.ASCII,
    "Latin-1" => Encoding.Latin1,
    "UTF-16 LE" => Encoding.Unicode,
    _ => Encoding.UTF8,
  };

  /// <summary>
  /// Maps each byte value to its rank among all 256 counts, so the hex view can wash rare values
  /// green and common ones red.
  /// </summary>
  private void UpdateFrequencyPercentiles() {
    if (this._data.Length == 0) return;

    var freq = BinaryStatistics.ComputeByteFrequency(this._data);
    var sorted = new long[256];
    Array.Copy(freq, sorted, 256);
    Array.Sort(sorted);

    var percentiles = new byte[256];
    for (var i = 0; i < 256; ++i) {
      var rank = Array.BinarySearch(sorted, freq[i]);
      if (rank < 0) rank = ~rank;
      while (rank > 0 && sorted[rank - 1] == freq[i]) --rank;
      percentiles[i] = (byte)rank;
    }

    this._hexRenderer.FrequencyPercentiles = percentiles;
    this._hexRenderer.ColorizeHex = this._analyzeMode;
  }

  /// <summary>
  /// Decodes the bytes through NativeForms' image decoder (PNG, JPEG, GIF, BMP, ICO, CUR, PCX and
  /// ANI), materialising every frame so animation and manual stepping both work.
  /// </summary>
  private bool TryRenderAsImage(byte[] data) {
    if (!IsKnownImageSignature(data)) return false;

    try {
      var decoded = ImageDecoder.Decode(data);
      if (decoded.Frames.Count == 0) return false;

      var frames = new IImage[decoded.Frames.Count];
      for (var i = 0; i < frames.Length; ++i)
        frames[i] = Images.FromArgb(decoded.Width, decoded.Height, decoded.Frames[i].Argb);

      this._decoded = decoded;
      this._frames = frames;
      this._frameIndex = 0;
      this.UpdateFrameDisplay();

      var multiFrame = frames.Length > 1;
      this.SetFrameNavVisible(multiFrame);
      if (multiFrame && decoded.IsAnimated) this.StartAnimation();

      this._picture.Visible = true;
      this._rows.Visible = false;
      return true;
    } catch {
      // Codec error (corrupt JPEG, unsupported subformat) — fall through to text/hex.
      this._picture.Image = null;
      this._frames = null;
      this._decoded = null;
      return false;
    }
  }

  private static bool IsKnownImageSignature(ReadOnlySpan<byte> data) {
    if (data.Length < 4) return false;
    ReadOnlySpan<byte> pngMagic = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    if (data.Length >= 8 && data[..8].SequenceEqual(pngMagic)) return true;
    if (data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF) return true;
    if (data[0] == 0x47 && data[1] == 0x49 && data[2] == 0x46 && data[3] == 0x38) return true;
    if (data[0] == 0x42 && data[1] == 0x4D) return true;
    // ICO (type 1) and CUR (type 2) both start with a zero reserved word.
    return data[0] == 0x00 && data[1] == 0x00 && data[3] == 0x00 && data[2] is 0x01 or 0x02;
  }

  private void SetFrameNavVisible(bool visible) {
    this._frameSeparator.Visible = visible;
    this._prevFrame.Visible = visible;
    this._playPause.Visible = visible;
    this._nextFrame.Visible = visible;
    this._frameIndexLabel.Visible = visible;
  }

  private void UpdateFrameDisplay() {
    if (this._frames is not { Length: > 0 } frames) return;
    if ((uint)this._frameIndex >= (uint)frames.Length) this._frameIndex = 0;

    this._picture.Image = frames[this._frameIndex];
    this._frameIndexLabel.Text = frames.Length > 1 ? $"Frame {this._frameIndex + 1} / {frames.Length}" : "";
    this._picture.Invalidate();
  }

  private int CurrentDelayMs() {
    if (this._decoded is not { } decoded || this._frameIndex >= decoded.Frames.Count) return 100;
    var delay = decoded.Frames[this._frameIndex].DelayMilliseconds;
    // Encoders that write 0/1/2 hundredths mean "as fast as possible"; browsers floor that at
    // ~100 ms to avoid burning CPU, and so do we.
    return delay < 30 ? 100 : delay;
  }

  private void StepFrame(int direction) {
    if (this._frames is not { Length: > 0 } frames) return;
    this.StopAnimation();
    this._frameIndex = (this._frameIndex + direction + frames.Length) % frames.Length;
    this.UpdateFrameDisplay();
  }

  private void StartAnimation() {
    if (this._frames is not { Length: > 1 }) return;
    this._animationTimer.Interval = this.CurrentDelayMs();
    this._animationTimer.Start();
    this._isPlaying = true;
    this._playPause.Text = "⏸";
  }

  private void StopAnimation() {
    this._animationTimer.Stop();
    this._isPlaying = false;
    this._playPause.Text = "▶";
  }

  private void OnAnimationTick(object? sender, EventArgs e) {
    if (this._frames is not { Length: > 0 } frames) return;
    this._frameIndex = (this._frameIndex + 1) % frames.Length;
    this.UpdateFrameDisplay();
    this._animationTimer.Interval = this.CurrentDelayMs();
  }

  private static string FormatSize(long bytes) => bytes switch {
    < 1024 => $"{bytes} bytes",
    < 1024 * 1024 => $"{bytes / 1024.0:F1} KB ({bytes:N0} bytes)",
    _ => $"{bytes / (1024.0 * 1024):F1} MB ({bytes:N0} bytes)",
  };
}
