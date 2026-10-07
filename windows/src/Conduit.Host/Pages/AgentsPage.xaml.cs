using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Windows.Storage.Pickers;

namespace Conduit.Host.Pages;

public sealed partial class AgentsPage : Page
{
    private bool _loading;

    public AgentsPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _loading = true;
        DemoToggle.IsOn = App.Runtime.Config.EnableEchoAgent;
        _loading = false;
        Refresh();
    }

    private void Refresh() => AgentList.ItemsSource = App.Runtime.Agents.List().Select(AgentItem.From).ToList();

    private async void Rescan_Click(object sender, RoutedEventArgs e)
    {
        RescanButton.IsEnabled = false;
        await App.Runtime.Agents.RefreshAsync();
        Refresh();
        RescanButton.IsEnabled = true;
    }

    private async void Browse_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not string id) return;
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder };
        picker.FileTypeFilter.Add(".exe");
        picker.FileTypeFilter.Add(".cmd");
        picker.FileTypeFilter.Add(".bat");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.Window));
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;
        App.Runtime.Update(c => c.AgentPaths[id] = file.Path);
        await App.Runtime.Agents.RefreshAsync();
        Refresh();
    }

    private void Demo_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        App.Runtime.Update(c => c.EnableEchoAgent = DemoToggle.IsOn);
        Refresh();
    }
}
