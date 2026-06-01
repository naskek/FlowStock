using System.Windows;
using System.Windows.Controls;
using FlowStock.Core.Models;

namespace FlowStock.App;

public partial class ReadyHuBindingWindow : Window
{
    private readonly AppServices _services;
    private readonly long _orderId;
    private OrderScopedHuBindingSession? _session;
    private ReadyHuBindingCandidateItem? _selectedCandidate;
    private ReadyHuBindingLineItem? _selectedLine;
    private ReadyHuBindingHuItem? _selectedHu;

    public ReadyHuBindingWindow(AppServices services, long orderId)
    {
        _services = services;
        _orderId = orderId;
        InitializeComponent();
    }

    private void ReadyHuBindingWindow_Loaded(object sender, RoutedEventArgs e)
    {
        LoadSession();
    }

    private void LoadSession()
    {
        if (!_services.WpfReadApi.TryGetOrder(_orderId, out var order) || order == null)
        {
            MessageBox.Show("Заказ не найден.", "Привязка HU", MessageBoxButton.OK, MessageBoxImage.Warning);
            DialogResult = false;
            Close();
            return;
        }

        if (order.Type != OrderType.Customer)
        {
            MessageBox.Show("Привязка HU доступна только для клиентского заказа.", "Привязка HU", MessageBoxButton.OK, MessageBoxImage.Information);
            DialogResult = false;
            Close();
            return;
        }

        if (!_services.WpfReadApi.TryGetOrderLines(_orderId, out var orderLines))
        {
            MessageBox.Show("Не удалось загрузить строки заказа.", "Привязка HU", MessageBoxButton.OK, MessageBoxImage.Warning);
            DialogResult = false;
            Close();
            return;
        }

        var planLines = _services.DataStore.GetOrderReceiptPlanLines(_orderId);
        _session = new OrderScopedHuBindingSession(order, orderLines, planLines);
        DataContext = _session;
        Title = $"Привязка HU к заказу {order.OrderRef}";
        StatusText.Text = string.Empty;

        var requestLines = _session.BuildCandidatesRequestLines();
        if (requestLines.Count == 0)
        {
            StatusText.Text = "В заказе нет строк, доступных для привязки HU.";
            return;
        }

        if (!_services.WpfReadApi.TryGetHuReservationCandidates(
                _orderId,
                requestLines,
                Array.Empty<string>(),
                out var candidates))
        {
            StatusText.Text = "Не удалось загрузить свободные HU. Уже привязанные HU доступны для отвязки.";
            return;
        }

        _session.ApplyCandidates(candidates);
    }

