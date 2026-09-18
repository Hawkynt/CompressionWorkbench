using System.Drawing;
using Compression.Registry;
using Hawkynt.NativeForms;
using Hawkynt.NativeForms.Drawing;

namespace Compression.NativeUI.Views;

/// <summary>
/// Renders a format's option schema as a labelled form, one row per knob: a check box for booleans,
/// a closed list for enums, an editable list for integers that publish presets, and a text field for
/// everything else. Rows gated by an unmet dependency are hidden, so the writer falls back to the
/// schema default for knobs that do not apply.
/// </summary>
internal sealed class TargetOptionsDialog : Form {
  private const int LabelWidth = 160;
  private const int RowHeight = 30;

  private readonly Label _heading = new() { Text = "Configure target format." };
  private readonly Label _subHeading = new() {
    ForeColor = Color.DimGray,
    Text = "Defaults are applied for any knob you don't change. Hover over a field for a description.",
  };

  private readonly Panel _optionsHost = new() { AutoScroll = true };
  private readonly Button _ok = new() { Text = "OK", DialogResult = DialogResult.OK };
  private readonly Button _cancel = new() { Text = "Cancel", DialogResult = DialogResult.Cancel };
  private readonly ToolTip _toolTips = new();

  private readonly List<RowState> _rows = [];

  public TargetOptionsDialog(IReadOnlyList<FormatOptionDescriptor> schema, string? formatDisplayName = null) {
    this.Text = string.IsNullOrEmpty(formatDisplayName) ? "Target options" : $"Target options — {formatDisplayName}";
    if (!string.IsNullOrEmpty(formatDisplayName))
      this._heading.Text = $"Configure target format: {formatDisplayName}";

    this._heading.Font = new(DefaultTheme.Instance.DefaultFont.Family, 9f, FontStyle.Bold);

    this.ClientSize = new(520, Math.Min(640, 150 + schema.Count * RowHeight));
    this.MinimumSize = new(420, 260);
    this.StartPosition = FormStartPosition.CenterParent;
    this.FormBorderStyle = FormBorderStyle.FixedDialog;
    this.MaximizeBox = false;
    this.MinimizeBox = false;

    this.BuildRows(schema);

    this._ok.Click += (_, _) => {
      this.Result = this.Harvest();
      this.DialogResult = DialogResult.OK;
      this.Close();
    };
    this._cancel.Click += (_, _) => this.Close();
    this.AcceptButton = this._ok;
    this.CancelButton = this._cancel;

    this.Controls.AddRange(this._heading, this._subHeading, this._optionsHost, this._ok, this._cancel);
    this.Resize += (_, _) => this.LayoutChildren();
    this.LayoutChildren();
    this.ApplyAllDependencies();
  }

  /// <summary>Collected values once OK is pressed; empty otherwise.</summary>
  public Dictionary<string, string> Result { get; private set; } = [];

  private void BuildRows(IReadOnlyList<FormatOptionDescriptor> schema) {
    foreach (var descriptor in schema) {
      var label = new Label { Text = descriptor.DisplayName + ":" };
      var control = CreateControl(descriptor);

      if (!string.IsNullOrEmpty(descriptor.Description)) {
        this._toolTips.SetToolTip(label, descriptor.Description);
        this._toolTips.SetToolTip(control, descriptor.Description);
      }

      this._optionsHost.Controls.AddRange(label, control);
      this._rows.Add(new(descriptor, label, control));
    }

    // Wire change notifications once every row exists, so a dependent row can re-evaluate against
    // any controller regardless of declaration order.
    foreach (var row in this._rows)
      switch (row.Control) {
        case TextBox textBox:
          textBox.TextChanged += (_, _) => this.ApplyAllDependencies();
          break;
        case CheckBox checkBox:
          checkBox.CheckedChanged += (_, _) => this.ApplyAllDependencies();
          break;
        case ComboBox comboBox:
          comboBox.SelectedIndexChanged += (_, _) => this.ApplyAllDependencies();
          comboBox.TextChanged += (_, _) => this.ApplyAllDependencies();
          break;
      }
  }

