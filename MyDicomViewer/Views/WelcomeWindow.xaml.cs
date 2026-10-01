using System.Windows;

namespace MyDicomViewer.Views;

/// <summary>
/// 初回起動時の案内と、利用上の注意への同意。
/// requireAcceptance=false（ヘルプメニューから開いた場合）は閲覧のみ。
/// </summary>
public partial class WelcomeWindow : Window
{
    private readonly bool _requireAcceptance;

    public WelcomeWindow(bool requireAcceptance)
    {
        InitializeComponent();
        _requireAcceptance = requireAcceptance;

        if (!requireAcceptance)
        {
            AcceptCheck.Visibility = Visibility.Collapsed;
            DeclineButton.Visibility = Visibility.Collapsed;
            StartButton.Content = "閉じる";
            StartButton.IsEnabled = true;
        }
    }

    private void AcceptCheck_Changed(object sender, RoutedEventArgs e) =>
        StartButton.IsEnabled = AcceptCheck.IsChecked == true;

    private void StartButton_Click(object sender, RoutedEventArgs e)
    {
        if (_requireAcceptance && AcceptCheck.IsChecked != true) return;
        DialogResult = true;
    }

    private void DeclineButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
