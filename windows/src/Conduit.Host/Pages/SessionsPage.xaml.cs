using Conduit.Host.Core.Protocol;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Conduit.Host.Pages;

public sealed partial class SessionsPage : Page
{
    private string _sig = "";

    public SessionsPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        App.Window!.RuntimeChanged += Refresh;
        Refresh();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e) => App.Window!.RuntimeChanged -= Refresh;

    private void Refresh()
    {
        var items = App.Runtime.Sessions.All.Select(s => s.Summary())
            .OrderByDescending(s => SessionStates.IsActive((string?)s["state"] ?? ""))
            .ThenByDescending(s => (long?)s["updatedAt"])
            .Select(SessionItem.From).ToList();
        var sig = string.Join(";", items.Select(i => i.Signature));
        if (sig != _sig)
        {
            _sig = sig;
            SessionList.ItemsSource = items;
        }
        Empty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void Stop_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is string id) await App.Runtime.Sessions.StopAsync(id);
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is string id) await App.Runtime.Sessions.DeleteAsync(id);
    }
}
