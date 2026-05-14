using System.ComponentModel;
using Lookout.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace Lookout.Views;

public sealed partial class ChatView : UserControl
{
    public ConversationViewModel ViewModel { get; }

    public ChatView()
    {
        ViewModel = new ConversationViewModel();
        InitializeComponent();

        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        ViewModel.Messages.CollectionChanged += (_, _) => ScrollToLatest();

        SyncApiKeyBar();
        SyncStatusLine();
        SyncBusyState();
    }

    private void OnSaveKeyClick(object sender, RoutedEventArgs e)
    {
        var key = ApiKeyBox.Password?.Trim();
        if (string.IsNullOrEmpty(key))
            return;
        ViewModel.SaveApiKey(key);
        ApiKeyBox.Password = string.Empty;
    }

    private void OnInputKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter)
            return;

        e.Handled = true;
        if (ViewModel.SendCommand.CanExecute(null))
            ViewModel.SendCommand.Execute(null);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ConversationViewModel.NeedsApiKey):
                SyncApiKeyBar();
                break;
            case nameof(ConversationViewModel.StatusMessage):
                SyncStatusLine();
                break;
            case nameof(ConversationViewModel.IsBusy):
                SyncBusyState();
                break;
        }
    }

    private void SyncApiKeyBar()
        => ApiKeyBar.Visibility = ViewModel.NeedsApiKey ? Visibility.Visible : Visibility.Collapsed;

    private void SyncStatusLine()
    {
        StatusLine.Text = ViewModel.StatusMessage ?? string.Empty;
        StatusLine.Visibility = string.IsNullOrEmpty(ViewModel.StatusMessage)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void SyncBusyState()
    {
        StopButton.Visibility = ViewModel.IsBusy ? Visibility.Visible : Visibility.Collapsed;
        SendButton.Visibility = ViewModel.IsBusy ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ScrollToLatest()
    {
        if (ViewModel.Messages.Count > 0)
            MessageList.ScrollIntoView(ViewModel.Messages[^1]);
    }
}
