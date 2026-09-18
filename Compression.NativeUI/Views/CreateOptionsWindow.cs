using System.Drawing;
using Hawkynt.NativeForms;
using Hawkynt.NativeForms.Drawing;
using F = Compression.Lib.FormatDetector.Format;

namespace Compression.NativeUI.Views;

/// <summary>
/// The compression knobs for a create (or reconfigure) run. Rows appear only when the chosen format
/// and method actually offer them, and the format's own option schema is appended underneath.
/// </summary>
internal sealed class CreateOptionsWindow : Form {
  private const int LabelWidth = 110;
  private const int RowHeight = 28;
  private const int Gutter = 16;
  private const int ContentWidth = 448 - LabelWidth;

  private readonly Label _formatLabel = new();
  private readonly Panel _body = new() { AutoScroll = true };

  private readonly ComboBox _method = new() { DropDownStyle = ComboBoxStyle.DropDownList };
  private readonly ComboBox _level = new() { DropDownStyle = ComboBoxStyle.DropDownList };
  private readonly Label _dictSizeCaption = new();
  private readonly ComboBox _dictSize = new() { DropDownStyle = ComboBoxStyle.DropDownList };
  private readonly ComboBox _wordSize = new() { DropDownStyle = ComboBoxStyle.DropDownList };
  private readonly ComboBox _solidSize = new() { DropDownStyle = ComboBoxStyle.DropDownList };
  private readonly ComboBox _threads = new() { DropDownStyle = ComboBoxStyle.DropDownList };
  private readonly TextBox _password = new() { UseSystemPasswordChar = true };
  private readonly ComboBox _encryptionMethod = new() { DropDownStyle = ComboBoxStyle.DropDownList };
  private readonly CheckBox _encryptFilenames = new() { Text = "Encrypt file names" };
  private readonly CheckBox _makeSfx = new() { Text = "Self-extracting archive" };
  private readonly ComboBox _sfxType = new() { DropDownStyle = ComboBoxStyle.DropDownList };
  private readonly ComboBox _sfxTarget = new() { DropDownStyle = ComboBoxStyle.DropDownList };

  private readonly MemoryBanner _memory = new();
  private readonly HintBanner _hint = new();
  private readonly Panel _formatSpecific = new();

  private readonly Button _create = new() { Text = "Create", DialogResult = DialogResult.OK };
  private readonly Button _cancel = new() { Text = "Cancel", DialogResult = DialogResult.Cancel };
  private readonly ToolTip _toolTips = new();

  private readonly List<(Control? Caption, Control Field, Func<bool> Visible)> _rows = [];
  private readonly List<(Control Caption, Control Field, FormatOptionViewModel Model)> _optionRows = [];
  private bool _suppressSync;

  public CreateOptionsWindow(F format) {
    this.Options = new(format);

    this.Text = "Compression Options";
    this.ClientSize = new(480, 680);
    this.StartPosition = FormStartPosition.CenterParent;
    this.FormBorderStyle = FormBorderStyle.FixedDialog;
    this.MaximizeBox = false;
    this.MinimizeBox = false;

    this.BuildRows();
    this.BuildFormatSpecificRows();
    this.BindFromViewModel();

    this._create.Click += (_, _) => {
      this.DialogResult = DialogResult.OK;
      this.Close();
    };
    this._cancel.Click += (_, _) => this.Close();
    this.AcceptButton = this._create;
    this.CancelButton = this._cancel;

    this.Controls.AddRange(this._formatLabel, this._body, this._create, this._cancel);
    this.Options.PropertyChanged += (_, _) => this.RefreshLayout();
    this.RefreshLayout();
  }

  /// <summary>The knobs the user chose. Read after the dialog returns <see cref="DialogResult.OK"/>.</summary>
  public CreateOptionsViewModel Options { get; }

