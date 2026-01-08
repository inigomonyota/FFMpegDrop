using System.Diagnostics;
using System.Windows;
using System.Windows.Navigation;

namespace FfmpegDrop;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();

        // This ensures the About window respects the current theme
        this.Resources.MergedDictionaries.Add(Application.Current.Resources.MergedDictionaries[0]);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        // Open the URL in the default browser
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }
}