using System;
using System.IO;
using System.Windows;

namespace STM32Bootloader
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            try
            {
                base.OnStartup(e);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Startup Error:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown(1);
            }
        }
        
        public App()
        {
            this.DispatcherUnhandledException += (s, args) =>
            {
                MessageBox.Show($"Error:\n{args.Exception.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                args.Handled = true;
            };
        }
    }
}