  private void BuildRows() {
    var font = DefaultTheme.Instance.DefaultFont;
    this._formatLabel.Font = new(font.Family, 11f, FontStyle.Bold);
    this._formatLabel.Text = this.Options.FormatLabel;

    Bind(this._method, () => this.Options.Methods, v => this.Options.SelectedMethod = v);
    Bind(this._level, () => this.Options.LevelOptions, v => this.Options.SelectedLevel = v);
    Bind(this._dictSize, () => this.Options.DictSizeOptions, v => this.Options.SelectedDictSize = v);
    Bind(this._wordSize, () => this.Options.WordSizeOptions, v => this.Options.SelectedWordSize = v);
    Bind(this._solidSize, () => this.Options.SolidSizeOptions, v => this.Options.SelectedSolidSize = v);
    Bind(this._threads, () => this.Options.ThreadOptions, v => this.Options.SelectedThreads = v);
    Bind(this._encryptionMethod, () => this.Options.EncryptionMethodOptions, v => this.Options.SelectedEncryptionMethod = v);
    Bind(this._sfxType, () => this.Options.SfxTypeOptions, v => this.Options.SelectedSfxType = v);
    Bind(this._sfxTarget, () => this.Options.SfxTargetOptions, v => this.Options.SelectedSfxTarget = v);

    this._password.TextChanged += (_, _) => this.Options.Password = this._password.Text;
    this._encryptFilenames.CheckedChanged += (_, _) => this.Options.EncryptFilenames = this._encryptFilenames.Checked;
    this._makeSfx.CheckedChanged += (_, _) => this.Options.MakeSfx = this._makeSfx.Checked;

    this.AddRow("Method:", this._method, () => this.Options.ShowMethod);
    this.AddRow("Level:", this._level, () => this.Options.ShowLevel);
    this.AddRow(this._dictSizeCaption, this._dictSize, () => this.Options.ShowDictSize);
    this.AddRow("Word size:", this._wordSize, () => this.Options.ShowWordSize);
    this.AddRow("Solid block:", this._solidSize, () => this.Options.ShowSolidSize);
    this.AddRow("Threads:", this._threads, () => this.Options.ShowThreads);
    this.AddRow("Password:", this._password, () => this.Options.ShowPassword);
    this.AddRow("Encryption:", this._encryptionMethod, () => this.Options.ShowEncryptionMethod);
    this.AddRow("Headers:", this._encryptFilenames, () => this.Options.ShowEncryptFilenames);
    this.AddRow("SFX:", this._makeSfx, () => this.Options.ShowSfx);
    this.AddRow("SFX type:", this._sfxType, () => this.Options.ShowSfxOptions);
    this.AddRow("SFX target:", this._sfxTarget, () => this.Options.ShowSfxOptions);

    this._body.Controls.AddRange(this._memory, this._hint, this._formatSpecific);

    void Bind(ComboBox box, Func<string[]> items, Action<string> setter) {
      box.SelectedIndexChanged += (_, _) => {
        if (this._suppressSync) return;
        if (box.SelectedItem is string value) setter(value);
      };
      box.Tag = items;
    }
  }

  private void AddRow(string caption, Control field, Func<bool> visible)
    => this.AddRow(new Label { Text = caption }, field, visible);

  private void AddRow(Control caption, Control field, Func<bool> visible) {
    this._body.Controls.AddRange(caption, field);
    this._rows.Add((caption, field, visible));
  }

  /// <summary>
  /// Builds one row per knob the format's schema publishes: a combo for enums and preset integers,
  /// a checkbox for booleans, a text box for everything else.
  /// </summary>
  private void BuildFormatSpecificRows() {
    foreach (var option in this.Options.FormatSpecificOptions) {
      var caption = new Label { Text = option.DisplayName };
      Control field;

      if (option.IsEnum) {
        var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        combo.Items.AddRange(option.AllowedValues);
        combo.SelectedItem = option.CurrentValue;
        combo.SelectedIndexChanged += (_, _) => {
          if (combo.SelectedItem is string v) option.CurrentValue = v;
        };
        field = combo;
      } else if (option.IsBool) {
        var check = new CheckBox { Checked = option.IsChecked };
        check.CheckedChanged += (_, _) => option.IsChecked = check.Checked;
        field = check;
      } else {
        var text = new TextBox { Text = option.CurrentValue };
        text.TextChanged += (_, _) => option.CurrentValue = text.Text;
        field = text;
      }

      if (option.ToolTip is { Length: > 0 } tip) {
        this._toolTips.SetToolTip(caption, tip);
        this._toolTips.SetToolTip(field, tip);
      }

      this._formatSpecific.Controls.AddRange(caption, field);
      this._optionRows.Add((caption, field, option));

      // A knob whose DependsOn condition changes may reveal or hide its siblings.
      option.PropertyChanged += (_, _) => this.RefreshLayout();
    }
  }

  private void BindFromViewModel() {
    this._suppressSync = true;
    try {
      Fill(this._method, this.Options.Methods, this.Options.SelectedMethod);
      Fill(this._level, this.Options.LevelOptions, this.Options.SelectedLevel);
      Fill(this._dictSize, this.Options.DictSizeOptions, this.Options.SelectedDictSize);
      Fill(this._wordSize, this.Options.WordSizeOptions, this.Options.SelectedWordSize);
      Fill(this._solidSize, this.Options.SolidSizeOptions, this.Options.SelectedSolidSize);
      Fill(this._threads, this.Options.ThreadOptions, this.Options.SelectedThreads);
      Fill(this._encryptionMethod, this.Options.EncryptionMethodOptions, this.Options.SelectedEncryptionMethod);
      Fill(this._sfxType, this.Options.SfxTypeOptions, this.Options.SelectedSfxType);
      Fill(this._sfxTarget, this.Options.SfxTargetOptions, this.Options.SelectedSfxTarget);
    } finally {
      this._suppressSync = false;
    }

    this._dictSizeCaption.Text = this.Options.DictSizeLabel;
    this._password.Enabled = this.Options.PasswordEnabled;
    this._encryptionMethod.Enabled = this.Options.PasswordEnabled;
    this._encryptFilenames.Enabled = this.Options.PasswordEnabled;

    this._memory.Set(this.Options.MemoryEstimate, this.Options.MemoryForeground, this.Options.MemoryBackground);
    this._hint.Text = this.Options.HintText;

    static void Fill(ComboBox box, string[] items, string selected) {
      box.Items.Clear();
      box.Items.AddRange(items.Cast<object>().ToArray());
      if (items.Length > 0) box.SelectedItem = items.Contains(selected) ? selected : items[0];
    }
  }

