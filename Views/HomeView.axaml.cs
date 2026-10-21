using Avalonia;
using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using VTC.Client.Services;
using VTC.Client.ViewModels;

namespace VTC.Client.Views;

public partial class HomeView : UserControl
{
    private HomeViewModel _vm = null!;

    public HomeView()
    {
        InitializeComponent();
        var sp = AppServiceLocator.Services;
        _vm = new HomeViewModel(
            sp.GetRequiredService<IApiClient>(),
            sp.GetRequiredService<AuthService>(),
            sp.GetRequiredService<DeployService>(),
            sp.GetRequiredService<UnreadService>(),
            sp.GetRequiredService<UpdateService>(),
            sp.GetRequiredService<AppSettings>());
        DataContext = _vm;
        _ = _vm.LoadAsync();
    }


    private void OnDeployClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: EventCard card })
            _ = _vm.DeployAsync(card);
    }

    private void OnUpdateYes(object? sender, RoutedEventArgs e) => _vm.DownloadUpdateCommand.Execute(null);
    private void OnUpdateNo(object? sender, RoutedEventArgs e) => _vm.DismissUpdateCommand.Execute(null);

    public void Refresh() => _ = _vm.LoadAsync();
}
