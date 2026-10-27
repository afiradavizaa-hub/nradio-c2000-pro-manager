using System.Windows.Controls;
using NRadioManager.ViewModels;

namespace NRadioManager.Views;

public partial class C2000WifiView : UserControl
{
    private C2000WifiViewModel? _viewModel;

    public C2000WifiView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_viewModel is not null) _viewModel.PropertyChanged -= OnViewModelChanged;
            _viewModel = DataContext as C2000WifiViewModel;
            if (_viewModel is not null) _viewModel.PropertyChanged += OnViewModelChanged;
        };
    }

    private void OnPassword24Changed(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is C2000WifiViewModel vm && sender is PasswordBox box)
            vm.Password24Input = box.Password;
    }

    private void OnPassword5Changed(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is C2000WifiViewModel vm && sender is PasswordBox box)
            vm.Password5Input = box.Password;
    }

    private void OnViewModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_viewModel is null) return;
        if (e.PropertyName == nameof(C2000WifiViewModel.Password24Input) &&
            string.IsNullOrEmpty(_viewModel.Password24Input) && Password24Box.Password.Length > 0)
            Password24Box.Clear();
        if (e.PropertyName == nameof(C2000WifiViewModel.Password5Input) &&
            string.IsNullOrEmpty(_viewModel.Password5Input) && Password5Box.Password.Length > 0)
            Password5Box.Clear();
    }
}