  /// <summary>Stacks the visible rows, then the banners, then the schema knobs.</summary>
  private void RefreshLayout() {
    this.BindFromViewModel();

    var y = 0;
    foreach (var (caption, field, visible) in this._rows) {
      var show = visible();
      if (caption is not null) caption.Visible = show;
      field.Visible = show;
      if (!show) continue;

      if (caption is not null) caption.Bounds = new(0, y + 3, LabelWidth, 22);
      field.Bounds = new(LabelWidth, y, ContentWidth, 24);
      y += RowHeight;
    }

    y += 4;
    this._memory.Visible = this.Options.ShowMemoryEstimate;
    if (this._memory.Visible) {
      this._memory.Bounds = new(0, y, LabelWidth + ContentWidth, 40);
      y += 44;
    }

    this._hint.Bounds = new(0, y, LabelWidth + ContentWidth, 72);
    y += 78;

    this._formatSpecific.Visible = this.Options.ShowFormatSpecific;
    if (this._formatSpecific.Visible) {
      var optionY = 0;
      foreach (var (caption, field, model) in this._optionRows) {
        var show = model.IsVisible;
        caption.Visible = show;
        field.Visible = show;
        if (!show) continue;

        caption.Bounds = new(0, optionY + 3, LabelWidth, 22);
        field.Bounds = new(LabelWidth, optionY, ContentWidth, 24);
        optionY += RowHeight;
      }

      this._formatSpecific.Bounds = new(0, y, LabelWidth + ContentWidth, optionY);
    }

    this._formatLabel.Bounds = new(Gutter, Gutter, this.ClientSize.Width - 2 * Gutter, 24);
    this._body.Bounds = new(Gutter, Gutter + 32,
      this.ClientSize.Width - 2 * Gutter,
      Math.Max(0, this.ClientSize.Height - Gutter - 32 - 50));

    var buttonY = this.ClientSize.Height - Gutter - 30;
    this._cancel.Bounds = new(this.ClientSize.Width - Gutter - 90, buttonY, 90, 30);
    this._create.Bounds = new(this.ClientSize.Width - Gutter - 188, buttonY, 90, 30);
  }

  /// <summary>The memory estimate, tinted by how close the estimate is to available RAM.</summary>
  private sealed class MemoryBanner : OwnerDrawnControl {
    private string _text = "";
    private Color _foreground = Color.Gray;
    private Color _background = Color.WhiteSmoke;

    public void Set(string text, Color foreground, Color background) {
      this._text = text;
      this._foreground = foreground;
      this._background = background;
      this.Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e) {
      var g = e.Graphics;
      g.FillRoundedRectangle(this._background, new(0, 0, this.Width, this.Height), 3);

      var font = DefaultTheme.Instance.DefaultFont;
      var bold = new Font(font.Family, 9f, FontStyle.Bold);
      var y = 4;
      foreach (var line in this._text.Split('\n')) {
        g.DrawText(line, bold, this._foreground, new(8, y, this.Width - 16, 18), ContentAlignment.MiddleLeft);
        y += 18;
      }
    }
  }

  /// <summary>The per-method explanation, wrapped across the banner.</summary>
  private sealed class HintBanner : OwnerDrawnControl {
    protected override void OnPaint(PaintEventArgs e) {
      var g = e.Graphics;
      g.FillRoundedRectangle(Color.FromArgb(0xF0, 0xF0, 0xF0), new(0, 0, this.Width, this.Height), 4);

      var font = DefaultTheme.Instance.DefaultFont;
      var color = Color.FromArgb(0x55, 0x55, 0x55);
      var y = 5;

      foreach (var line in WrapLines(g, this.Text, font, this.Width - 16)) {
        if (y + 16 > this.Height) break;
        g.DrawText(line, font, color, new(8, y, this.Width - 16, 16), ContentAlignment.MiddleLeft);
        y += 16;
      }
    }

    /// <summary>Greedy word wrap — IGraphics measures text but does not lay it out.</summary>
    private static IEnumerable<string> WrapLines(IGraphics g, string text, Font font, int width) {
      foreach (var paragraph in text.Split('\n')) {
        var line = "";
        foreach (var word in paragraph.Split(' ')) {
          var candidate = line.Length == 0 ? word : line + " " + word;
          if (g.MeasureText(candidate, font).Width <= width) {
            line = candidate;
            continue;
          }

          if (line.Length > 0) yield return line;
          line = word;
        }
        if (line.Length > 0) yield return line;
      }
    }
  }
}
