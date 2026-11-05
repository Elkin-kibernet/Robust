using System.Windows;

namespace RobloxWorker.Launcher;

public partial class ContractWarningWindow : Window
{
    /// <summary>
    /// Отметил ли пользователь галочку «Больше не показывать эту памятку».
    /// </summary>
    public bool DontShowAgain => DontShowAgainCheckBox.IsChecked == true;

    public ContractWarningWindow(
        int slotsCount,
        string actionTitle,
        bool currentHidePreference,
        bool isInfoOnlyMode = false)
    {
        InitializeComponent();

        DontShowAgainCheckBox.IsChecked = currentHidePreference;

        HeaderTitleText.Text = $"ℹ️ {actionTitle.ToUpperInvariant()} (ОКОН: {slotsCount})";
        SubHeaderText.Text =
            $"К запуску выбрано аккаунтов: {slotsCount}. Ознакомьтесь с правилами сохранения выплаты и тайм-аута.";

        if (isInfoOnlyMode)
        {
            CancelBtn.Visibility = Visibility.Collapsed;
            ConfirmBtn.Content = "Сохранить настройку и закрыть";
        }
    }

    private void ConfirmBtn_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void CancelBtn_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}