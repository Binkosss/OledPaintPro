using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace OledPaintPro.Dialogs;

/// <summary>Prosty dialog do wpisania nazwy szablonu oka.</summary>
public sealed class NameDialog : Window
{
    private readonly TextBox _inputBox;

    public string ResultName { get; private set; }

    public NameDialog(string currentName)
    {
        ResultName = currentName;
        Title      = "Nazwa szablonu";
        Width      = 300;
        Height     = 130;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brushes.Black;

        var layout = new StackPanel { Margin = new Thickness(14) };

        var label = new TextBlock
        {
            Text       = "Podaj nazwę oka:",
            Foreground = Brushes.Gray,
            FontSize   = 11,
            Margin     = new Thickness(0, 0, 0, 6),
        };

        _inputBox = new TextBox
        {
            Text          = currentName,
            Background    = Brushes.Black,
            Foreground    = Brushes.White,
            BorderBrush   = Brushes.DimGray,
            Padding       = new Thickness(6, 4, 6, 4),
        };
        _inputBox.SelectAll();

        var btnRow = new StackPanel
        {
            Orientation         = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin              = new Thickness(0, 10, 0, 0),
        };

        var okBtn = new Button
        {
            Content   = "OK",
            Width     = 70,
            Height    = 26,
            Margin    = new Thickness(0, 0, 6, 0),
            IsDefault = true,
        };
        var cancelBtn = new Button
        {
            Content  = "Anuluj",
            Width    = 70,
            Height   = 26,
            IsCancel = true,
        };

        okBtn.Click     += (_, _) =>
        {
            ResultName   = _inputBox.Text.Trim();
            if (ResultName.Length == 0) ResultName = "Oko";
            DialogResult = true;
        };
        cancelBtn.Click += (_, _) => DialogResult = false;

        btnRow.Children.Add(okBtn);
        btnRow.Children.Add(cancelBtn);
        layout.Children.Add(label);
        layout.Children.Add(_inputBox);
        layout.Children.Add(btnRow);
        Content = layout;

        Loaded += (_, _) => _inputBox.Focus();
    }
}
