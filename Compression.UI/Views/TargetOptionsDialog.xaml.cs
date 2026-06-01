using System.Windows;
using Compression.Registry;
// Explicit WPF aliases — UseWindowsForms=true means bare type names collide
// with the WinForms types of the same name.
using TextBox = System.Windows.Controls.TextBox;
using TextBlock = System.Windows.Controls.TextBlock;
using CheckBox = System.Windows.Controls.CheckBox;
using ComboBox = System.Windows.Controls.ComboBox;
using Grid = System.Windows.Controls.Grid;
using RowDefinition = System.Windows.Controls.RowDefinition;
using TextChangedEventHandler = System.Windows.Controls.TextChangedEventHandler;
using TextBoxBase = System.Windows.Controls.Primitives.TextBoxBase;

namespace Compression.UI.Views;

/// <summary>
/// Modal dialog that renders an <see cref="IFormatOptionsSchema"/> as a
/// labelled form. Each <see cref="FormatOptionDescriptor"/> becomes one
/// row in a <c>Grid</c> (label | control):
/// <list type="bullet">
///   <item><see cref="FormatOptionKind.String"/> → <see cref="TextBox"/></item>
///   <item><see cref="FormatOptionKind.Integer"/> + <c>AllowedValues</c> →
///   editable <see cref="ComboBox"/> (preset dropdown that also accepts a
///   typed custom value).</item>
///   <item><see cref="FormatOptionKind.Integer"/> without presets →
///   <see cref="TextBox"/>.</item>
///   <item><see cref="FormatOptionKind.Boolean"/> → <see cref="CheckBox"/></item>
///   <item><see cref="FormatOptionKind.Enum"/> → non-editable
///   <see cref="ComboBox"/>.</item>
/// </list>
/// On <c>OK</c>, values are harvested into <see cref="Result"/> keyed by
/// <see cref="FormatOptionDescriptor.Key"/>. Rows hidden by a
/// <see cref="FormatOptionDescriptor.DependsOn"/> gate are skipped so the
/// writer reads the schema default for inapplicable knobs.
/// </summary>
public partial class TargetOptionsDialog : Window {

  private readonly List<RowState> _rows = [];

  /// <summary>Collected values once <c>OK</c> is pressed; empty otherwise.</summary>
  public Dictionary<string, string> Result { get; private set; } = [];

  public TargetOptionsDialog(IReadOnlyList<FormatOptionDescriptor> schema, string? formatDisplayName = null) {
    InitializeComponent();

    if (!string.IsNullOrEmpty(formatDisplayName)) {
      Title = $"Target options — {formatDisplayName}";
      HeadingLbl.Text = $"Configure target format: {formatDisplayName}";
    }

    BuildRows(schema);
    ApplyAllDependencies();
  }

  private void BuildRows(IReadOnlyList<FormatOptionDescriptor> schema) {
    for (var i = 0; i < schema.Count; ++i) {
      var desc = schema[i];
      OptionsHost.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

      var label = new TextBlock {
        Text = desc.DisplayName + ":",
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(0, 4, 8, 4),
      };
      if (!string.IsNullOrEmpty(desc.Description))
        label.ToolTip = desc.Description;
      Grid.SetRow(label, i);
      Grid.SetColumn(label, 0);
      OptionsHost.Children.Add(label);

      var control = CreateControl(desc);
      control.Margin = new Thickness(0, 4, 0, 4);
      if (!string.IsNullOrEmpty(desc.Description))
        control.ToolTip = desc.Description;
      Grid.SetRow(control, i);
      Grid.SetColumn(control, 1);
      OptionsHost.Children.Add(control);

      _rows.Add(new RowState(desc, label, control));
    }

    // Wire change notifications now that all rows exist so dependent rows
    // can re-evaluate against any controller.
    foreach (var row in _rows) {
      switch (row.Control) {
        case TextBox tb:
          tb.TextChanged += (_, _) => ApplyAllDependencies();
          break;
        case CheckBox cb:
          cb.Checked += (_, _) => ApplyAllDependencies();
          cb.Unchecked += (_, _) => ApplyAllDependencies();
          break;
        case ComboBox combo:
          combo.SelectionChanged += (_, _) => ApplyAllDependencies();
          combo.AddHandler(TextBoxBase.TextChangedEvent,
            new TextChangedEventHandler((_, _) => ApplyAllDependencies()));
          break;
      }
    }
  }

