using System.Windows;
using System.Windows.Controls;
using NRadioManager.ViewModels;

namespace NRadioManager.Views;

public partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();

    private void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel vm && sender is PasswordBox box) vm.PasswordText = box.Password;
    }
}
