using System.Windows;
using System.Windows.Controls;
using NRadioManager.ViewModels;

namespace NRadioManager.Views;

public partial class SystemView : UserControl
{
    public SystemView() => InitializeComponent();

    private static SystemViewModel? Vm(object sender) => (sender as FrameworkElement)?.DataContext as SystemViewModel;

    private void OnOldPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (Vm(sender) is { } vm && sender is PasswordBox box) vm.OldPassword = box.Password;
    }

    private void OnNewPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (Vm(sender) is { } vm && sender is PasswordBox box) vm.NewPassword = box.Password;
    }

    private void OnConfirmPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (Vm(sender) is { } vm && sender is PasswordBox box) vm.ConfirmPassword = box.Password;
    }
}
