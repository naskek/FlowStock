using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using FlowStock.App;

namespace FlowStock.Server.Tests.Wpf;

public sealed class UiPreviewTests
{
    [Theory]
    [InlineData("--ui-preview", true)]
    [InlineData("--ui-preview=true", false)]
    [InlineData("--dev", false)]
    [InlineData("--UI-PREVIEW", false)]
    [InlineData("", false)]
    public void Only_exact_explicit_flag_selects_preview(string argument, bool expected)
    {
        var calls = 0;
        Assert.Equal(expected, UiPreviewStartup.TryStart(new[] { argument }, _ => calls++));
        Assert.Equal(expected ? 1 : 0, calls);
    }

    [Fact]
    public async Task Main_window_and_every_settings_page_are_backend_free_and_fail_closed()
    {
        await OnUiThread(() =>
        {
            var context = new UiPreviewContext();
            var main = new MainWindow(context);
            var admin = new AdminWindow(context);
            try
            {
                AssertPreviewController(main);
                AssertPreviewController(admin);
                Assert.Contains("UI Preview / DEV", main.Title);
                Assert.Equal(Visibility.Visible, ((FrameworkElement)main.FindName("UiPreviewBanner")).Visibility);
                Assert.True(((MenuItem)main.FindName("OpenSettingsMenuItem")).IsEnabled);
                Assert.True(((MenuItem)main.FindName("OpenHuRegistryMenuItem")).IsEnabled);
                main.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                typeof(MainWindow).GetMethod("OnContentRendered", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(main, new object[] { EventArgs.Empty });

                foreach (var gridName in new[]
                {
                    "WarehouseProductionStateGrid", "DocsGrid", "OrdersGrid", "StatisticsMonthlyGrid",
                    "StatisticsGroupsGrid", "ItemsGrid", "LocationsGrid", "PartnersGrid"
                })
                {
                    var grid = (DataGrid)main.FindName(gridName);
                    Assert.True(grid.Items.Count >= 12, gridName + " should have demo records");
                }
                Assert.Contains(Descendants(main).OfType<GroupBox>(),
                    group => Equals(group.Header, "Период"));
                Assert.Contains(Descendants(main).OfType<GroupBox>(),
                    group => Equals(group.Header, "Отображение и действия"));

                var tabs = (TabControl)main.FindName("MainTabs");
                for (var index = 0; index < tabs.Items.Count; index++)
                {
                    tabs.SelectedIndex = index;
                    AssertActionsDisabled(main);
                }

                var tree = (TreeView)admin.FindName("AdminNavigationTree");
                var categories = Descendants(tree).OfType<TreeViewItem>().Where(item => item.Tag is string).ToArray();
                Assert.Equal(15, categories.Length);
                Assert.DoesNotContain(categories, category => Equals(category.Tag, "update"));
                Assert.DoesNotContain(categories, category => Equals(category.Tag, "maintenance"));

                var systemPanel = (ScrollViewer)admin.FindName("SystemCategoryPanel");
                Assert.Contains(Descendants(systemPanel).OfType<GroupBox>(),
                    group => Equals(group.Header, "Обновление FlowStock"));
                Assert.Contains(Descendants(systemPanel).OfType<Button>(),
                    button => Equals(button.Content, "Проверить"));
                Assert.Contains(Descendants(systemPanel).OfType<Button>(),
                    button => Equals(button.Content, "Обновить и перезапустить"));
                foreach (var category in categories)
                {
                    category.IsSelected = true;
                    AssertActionsDisabled(admin);
                }

                var cache = (System.Collections.IDictionary)typeof(AdminWindow)
                    .GetField("_embeddedPages", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(admin)!;
                Assert.Equal(12, cache.Count);
                foreach (var hosted in cache.Values)
                {
                    var controller = hosted!.GetType().GetProperty("Controller")!.GetValue(hosted)!;
                    var content = (FrameworkElement)hosted.GetType().GetProperty("Content")!.GetValue(hosted)!;
                    AssertPreviewController(controller);
                    // Exercise Loaded/re-entry and programmatic Click despite disabled actions.
                    content.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                    content.RaiseEvent(new RoutedEventArgs(FrameworkElement.UnloadedEvent));
                    content.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                    AssertActionsDisabled(content);
                }

                Assert.Null(typeof(MainWindow).GetField("_liveRefreshSubscription", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main));
                Assert.Null(typeof(MainWindow).GetField("_itemRequestsBadgeRefreshTimer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main));
                Assert.Null(typeof(MainWindow).GetField("_commercialStatisticsRefreshTimer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main));
                Assert.Null(typeof(AdminWindow).GetField("_updateCheckCancellation", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(admin));
            }
            finally
            {
                admin.Close();
                main.Close();
            }
        });
    }


    [Fact]
    public async Task Account_preview_shows_demo_rows_and_all_three_backend_free_dialogs()
    {
        await OnUiThread(() =>
        {
            var accounts = new TsdDeviceWindow(new UiPreviewContext());
            try
            {
                Assert.True(accounts.IsUiPreview);
                var grid = (DataGrid)accounts.FindName("DevicesGrid");
                Assert.Equal(6, grid.Items.Count);
                Assert.All(grid.Items.OfType<TsdDeviceInfo>(), row =>
                    Assert.StartsWith("DEMO-DEVICE-", row.DeviceId));
                Assert.True(grid.IsReadOnly);
                Assert.Null(typeof(TsdDeviceWindow)
                    .GetField("_productionServices", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .GetValue(accounts));
                var account = Assert.IsType<TsdDeviceInfo>(grid.SelectedItem);

                foreach (var mode in new[]
                {
                    AccountDialogMode.Create, AccountDialogMode.Edit, AccountDialogMode.ChangePassword
                })
                {
                    var dialog = new TsdDeviceEditorWindow(
                        mode, mode == AccountDialogMode.Create ? null : account, null);
                    try
                    {
                        Assert.True(dialog.IsUiPreview);
                        Assert.Contains("UI Preview / DEV", dialog.Title);
                        Assert.False(((Button)dialog.FindName("SaveButton")).IsEnabled);
                        Assert.Empty(((PasswordBox)dialog.FindName("NewPasswordBox")).Password);
                        Assert.Empty(((PasswordBox)dialog.FindName("ConfirmPasswordBox")).Password);
                        var profile = (StackPanel)dialog.FindName("ProfileFieldsPanel");
                        var password = (StackPanel)dialog.FindName("PasswordFieldsPanel");
                        Assert.Equal(mode == AccountDialogMode.ChangePassword
                            ? Visibility.Collapsed : Visibility.Visible, profile.Visibility);
                        Assert.Equal(mode == AccountDialogMode.Edit
                            ? Visibility.Collapsed : Visibility.Visible, password.Visibility);
                        Assert.Equal(mode == AccountDialogMode.Create
                            ? string.Empty : account.Login,
                            ((TextBox)dialog.FindName("LoginBox")).Text);
                    }
                    finally { dialog.Close(); }
                }
            }
            finally { accounts.Close(); }
        });
    }

    [Fact]
    public async Task Account_modal_validation_does_not_submit_incomplete_credentials()
    {
        await OnUiThread(() =>
        {
            var calls = 0;
            var dialog = new TsdDeviceEditorWindow(
                AccountDialogMode.Create, null,
                _ => { calls++; return Task.CompletedTask; });
            try
            {
                ((TextBox)dialog.FindName("LoginBox")).Text = "test_login";
                var save = (Button)dialog.FindName("SaveButton");
                save.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Assert.Equal(0, calls);
                Assert.Equal(Visibility.Visible,
                    ((TextBlock)dialog.FindName("ErrorText")).Visibility);

                ((PasswordBox)dialog.FindName("NewPasswordBox")).Password = "test-secret";
                ((PasswordBox)dialog.FindName("ConfirmPasswordBox")).Password = "other-secret";
                save.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Assert.Equal(0, calls);
                Assert.Equal(Visibility.Visible,
                    ((TextBlock)dialog.FindName("ErrorText")).Visibility);
            }
            finally { dialog.Close(); }
        });
    }

    [Fact]
    public async Task Hu_registry_preview_is_populated_and_safe_without_services()
    {
        await OnUiThread(() =>
        {
            var hu = new HuRegistryWindow(new UiPreviewContext());
            try
            {
                Assert.True(hu.IsUiPreview);
                var registry = (DataGrid)hu.FindName("RegistryGrid");
                var composition = (DataGrid)hu.FindName("CompositionGrid");
                var state = (ComboBox)hu.FindName("StateFilter");
                Assert.Equal(24, registry.Items.Count);
                registry.SelectedIndex = 0;
                Assert.NotEmpty(composition.Items);
                state.SelectedIndex = 2; // ACTIVE
                Assert.Equal(6, registry.Items.Count);
                Assert.All(Descendants(hu).OfType<Button>(), button =>
                {
                    if (button.Content as string != "Закрыть")
                        Assert.False(button.IsEnabled);
                });
                Assert.Null(typeof(HuRegistryWindow)
                    .GetField("_liveRefreshSubscription", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .GetValue(hu));
            }
            finally { hu.Close(); }
        });
    }

    private static void AssertPreviewController(object controller)
    {
        Assert.True((bool)controller.GetType().GetProperty("IsUiPreview")!.GetValue(controller)!);
        Assert.Null(controller.GetType().GetField("_productionServices", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(controller));
        var error = Assert.Throws<TargetInvocationException>(() => controller.GetType()
            .GetProperty("_services", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(controller));
        Assert.IsType<InvalidOperationException>(error.InnerException);
        Assert.Equal(UiPreviewContext.OperationUnavailable, error.InnerException!.Message);
    }

    private static void AssertActionsDisabled(DependencyObject root)
    {
        foreach (var element in Descendants(root))
        {
            if (element is Button button && button.Content as string != "Закрыть")
            {
                // Account preview buttons open presentation-only dialogs. They
                // deliberately cannot persist data; don't invoke a modal from
                // this generic event-probing loop.
                if (button.Name is "CreateAccountButton" or "EditAccountButton" or "ChangePasswordButton")
                {
                    Assert.True(button.IsEnabled);
                    continue;
                }
                Assert.False(button.IsEnabled);
                button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Assert.False(button.IsEnabled);
            }
            if (element is DataGrid grid)
            {
                Assert.True(grid.IsReadOnly);
                Assert.NotEmpty(grid.Items);
            }
        }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        foreach (var element in Descendants(child)) yield return element;
    }

    private static async Task OnUiThread(Action action)
    {
        var result = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
                action();
                result.SetResult();
            }
            catch (Exception exception) { result.SetException(exception); }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        try { await result.Task.WaitAsync(TimeSpan.FromSeconds(30)); }
        finally { Assert.True(thread.Join(TimeSpan.FromSeconds(10))); }
    }
}