  private static Control CreateControl(FormatOptionDescriptor descriptor) {
    switch (descriptor.Kind) {
      case FormatOptionKind.Boolean:
        return new CheckBox { Checked = ParseBool(descriptor.Default) };

      case FormatOptionKind.Enum: {
        var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        if (descriptor.AllowedValues is { } values)
          foreach (var value in values)
            combo.Items.Add(value);
        SelectInitial(combo, descriptor.Default, allowTypedValue: false);
        return combo;
      }

      case FormatOptionKind.Integer when descriptor.AllowedValues is { Count: > 0 } presets: {
        var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDown };
        foreach (var value in presets) combo.Items.Add(value);
        SelectInitial(combo, descriptor.Default, allowTypedValue: true);
        return combo;
      }

      default:
        return new TextBox { Text = descriptor.Default ?? "" };
    }
  }

  private static void SelectInitial(ComboBox combo, string? value, bool allowTypedValue) {
    if (string.IsNullOrEmpty(value)) {
      if (combo.Items.Count > 0) combo.SelectedIndex = 0;
      return;
    }

    for (var i = 0; i < combo.Items.Count; ++i)
      if (string.Equals(combo.Items[i]?.ToString(), value, StringComparison.OrdinalIgnoreCase)) {
        combo.SelectedIndex = i;
        return;
      }

    // The value is not one of the presets: an editable combo shows it as typed text, a closed one
    // falls back to the first preset.
    if (allowTypedValue) combo.Text = value;
    else if (combo.Items.Count > 0) combo.SelectedIndex = 0;
  }

  private static bool ParseBool(string? s)
    => !string.IsNullOrEmpty(s) && (s.Equals("true", StringComparison.OrdinalIgnoreCase) || s == "1");

  private static string ReadControlValue(Control control) => control switch {
    TextBox textBox => textBox.Text,
    CheckBox checkBox => checkBox.Checked ? "true" : "false",
    ComboBox { DropDownStyle: ComboBoxStyle.DropDown } combo => combo.Text ?? combo.SelectedItem?.ToString() ?? "",
    ComboBox combo => combo.SelectedItem?.ToString() ?? "",
    _ => "",
  };

  /// <summary>
  /// Re-evaluates every row's dependency gate against the current state of its controller. One pass
  /// suffices even for a chain — a controller always appears before what depends on it.
  /// </summary>
  private void ApplyAllDependencies() {
    foreach (var row in this._rows) {
      var visible = string.IsNullOrEmpty(row.Descriptor.DependsOn) || this.EvaluateDependency(row.Descriptor.DependsOn);
      row.Label.Visible = visible;
      row.Control.Visible = visible;
    }

    this.LayoutRows();
  }

  private bool EvaluateDependency(string spec) {
    // The spec reads "OtherKey=value1|value2".
    var eq = spec.IndexOf('=');
    if (eq <= 0) return true;

    var otherKey = spec[..eq].Trim();
    var allowed = spec[(eq + 1)..].Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    var controller = this._rows.FirstOrDefault(r => string.Equals(r.Descriptor.Key, otherKey, StringComparison.OrdinalIgnoreCase));
    // An unknown controller fails open: hiding a knob because of a typo in the schema would be worse
    // than showing one that does not apply.
    if (controller is null) return true;

    // A hidden controller hides everything that depends on it.
    if (!controller.Label.Visible) return false;

    var current = ReadControlValue(controller.Control);
    return allowed.Any(v => string.Equals(current, v, StringComparison.OrdinalIgnoreCase));
  }

  private Dictionary<string, string> Harvest() {
    var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    foreach (var row in this._rows) {
      if (!row.Label.Visible) continue; // A gated-out knob keeps its schema default.
      values[row.Descriptor.Key] = ReadControlValue(row.Control);
    }
    return values;
  }

  private void LayoutChildren() {
    const int Gutter = 12;
    var width = this.ClientSize.Width - 2 * Gutter;

    this._heading.Bounds = new(Gutter, Gutter, width, 20);
    this._subHeading.Bounds = new(Gutter, Gutter + 22, width, 32);
    this._optionsHost.Bounds = new(Gutter, Gutter + 58, width, Math.Max(60, this.ClientSize.Height - Gutter - 58 - 48));

    var buttonY = this.ClientSize.Height - Gutter - 28;
    this._cancel.Bounds = new(this.ClientSize.Width - Gutter - 80, buttonY, 80, 28);
    this._ok.Bounds = new(this.ClientSize.Width - Gutter - 168, buttonY, 80, 28);

    this.LayoutRows();
  }

  private void LayoutRows() {
    var width = Math.Max(240, this._optionsHost.Width - 20);
    var y = 0;

    foreach (var row in this._rows) {
      if (!row.Label.Visible) continue;
      row.Label.Bounds = new(0, y + 4, LabelWidth - 8, 20);
      row.Control.Bounds = new(LabelWidth, y, Math.Max(80, width - LabelWidth), 24);
      y += RowHeight;
    }
  }

  private sealed record RowState(FormatOptionDescriptor Descriptor, Label Label, Control Control);
}
