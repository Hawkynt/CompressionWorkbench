using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Compression.UI.ViewModels;

/// <summary>
/// Converts a <see cref="ReverseEngineerWizardViewModel.WizardStep"/> enum value to Visibility.
/// The ConverterParameter is the step name to compare against.
/// Visible when the current step matches the parameter, Collapsed otherwise.
/// </summary>
internal sealed class WizardStepToVisibilityConverter : IValueConverter {
  public object Convert(object value, Type targetType, object parameter, CultureInfo culture) {
    if (value is ReverseEngineerWizardViewModel.WizardStep step && parameter is string stepName)
      return step.ToString() == stepName ? Visibility.Visible : Visibility.Collapsed;
    return Visibility.Collapsed;
  }

  public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    => throw new NotSupportedException();
}
