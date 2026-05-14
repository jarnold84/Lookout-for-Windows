using System.ComponentModel;
using Lookout.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Lookout.Views;

/// <summary>
/// A single chat bubble. Listens to its bound MessageViewModel so the text
/// updates live while the assistant reply streams in.
/// </summary>
public sealed partial class MessageView : UserControl
{
    private MessageViewModel? _vm;

    public MessageView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Unloaded += (_, _) => Detach();
    }

    private void OnDataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        Detach();
        _vm = args.NewValue as MessageViewModel;
        if (_vm == null)
            return;
        _vm.PropertyChanged += OnVmPropertyChanged;
        Render();
    }

    private void Detach()
    {
        if (_vm != null)
            _vm.PropertyChanged -= OnVmPropertyChanged;
        _vm = null;
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MessageViewModel.Text) or nameof(MessageViewModel.IsStreaming))
            Render();
    }

    private void Render()
    {
        if (_vm == null)
            return;

        RoleLabel.Text = _vm.RoleLabel;
        BodyText.Text = _vm.Text.Length == 0 && _vm.IsStreaming ? "…" : _vm.Text;

        if (_vm.HasScreenshot)
        {
            ScreenshotLabel.Text = "\U0001F4F7 " + _vm.ScreenshotLabel;
            ScreenshotLabel.Visibility = Visibility.Visible;
        }
        else
        {
            ScreenshotLabel.Visibility = Visibility.Collapsed;
        }

        if (_vm.IsUser)
        {
            Bubble.HorizontalAlignment = HorizontalAlignment.Right;
            Bubble.Background = GetBrush("AccentFillColorDefaultBrush", Color.FromArgb(255, 46, 91, 164));
            BodyText.Foreground = new SolidColorBrush(Colors.White);
            RoleLabel.Foreground = new SolidColorBrush(Colors.White);
        }
        else
        {
            Bubble.HorizontalAlignment = HorizontalAlignment.Left;
            Bubble.Background = GetBrush("CardBackgroundFillColorDefaultBrush", Color.FromArgb(255, 240, 240, 240));
            BodyText.ClearValue(TextBlock.ForegroundProperty);
            RoleLabel.ClearValue(TextBlock.ForegroundProperty);
        }
    }

    private static Brush GetBrush(string resourceKey, Color fallback)
    {
        if (Application.Current.Resources.TryGetValue(resourceKey, out var res) && res is Brush brush)
            return brush;
        return new SolidColorBrush(fallback);
    }
}
