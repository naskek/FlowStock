using System;
using System.Windows;
using FlowStock.Core.Models;

namespace FlowStock.App;

public partial class NewDocWindow : Window
{
    private readonly AppServices _services;
    private readonly List<DocTypeOption> _types = new();
    private bool _createInProgress;
    private string? _pendingServerDocUid;
    private string? _pendingServerEventId;
    private string? _pendingServerFingerprint;

    public long? CreatedDocId { get; private set; }

    public NewDocWindow(AppServices services)
    {
        _services = services;
        InitializeComponent();

        var typeOrder = new[]
        {
            DocType.Inbound,
            DocType.ProductionReceipt,
            DocType.Outbound,
            DocType.Move,
            DocType.WriteOff,
            DocType.Inventory
        };
        foreach (var type in typeOrder)
        {
            _types.Add(new DocTypeOption(type, DocTypeMapper.ToDisplayName(type)));
        }

        TypeCombo.ItemsSource = _types;
        TypeCombo.SelectedIndex = 0;

        UpdateDocRef();
        DocRefBox.Focus();
    }

    private void TypeCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        UpdateDocRef();
    }

    private void GenerateRef_Click(object sender, RoutedEventArgs e)
    {
        UpdateDocRef();
    }

    private void UpdateDocRef()
    {
        if (TypeCombo.SelectedItem is not DocTypeOption option)
        {
            return;
        }

        DocRefBox.Text = _services.WpfReadApi.TryGenerateNextDocRef(option.Type, out var apiDocRef)
            ? apiDocRef
            : BuildPreviewDocRef(option.Type);
    }

    private async void Create_Click(object sender, RoutedEventArgs e)
    {
        if (_createInProgress)
        {
            return;
        }

        if (TypeCombo.SelectedItem is not DocTypeOption option)
        {
            MessageBox.Show("Выберите тип документа.", "Документ", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        await TryCreateViaServerAsync(option);
    }

    private async Task TryCreateViaServerAsync(DocTypeOption option)
    {
        SetCreateInProgress(true);
        try
        {
            var requestIdentity = GetOrCreatePendingServerRequestIdentity(option);
            var result = await _services.WpfCreateDocDrafts.CreateDraftAsync(
                new WpfCreateDocDraftContext(
                    requestIdentity.DocUid,
                    requestIdentity.EventId,
                    option.Type,
                    NormalizeValue(DocRefBox.Text),
                    NormalizeValue(CommentBox.Text)));

            if (result.IsSuccess)
            {
                if (result.Response?.Doc == null)
                {
                    MessageBox.Show(
                        "Сервер вернул неполный ответ при создании документа.",
                        "Документ",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }

                CreatedDocId = result.Response.Doc.Id;
                if (!string.IsNullOrWhiteSpace(result.Response.Doc.DocRef))
                {
                    DocRefBox.Text = result.Response.Doc.DocRef;
                }

                if (!string.IsNullOrWhiteSpace(result.Message))
                {
                    MessageBox.Show(result.Message, "Документ", MessageBoxButton.OK, MessageBoxImage.Information);
                }

                DialogResult = true;
                return;
            }

            if (result.Kind is WpfCreateDocDraftResultKind.EventConflict
                or WpfCreateDocDraftResultKind.DuplicateDocUid)
            {
                ResetPendingServerRequestIdentity();
            }

            MessageBox.Show(
                string.IsNullOrWhiteSpace(result.Message)
                    ? "Не удалось создать документ через сервер."
                    : result.Message,
                "Документ",
                MessageBoxButton.OK,
                ResolveServerCreateMessageImage(result.Kind));
        }
        finally
        {
            SetCreateInProgress(false);
        }
    }

    private (string DocUid, string EventId) GetOrCreatePendingServerRequestIdentity(DocTypeOption option)
    {
        var fingerprint = BuildServerRequestFingerprint(option);
        if (string.IsNullOrWhiteSpace(_pendingServerDocUid)
            || string.IsNullOrWhiteSpace(_pendingServerEventId)
            || !string.Equals(_pendingServerFingerprint, fingerprint, StringComparison.Ordinal))
        {
            _pendingServerFingerprint = fingerprint;
            _pendingServerDocUid = $"wpf-create-{Guid.NewGuid():N}";
            _pendingServerEventId = $"wpf-create-{Guid.NewGuid():N}";
        }

        return (_pendingServerDocUid!, _pendingServerEventId!);
    }

    private string BuildServerRequestFingerprint(DocTypeOption option)
    {
        return string.Join(
            "|",
            option.Type.ToString(),
            NormalizeValue(DocRefBox.Text) ?? "<null>",
            NormalizeValue(CommentBox.Text) ?? "<null>");
    }

    private void ResetPendingServerRequestIdentity()
    {
        _pendingServerFingerprint = null;
        _pendingServerDocUid = null;
        _pendingServerEventId = null;
    }

    private void SetCreateInProgress(bool inProgress)
    {
        _createInProgress = inProgress;
        CreateButton.IsEnabled = !inProgress;
        GenerateRefButton.IsEnabled = !inProgress;
    }

    private static MessageBoxImage ResolveServerCreateMessageImage(WpfCreateDocDraftResultKind kind)
    {
        return kind switch
        {
            WpfCreateDocDraftResultKind.ValidationFailed => MessageBoxImage.Warning,
            WpfCreateDocDraftResultKind.EventConflict => MessageBoxImage.Warning,
            WpfCreateDocDraftResultKind.DuplicateDocUid => MessageBoxImage.Warning,
            WpfCreateDocDraftResultKind.InvalidConfiguration => MessageBoxImage.Warning,
            WpfCreateDocDraftResultKind.ServerUnavailable => MessageBoxImage.Warning,
            WpfCreateDocDraftResultKind.Timeout => MessageBoxImage.Warning,
            _ => MessageBoxImage.Error
        };
    }

    private static string? NormalizeValue(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static string BuildPreviewDocRef(DocType type)
    {
        return $"PREVIEW-{DocTypeMapper.ToOpString(type)}-{DateTime.Now:yyyyMMddHHmmss}";
    }

    private sealed record DocTypeOption(DocType Type, string Name);
}

