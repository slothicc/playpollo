using System.Windows;
using System.Windows.Controls;

namespace PlayPollo.Settings
{
    public partial class PlayPolloSettingsView : UserControl
    {
        public PlayPolloSettingsView()
        {
            InitializeComponent();
            Loaded += (sender, e) => ShowSavedPassword();
            DataContextChanged += (sender, e) => ShowSavedPassword();
        }

        // PasswordBox can't be data-bound, so copy the value across by hand.
        private void ShowSavedPassword()
        {
            if (DataContext is PlayPolloSettingsViewModel vm && PasswordInput.Password != vm.Settings.Password)
            {
                PasswordInput.Password = vm.Settings.Password;
            }
        }

        private void PasswordInput_PasswordChanged(object sender, RoutedEventArgs e)
        {
            if (DataContext is PlayPolloSettingsViewModel vm && vm.Settings.Password != PasswordInput.Password)
            {
                vm.Settings.Password = PasswordInput.Password;
            }
        }
    }
}
