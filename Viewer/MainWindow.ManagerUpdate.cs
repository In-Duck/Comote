using System.Windows;
using System.Windows.Controls;

namespace Viewer;

public partial class MainWindow
{
    private Button? _managerUpdateButton;

    private async Task CheckManagerUpdateAsync()
    {
        if (_managerUpdateButton == null) return;
        _managerUpdateButton.IsEnabled = false;
        _updateProgressBar.Visibility = Visibility.Visible;
        var progress = new Progress<ManagerUpdateProgress>(value =>
        {
            _statusBarText.Text = value.Status;
            if (value.Percent.HasValue)
            {
                _updateProgressBar.IsIndeterminate = false;
                _updateProgressBar.Value = value.Percent.Value;
            }
            else
            {
                _updateProgressBar.IsIndeterminate = true;
            }
        });

        try
        {
            await ManagerAutoUpdater.CheckAndApplyAsync(
                showCurrentStatus: true,
                progress);
        }
        finally
        {
            _managerUpdateButton.IsEnabled = true;
            _updateProgressBar.IsIndeterminate = false;
            _updateProgressBar.Visibility = Visibility.Collapsed;
        }
    }
}
