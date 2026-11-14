// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using FluentFlyout.Classes;
using FluentFlyout.Classes.Settings;
using FluentFlyoutWPF.Classes.Utils;
using FluentFlyoutWPF.Pages;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Wpf.Ui.Controls;

namespace FluentFlyoutWPF;

public partial class SettingsWindow : FluentWindow
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

    private static SettingsWindow? instance;
    private Type? _currentPageType;
    private Type? _initialPageType;
    private List<SearchItem> _allSearchItems = [];
    private string? _pendingHighlightElementId = null;
    static readonly Regex SplitCamelCaseRegex = new(@"(?<=[a-z0-9])(?=[A-Z])", RegexOptions.Compiled);

    private SettingsWindow()
    {
        InitializeComponent();
        instance = this;

        var settings = SettingsManager.Current;

        Width = settings.SettingsWindowWidth >= MinWidth ? settings.SettingsWindowWidth : 900;
        Height = settings.SettingsWindowHeight >= MinHeight ? settings.SettingsWindowHeight : 700;

        if (!double.IsNaN(settings.SettingsWindowLeft) && !double.IsNaN(settings.SettingsWindowTop))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            var workArea = MonitorUtil.GetWorkAreaDips(settings.SettingsWindowLeft, settings.SettingsWindowTop);

            Width = Math.Max(MinWidth, Math.Min(Width, workArea.Width));
            Height = Math.Max(MinHeight, Math.Min(Height, workArea.Height));

            Left = Math.Max(workArea.Left, Math.Min(settings.SettingsWindowLeft, workArea.Right - Width));
            Top = Math.Max(workArea.Top, Math.Min(settings.SettingsWindowTop, workArea.Bottom - Height));
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        if (settings.SettingsWindowState == WindowState.Maximized)
        {
            WindowState = WindowState.Maximized;
        }

        DataContext = SettingsManager.Current;

        RootNavigation.SetCurrentValue(NavigationView.IsPaneOpenProperty, false);
    }

    public static void ShowInstance(string? navigationPage = null)
    {
        Type? targetPageType = navigationPage != null
            ? System.Reflection.Assembly
                .GetExecutingAssembly()
                .GetType($"FluentFlyoutWPF.Pages.{navigationPage}")
            : null;

        if (instance == null)
        {
            var window = new SettingsWindow
            {
                _initialPageType = targetPageType
            };
            window.Show();
            window.Activate();
            return;
        }

        if (instance.WindowState == WindowState.Minimized)
        {
            instance.WindowState = WindowState.Normal;
        }

        instance.Activate();
        instance.Focus();

        if (targetPageType != null)
        {
            NavigateToPage(targetPageType);
        }
    }

    private void SearchBox_SuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        if (args.SelectedItem is SearchItem selectedItem)
        {
            if (selectedItem.TargetPageType != null)
            {
                if (_currentPageType != selectedItem.TargetPageType)
                {
                    _pendingHighlightElementId = selectedItem.TargetElementId;
                    RootNavigation.Navigate(selectedItem.TargetPageType);
                }
                else if (!string.IsNullOrEmpty(selectedItem.TargetElementId))
                {
                    // Already on the page, just scroll and highlight
                    ScrollToAndHighlight(selectedItem.TargetElementId);
                }
            }
        }
    }

    private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
        {
            var query = (sender.Text ?? string.Empty).ToLowerInvariant();
            if (string.IsNullOrEmpty(query))
            {
                sender.ItemsSource = _allSearchItems;
                sender.IsSuggestionListOpen = false;
                return;
            }
            var matches = _allSearchItems.Where(x => x.Title.ToLowerInvariant().Contains(query)).ToList();
            sender.ItemsSource = matches;
            sender.IsSuggestionListOpen = matches.Count > 0;
        }
    }

    private void FluentWindow_PreviewMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (SearchBox.IsKeyboardFocusWithin && !SearchBox.IsMouseOver)
        {
            SearchBox.IsSuggestionListOpen = false;
            // Move focus to the window to unfocus the search box
            this.Focus();
        }
    }

    public static void NavigateToPage(Type pageType)
    {
        if (instance == null) return;
        if (!instance.IsLoaded)
        {
            instance._initialPageType = pageType;
            return;
        }
        instance.RootNavigation.Navigate(pageType);
    }

    private void BuildSearchItems()
    {
        var items = new List<SearchItem>();

        // Add all tabs
        foreach (var navItem in RootNavigation.MenuItems.OfType<NavigationViewItem>().Concat(RootNavigation.FooterMenuItems.OfType<NavigationViewItem>()))
        {
            if (navItem.Content != null)
            {
                items.Add(new SearchItem { Title = navItem.Content.ToString()!, TargetPageType = navItem.TargetPageType });
            }
        }

        // Add specific settings deep links from auto-generated static array
        foreach (var item in SearchItems)
        {
            string title = Application.Current?.TryFindResource(item.ResourceKey)?.ToString() ?? item.ResourceKey;
            // Clean up the page type name (e.g. "SystemPage" -> "System") and split camel case (e.g. "MediaFlyout" -> "Media Flyout")
            string pageName = SplitCamelCaseRegex.Replace(item.TargetPageType.Name.Replace("Page", ""), " ");
            items.Add(new SearchItem { Title = $"{title}", Subtitle = pageName, TargetPageType = item.TargetPageType, TargetElementId = item.TargetElementId });
        }

        _allSearchItems = items;
        SearchBox.OriginalItemsSource = _allSearchItems;
    }

    private void ScrollToAndHighlight(string elementId)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                var targetElement = FindChildByName<FrameworkElement>(RootNavigation, elementId);
                if (targetElement != null)
                {
                    targetElement.BringIntoView();

                    // Heartbeat animation
                    var heartbeatAnimation = new System.Windows.Media.Animation.DoubleAnimation
                    {
                        From = 1.0,
                        To = 0.5,
                        Duration = new Duration(TimeSpan.FromMilliseconds(300)),
                        AutoReverse = true,
                        RepeatBehavior = new System.Windows.Media.Animation.RepeatBehavior(2)
                    };
                    targetElement.BeginAnimation(UIElement.OpacityProperty, heartbeatAnimation);
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Error scrolling to and highlighting element");
            }
        }), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private async void SettingsWindow_Loaded(object sender, RoutedEventArgs e)
    {
        RootNavigation.IsPaneOpen = false;

        _currentPageType = _initialPageType ?? typeof(HomePage);
        _initialPageType = null;
        RootNavigation.Navigate(_currentPageType);

        // wrkaround for WPF-UI NavigationView theme change bug:
        // force pane initialization by toggling it once to prevent width corruption on theme changes
        // not sure why this has to be done
        await Task.Delay(100);
        if (!IsLoaded) return;
        RootNavigation.IsPaneOpen = true;
        await Task.Delay(10);
        if (!IsLoaded) return;
        RootNavigation.IsPaneOpen = false;

        LicenseManager.GetPremiumProductInfo();

        RootNavigation.Navigated -= RootNavigation_Navigated;
        RootNavigation.Navigated += RootNavigation_Navigated;

        SettingsManager.Current.PropertyChanged -= Settings_PropertyChanged;
        SettingsManager.Current.PropertyChanged += Settings_PropertyChanged;

        BuildSearchItems();
    }

    private async void RootNavigation_Navigated(NavigationView sender, NavigatedEventArgs args)
    {
        _currentPageType = args.Page?.GetType();
        ResetScrollPosition(args.Page as DependencyObject);
        if (!string.IsNullOrEmpty(_pendingHighlightElementId))
        {
            var elementId = _pendingHighlightElementId;
            _pendingHighlightElementId = null;
            // Add a slight delay to ensure page is fully rendered before finding child and scrolling
            await Task.Delay(300);
            if (IsLoaded)
            {
                ScrollToAndHighlight(elementId);
            }
        }
    }

    private async void Settings_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        try
        {
            if (e.PropertyName == nameof(SettingsManager.Current.AppTheme))
            {
                // force fix pane state after theme change
                await await Dispatcher.InvokeAsync(async () =>
                {
                    if (!IsLoaded) return;
                    var wasPaneOpen = RootNavigation.IsPaneOpen;

                    await Task.Delay(100);
                    if (!IsLoaded) return;
                    RootNavigation.IsPaneOpen = !wasPaneOpen;
                    await Task.Delay(10);
                    if (!IsLoaded) return;
                    RootNavigation.IsPaneOpen = wasPaneOpen;

                    await Task.Delay(300);
                    if (!IsLoaded) return;
                    RootNavigation.Navigate(typeof(HomePage));

                    BuildSearchItems();
                }, System.Windows.Threading.DispatcherPriority.Loaded);
            }
            else if (e.PropertyName == nameof(SettingsManager.Current.AppLanguage))
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    if (!IsLoaded) return;
                    BuildSearchItems();
                }, System.Windows.Threading.DispatcherPriority.Loaded);
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Error handling settings change in SettingsWindow");
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        SettingsManager.Current.PropertyChanged -= Settings_PropertyChanged;
        RootNavigation.Navigated -= RootNavigation_Navigated;
        if (ReferenceEquals(instance, this))
        {
            instance = null;
        }
        base.OnClosed(e);
    }


    private void SettingsWindow_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        var settings = SettingsManager.Current;
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, ActualWidth, ActualHeight) : RestoreBounds;

        if (bounds.Width >= MinWidth && bounds.Height >= MinHeight)
        {
            settings.SettingsWindowWidth = bounds.Width;
            settings.SettingsWindowHeight = bounds.Height;
            settings.SettingsWindowLeft = bounds.Left;
            settings.SettingsWindowTop = bounds.Top;
            settings.SettingsWindowState = WindowState == WindowState.Maximized ? WindowState.Maximized : WindowState.Normal;
        }

        SettingsManager.SaveSettings();
    }

    private void ResetScrollPosition(DependencyObject? navigatedPage)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                var scrollViewer = navigatedPage != null
                    ? FindAncestorScrollViewer(navigatedPage, RootNavigation)
                    : null;
                scrollViewer ??= FindScrollableScrollViewer(RootNavigation);
                scrollViewer?.ScrollToVerticalOffset(0);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Error resetting scroll position in SettingsWindow");
            }
        }), System.Windows.Threading.DispatcherPriority.Loaded);
    }


    // helper functions to traverse visual tree

    private static ScrollViewer? FindAncestorScrollViewer(DependencyObject child, DependencyObject stopAt)
    {
        var current = VisualTreeHelper.GetParent(child);
        while (current != null && !ReferenceEquals(current, stopAt))
        {
            if (current is ScrollViewer sv)
            {
                return sv;
            }
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    private static T? FindChildByName<T>(DependencyObject parent, string name) where T : FrameworkElement
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typedChild && typedChild.Name == name)
            {
                return typedChild;
            }

            var result = FindChildByName<T>(child, name);
            if (result != null)
            {
                return result;
            }
        }
        return null;
    }

    private static ScrollViewer? FindScrollableScrollViewer(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is ScrollViewer sv && sv.ScrollableHeight > 0)
            {
                return sv;
            }

            var result = FindScrollableScrollViewer(child);
            if (result != null)
            {
                return result;
            }
        }
        return null;
    }

    public class SearchItem
    {
        public string Title { get; set; } = string.Empty;
        public string? Subtitle { get; set; }
        public Type? TargetPageType { get; set; }
        public string? TargetElementId { get; set; }
        public override string ToString() => Title;
    }
}