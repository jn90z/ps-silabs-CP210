using System;
using System.Windows;
using SPPS2001D_Pro.ViewModels;

namespace SPPS2001D_Pro
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
            DataContext = new MainViewModel();
        }

        private void ComPortsCombo_DropDownOpened(object sender, EventArgs e)
        {
            if (DataContext is MainViewModel vm)
            {
                _ = vm.Serial.RefreshPortsAsync();
            }
        }

        private void TextBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {

        }
    }
}