  private static FrameworkElement CreateControl(FormatOptionDescriptor desc) {
    switch (desc.Kind) {
      case FormatOptionKind.Boolean: {
        var cb = new CheckBox {
          IsChecked = ParseBool(desc.Default),
          VerticalAlignment = VerticalAlignment.Center,
        };
        return cb;
      }

      case FormatOptionKind.Enum: {
        var combo = new ComboBox { IsEditable = false };
        if (desc.AllowedValues != null) {
          foreach (var v in desc.AllowedValues)
            combo.Items.Add(v);
        }
        SelectInitial(combo, desc.Default, allowAdd: false);
        return combo;
      }

      case FormatOptionKind.Integer when desc.AllowedValues is { Count: > 0 }: {
        var combo = new ComboBox { IsEditable = true };
        foreach (var v in desc.AllowedValues)
          combo.Items.Add(v);
        SelectInitial(combo, desc.Default, allowAdd: true);
        return combo;
      }

      case FormatOptionKind.Integer:
      case FormatOptionKind.String:
      default: {
        var tb = new TextBox {
          Text = desc.Default ?? string.Empty,
          VerticalContentAlignment = VerticalAlignment.Center,
        };
        return tb;
      }
    }
  }

  private static void SelectInitial(ComboBox combo, string? value, bool allowAdd) {
    if (string.IsNullOrEmpty(value)) {
      if (combo.Items.Count > 0) combo.SelectedIndex = 0;
      return;
    }
    for (var i = 0; i < combo.Items.Count; ++i) {
      if (string.Equals(combo.Items[i]?.ToString(), value, StringComparison.OrdinalIgnoreCase)) {
        combo.SelectedIndex = i;
        return;
      }
    }
    // Value isn't in the preset list — for editable combos surface it as
    // typed text; for closed combos fall back to the first preset.
    if (allowAdd) {
      combo.Text = value;
    } else if (combo.Items.Count > 0) {
      combo.SelectedIndex = 0;
    }
  }

  private static bool ParseBool(string? s) {
    if (string.IsNullOrEmpty(s)) return false;
    return s.Equals("true", StringComparison.OrdinalIgnoreCase) || s == "1";
  }

  private string ReadControlValue(FrameworkElement c) => c switch {
    TextBox tb => tb.Text,
    CheckBox cb => cb.IsChecked == true ? "true" : "false",
    ComboBox combo when combo.IsEditable => combo.Text ?? combo.SelectedItem?.ToString() ?? string.Empty,
    ComboBox combo => combo.SelectedItem?.ToString() ?? string.Empty,
    _ => string.Empty,
  };

  /// <summary>
  /// Re-evaluates every row's <c>DependsOn</c> gate against the current
  /// state of the controller row, hiding (Collapsed) any row whose gate
  /// fails. Single-pass over <see cref="_rows"/>; a cascading dependency
  /// (A → B → C) resolves correctly because A always evaluates before B.
  /// </summary>
  private void ApplyAllDependencies() {
    foreach (var row in _rows) {
      var dep = row.Descriptor.DependsOn;
      if (string.IsNullOrEmpty(dep)) {
        SetRowVisible(row, true);
        continue;
      }

      var visible = EvaluateDependency(dep);
      SetRowVisible(row, visible);
    }
  }

  private bool EvaluateDependency(string spec) {
    // Format: "OtherKey=v1|v2"
    var eq = spec.IndexOf('=');
    if (eq <= 0) return true;
    var otherKey = spec[..eq].Trim();
    var allowedRaw = spec[(eq + 1)..];
    var allowed = allowedRaw.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    var controller = _rows.FirstOrDefault(r =>
      string.Equals(r.Descriptor.Key, otherKey, StringComparison.OrdinalIgnoreCase));
    if (controller == null) return true; // unknown controller — fail-open

    // If the controller itself is hidden, hide the dependent too.
    if (controller.Label.Visibility == Visibility.Collapsed)
      return false;

    var current = ReadControlValue(controller.Control);
    foreach (var v in allowed) {
      if (string.Equals(current, v, StringComparison.OrdinalIgnoreCase))
        return true;
    }
    return false;
  }

  private static void SetRowVisible(RowState row, bool visible) {
    var v = visible ? Visibility.Visible : Visibility.Collapsed;
    row.Label.Visibility = v;
    row.Control.Visibility = v;
  }

  private void OnOk(object sender, RoutedEventArgs e) {
    var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    foreach (var row in _rows) {
      if (row.Label.Visibility == Visibility.Collapsed) continue; // skip dependency-hidden rows
      dict[row.Descriptor.Key] = ReadControlValue(row.Control);
    }
    Result = dict;
    DialogResult = true;
    Close();
  }

  private void OnCancel(object sender, RoutedEventArgs e) {
    DialogResult = false;
    Close();
  }

  private sealed record RowState(FormatOptionDescriptor Descriptor, TextBlock Label, FrameworkElement Control);
}
