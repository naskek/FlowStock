using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;

namespace FlowStock.App;

public partial class DocNumberingSettingsWindow : Window
{
    private static readonly Regex YearRegex = new(@"^\d{4}$", RegexOptions.Compiled);

    private readonly AppServices _services;
    private readonly IReadOnlyList<SequenceStyleOption> _styles = new[]
    {
        new SequenceStyleOption("D6", "NNNNNN (6 цифр, с нулями)"),
        new SequenceStyleOption("D5", "NNNNN (5 цифр, с нулями)"),
        new SequenceStyleOption("D4", "NNNN (4 цифры, с нулями)"),
        new SequenceStyleOption("N", "N (без ведущих нулей)")
    };

    public DocNumberingSettingsWindow(AppServices services)
    {
        _services = services;
        InitializeComponent();
        SequenceStyleCombo.ItemsSource = _styles;
        LoadSettings();
    }

    private void LoadSettings()
    {
        var settings = (_services.Settings.Load().DocumentNumbering ?? new DocumentNumberingSettings()).Normalize();
        TemplateBox.Text = settings.Template;
        YearBox.Text = settings.Year ?? string.Empty;
        SequenceStyleCombo.SelectedItem = _styles.FirstOrDefault(style =>
                                           string.Equals(style.Code, settings.SequenceStyle, StringComparison.OrdinalIgnoreCase))
                                       ?? _styles.First();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var template = (TemplateBox.Text ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(template))
        {
            MessageBox.Show("Шаблон не может быть пустым.", "Нумерация документов", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var year = (YearBox.Text ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(year) && !YearRegex.IsMatch(year))
        {
            MessageBox.Show("Год должен быть в формате YYYY, например 2026.", "Нумерация документов", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (SequenceStyleCombo.SelectedItem is not SequenceStyleOption style)
        {
            MessageBox.Show("Выберите стиль номера.", "Нумерация документов", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var all = _services.Settings.Load();
        all.DocumentNumbering = new DocumentNumberingSettings
        {
            Template = template,
            Year = string.IsNullOrWhiteSpace(year) ? null : year,
            SequenceStyle = style.Code
        }.Normalize();
        _services.Settings.Save(all);

        MessageBox.Show("Настройки нумерации сохранены.", "Нумерация документов", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        TemplateBox.Text = "{PREFIX}-{YYYY}-{SEQ}";
        YearBox.Text = string.Empty;
        SequenceStyleCombo.SelectedItem = _styles.First();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private sealed record SequenceStyleOption(string Code, string Name);
}
