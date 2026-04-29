using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using FlowStock.Core.Models;
using FlowStock.Core.Services;

namespace FlowStock.App;

public partial class NewDocWindow : Window
{
    private static readonly Regex YearRegex = new(@"^\d{4}$", RegexOptions.Compiled);

    private readonly AppServices _services;
    private readonly List<DocTypeOption> _types = new();
    private readonly DocumentNumberingSettings _numberingSettings;
    private bool _createInProgress;
    private bool _suppressTypeSelectionChanged;
    private int _sequenceNumber = 1;
    private string _docYear = DateTime.Now.Year.ToString(CultureInfo.InvariantCulture);
    private string? _pendingServerDocUid;
    private string? _pendingServerEventId;
    private string? _pendingServerFingerprint;

    public long? CreatedDocId { get; private set; }
    public string? CreatedDocUid { get; private set; }

    public NewDocWindow(AppServices services)
    {
        _services = services;
        _numberingSettings = (_services.Settings.Load().DocumentNumbering ?? new DocumentNumberingSettings()).Normalize();
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
        _suppressTypeSelectionChanged = true;
        TypeCombo.SelectedIndex = 0;
        _suppressTypeSelectionChanged = false;

        InitializeDocRefParts();
        UpdateDocRefDisplay();
        CommentBox.Focus();
    }

    private void TypeCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_suppressTypeSelectionChanged)
        {
            return;
        }

        UpdateDocRefDisplay();
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

    private void InitializeDocRefParts()
    {
        if (TypeCombo.SelectedItem is not DocTypeOption option)
        {
            return;
        }

        if (_services.WpfReadApi.TryGenerateNextDocRef(option.Type, out var apiDocRef)
            && TryParseDocRef(apiDocRef, out var apiYear, out var sequence))
        {
            _docYear = ResolveDocYear(apiYear);
            _sequenceNumber = sequence;
            return;
        }

        _docYear = ResolveDocYear(DateTime.Now.Year.ToString(CultureInfo.InvariantCulture));
        _sequenceNumber = 1;
    }

    private void UpdateDocRefDisplay(string? forcedDocRef = null)
    {
        if (!string.IsNullOrWhiteSpace(forcedDocRef))
        {
            DocRefText.Text = forcedDocRef.Trim();
            return;
        }

        if (TypeCombo.SelectedItem is not DocTypeOption option)
        {
            DocRefText.Text = string.Empty;
            return;
        }

        var sequenceText = FormatSequence(_sequenceNumber);
        if (string.IsNullOrWhiteSpace(sequenceText))
        {
            DocRefText.Text = "Не удалось получить номер";
            return;
        }

        var builtRef = _numberingSettings.Template
            .Replace("{PREFIX}", DocRefGenerator.GetPrefix(option.Type), StringComparison.OrdinalIgnoreCase)
            .Replace("{YYYY}", _docYear, StringComparison.OrdinalIgnoreCase)
            .Replace("{SEQ}", sequenceText, StringComparison.OrdinalIgnoreCase);
        DocRefText.Text = builtRef;
    }

    private async Task TryCreateViaServerAsync(DocTypeOption option)
    {
        var requestedDocRef = NormalizeValue(DocRefText.Text);
        if (string.IsNullOrWhiteSpace(requestedDocRef))
        {
            MessageBox.Show("Не удалось определить номер документа.", "Документ", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        SetCreateInProgress(true);
        try
        {
            var requestIdentity = GetOrCreatePendingServerRequestIdentity(option, requestedDocRef);
            var result = await _services.WpfCreateDocDrafts.CreateDraftAsync(
                new WpfCreateDocDraftContext(
                    requestIdentity.DocUid,
                    requestIdentity.EventId,
                    option.Type,
                    requestedDocRef,
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
                CreatedDocUid = string.IsNullOrWhiteSpace(result.Response.Doc.DocUid)
                    ? requestIdentity.DocUid
                    : result.Response.Doc.DocUid.Trim();
                if (!string.IsNullOrWhiteSpace(result.Response.Doc.DocRef))
                {
                    UpdateDocRefDisplay(result.Response.Doc.DocRef);
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

    private (string DocUid, string EventId) GetOrCreatePendingServerRequestIdentity(DocTypeOption option, string requestedDocRef)
    {
        var fingerprint = BuildServerRequestFingerprint(option, requestedDocRef);
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

    private string BuildServerRequestFingerprint(DocTypeOption option, string requestedDocRef)
    {
        return string.Join(
            "|",
            option.Type.ToString(),
            requestedDocRef,
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
        TypeCombo.IsEnabled = !inProgress;
        CommentBox.IsEnabled = !inProgress;
    }

    private string ResolveDocYear(string defaultYear)
    {
        return YearRegex.IsMatch(_numberingSettings.Year ?? string.Empty)
            ? _numberingSettings.Year!
            : defaultYear;
    }

    private string FormatSequence(int sequence)
    {
        if (sequence < 1)
        {
            return string.Empty;
        }

        return _numberingSettings.SequenceStyle.ToUpperInvariant() switch
        {
            "D6" => sequence.ToString("D6", CultureInfo.InvariantCulture),
            "D5" => sequence.ToString("D5", CultureInfo.InvariantCulture),
            "D4" => sequence.ToString("D4", CultureInfo.InvariantCulture),
            "N" => sequence.ToString(CultureInfo.InvariantCulture),
            _ => sequence.ToString("D6", CultureInfo.InvariantCulture)
        };
    }

    private static bool TryParseDocRef(string? docRef, out string year, out int sequence)
    {
        year = string.Empty;
        sequence = 0;
        if (string.IsNullOrWhiteSpace(docRef))
        {
            return false;
        }

        var parts = docRef.Trim().Split('-', 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3)
        {
            return false;
        }

        if (!YearRegex.IsMatch(parts[1]))
        {
            return false;
        }

        if (!int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedSequence)
            || parsedSequence < 1)
        {
            return false;
        }

        year = parts[1];
        sequence = parsedSequence;
        return true;
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

    private sealed record DocTypeOption(DocType Type, string Name);
}
