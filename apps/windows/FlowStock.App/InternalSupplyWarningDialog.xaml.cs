using System.Windows;

namespace FlowStock.App;

public partial class InternalSupplyWarningDialog : Window
{
    public InternalSupplyWarningDialog(string warningText, string questionText)
    {
        InitializeComponent();
        WarningTextBox.Text = warningText;
        QuestionTextBlock.Text = questionText;
    }

    private void Proceed_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }
}
