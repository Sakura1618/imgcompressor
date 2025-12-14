using System.Diagnostics;
using System.Windows;
using System.Windows.Navigation;

namespace imgcompressor
{
    public partial class AboutWindow : Window
    {
        public AboutWindow()
        {
            InitializeComponent();
        }

        private void OkButton_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }

        private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo { FileName = e.Uri.AbsoluteUri, UseShellExecute = true });
            }
            catch
            {
                MessageBox.Show("无法打开链接。");
            }
            e.Handled = true;
        }
    }
}