    private void CandidatesTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        _selectedCandidate = e.NewValue as ReadyHuBindingCandidateItem;
    }

    private void OrderTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        _selectedHu = e.NewValue as ReadyHuBindingHuItem;
        _selectedLine = e.NewValue switch
        {
            ReadyHuBindingLineItem line => line,
            ReadyHuBindingHuItem hu => hu.Line,
            _ => _selectedLine
        };
    }

    private void Bind_Click(object sender, RoutedEventArgs e)
    {
        if (_session == null)
        {
            return;
        }

        CaptureTreeState();
        var movedHuCode = _selectedCandidate?.HuCode;
        var targetLineId = _selectedLine?.OrderLineId;

        if (!_session.StageBind(_selectedCandidate, _selectedLine, out var message))
        {
            MessageBox.Show(message, "Привязка HU", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _session.ExpandOrderRoot();
        if (targetLineId.HasValue)
        {
            _session.ExpandOrderLine(targetLineId.Value);
        }

        RestoreTreeState(selectCandidateHuCode: null, selectLineId: targetLineId, selectHuCode: movedHuCode);
        StatusText.Text = "Изменения подготовлены. База будет изменена после сохранения.";
    }

    private void Detach_Click(object sender, RoutedEventArgs e)
    {
        if (_session == null)
        {
            return;
        }

        CaptureTreeState();
        var movedHuCode = _selectedHu?.HuCode;
        var itemId = _selectedHu?.Line.ItemId;

        if (!_session.StageDetach(_selectedHu, out var message))
        {
            MessageBox.Show(message, "Привязка HU", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (itemId.HasValue)
        {
            _session.ExpandCandidateGroup(itemId.Value);
        }

        RestoreTreeState(selectCandidateHuCode: movedHuCode, selectLineId: _selectedLine?.OrderLineId, selectHuCode: null);
        StatusText.Text = "Изменения подготовлены. База будет изменена после сохранения.";
    }

    private void Auto_Click(object sender, RoutedEventArgs e)
    {
        if (_session == null)
        {
            return;
        }

        CaptureTreeState();
        _session.StageAuto();
        RestoreTreeState();
        StatusText.Text = "Авто-подбор подготовлен. База будет изменена после сохранения.";
    }

    private void CaptureTreeState()
    {
        if (_session == null)
        {
            return;
        }

        _session.CaptureUiState(
            CollectExpandedKeys(CandidatesTree),
            CollectExpandedKeys(OrderTree),
            _selectedCandidate?.HuCode,
            _selectedLine?.OrderLineId,
            _selectedHu?.HuCode);
    }

    private void RestoreTreeState(
        string? selectCandidateHuCode = null,
        long? selectLineId = null,
        string? selectHuCode = null)
    {
        if (_session == null)
        {
            return;
        }

        CandidatesTree.UpdateLayout();
        OrderTree.UpdateLayout();
        RestoreExpandedKeys(CandidatesTree, _session.ExpandedCandidateGroupKeys);
        RestoreExpandedKeys(OrderTree, _session.ExpandedOrderGroupKeys);

        _selectedCandidate = _session.FindCandidate(selectCandidateHuCode ?? _session.SelectedCandidateHuCode);
        _selectedLine = _session.FindLine(selectLineId ?? _session.SelectedLineId) ?? _selectedLine;
        _selectedHu = _session.FindFutureHu(selectHuCode ?? _session.SelectedHuCode);

        SelectTreeItem(CandidatesTree, _selectedCandidate);
        SelectTreeItem(OrderTree, _selectedHu ?? (object?)_selectedLine);
    }

    private static IReadOnlyList<string> CollectExpandedKeys(ItemsControl root)
    {
        var keys = new List<string>();
        CollectExpandedKeys(root, keys);
        return keys;
    }

    private static void CollectExpandedKeys(ItemsControl parent, ICollection<string> keys)
    {
        foreach (var item in parent.Items)
        {
            if (parent.ItemContainerGenerator.ContainerFromItem(item) is not TreeViewItem container)
            {
                continue;
            }

            var key = ResolveExpandKey(item);
            if (container.IsExpanded && !string.IsNullOrWhiteSpace(key))
            {
                keys.Add(key);
            }

            CollectExpandedKeys(container, keys);
        }
    }

    private static void RestoreExpandedKeys(ItemsControl root, IReadOnlySet<string> keys)
    {
        foreach (var item in root.Items)
        {
            if (root.ItemContainerGenerator.ContainerFromItem(item) is not TreeViewItem container)
            {
                continue;
            }

            var key = ResolveExpandKey(item);
            if (!string.IsNullOrWhiteSpace(key) && keys.Contains(key))
            {
                container.IsExpanded = true;
                container.UpdateLayout();
            }

            RestoreExpandedKeys(container, keys);
        }
    }

    private static bool SelectTreeItem(ItemsControl root, object? target)
    {
        if (target == null)
        {
            return false;
        }

        foreach (var item in root.Items)
        {
            if (root.ItemContainerGenerator.ContainerFromItem(item) is not TreeViewItem container)
            {
                continue;
            }

            if (ReferenceEquals(item, target))
            {
                container.IsSelected = true;
                container.BringIntoView();
                return true;
            }

            container.IsExpanded = container.IsExpanded || ContainsTarget(container, target);
            container.UpdateLayout();
            if (SelectTreeItem(container, target))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsTarget(ItemsControl root, object target)
    {
        foreach (var item in root.Items)
        {
            if (ReferenceEquals(item, target))
            {
                return true;
            }

            if (root.ItemContainerGenerator.ContainerFromItem(item) is ItemsControl child && ContainsTarget(child, target))
            {
                return true;
            }
        }

        return false;
    }

    private static string? ResolveExpandKey(object? item) =>
        item switch
        {
            ReadyHuBindingCandidateGroup group => group.ExpandKey,
            ReadyHuBindingOrderGroup group => group.ExpandKey,
            ReadyHuBindingLineItem line => line.ExpandKey,
            _ => null
        };

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_session == null)
        {
            return;
        }

        var lines = _session.BuildApplyFinalLines();
        if (lines.Count == 0)
        {
            DialogResult = true;
            Close();
            return;
        }

        SaveButton.IsEnabled = false;
        try
        {
            if (!_services.WpfReadApi.TryApplyFinalHuBindings(_orderId, lines, out _, out var error))
            {
                MessageBox.Show(
                    BuildApplyFinalErrorMessage(error),
                    "Привязка HU",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            DialogResult = true;
            Close();
        }
        finally
        {
            SaveButton.IsEnabled = true;
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private static string BuildApplyFinalErrorMessage(WpfHuBindingApplyFinalError? error)
    {
        if (error == null)
        {
            return "Сервер отклонил привязку HU. Проверьте выбор HU и повторите действие.";
        }

        if (string.Equals(error.ErrorCode, "HU_BINDING_STALE", StringComparison.OrdinalIgnoreCase))
        {
            return "Список HU изменился. Обновите заказ и повторите действие.";
        }

        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(error.Message))
        {
            parts.Add(error.Message);
        }
        else if (!string.IsNullOrWhiteSpace(error.ErrorCode))
        {
            parts.Add(error.ErrorCode);
        }

        if (error.Problems.Count > 0)
        {
            parts.Add(string.Join(Environment.NewLine, error.Problems));
        }

        return parts.Count == 0
            ? "Сервер отклонил привязку HU."
            : string.Join(Environment.NewLine + Environment.NewLine, parts);
    }
}
