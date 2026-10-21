using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;

namespace VTC.Client.Views;

public partial class NewsView : UserControl
{
    private string _tab = "ets2";

    public NewsView()
    {
        InitializeComponent();
        sourceHint.Text = _tab == "ets2"
            ? "来源：SCS Blog（feedburner）· 欧卡"
            : "来源：SCS Blog（feedburner）· 美卡";
    }


    private void OnTabClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag }) return;
        _tab = tag;
        var accent = App.Current.Resources["vtc_accent"] as IBrush ?? new SolidColorBrush(Color.FromRgb(30, 136, 229));
        tabEts2.Background = tag == "ets2" ? accent : Brushes.Transparent;
        tabEts2.Foreground = tag == "ets2" ? Brushes.White : new SolidColorBrush(Color.FromRgb(51, 51, 51));
        tabAts.Background = tag == "ats" ? accent : Brushes.Transparent;
        tabAts.Foreground = tag == "ats" ? Brushes.White : new SolidColorBrush(Color.FromRgb(51, 51, 51));
        sourceHint.Text = tag == "ets2" ? "来源：SCS Blog（feedburner）· 欧卡" : "来源：SCS Blog（feedburner）· 美卡";
    }

    private void OnRefreshClick(object? sender, RoutedEventArgs e)
    {
        // P1：拉 RSS。P0 占位。
        placeholder.Text += "\n（点击刷新：RSS 抓取为 P1 功能，当前未拉取）";
    }
}
