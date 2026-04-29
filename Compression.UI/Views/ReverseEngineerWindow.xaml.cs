using System.Windows;

namespace Compression.UI.Views;

public partial class ReverseEngineerWindow : Window {
  public ReverseEngineerWindow() {
    InitializeComponent();
    Loaded += (_, _) => {
      if (DataContext is ViewModels.ReverseEngineerWizardViewModel vm)
        vm.RequestClose += (_, _) => Close();
    };
  }
}
