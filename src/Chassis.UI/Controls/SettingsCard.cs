using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Chassis.UI.Controls;

/// <summary>
/// Fluent 2 Layer-1 card wrapping a single-pixel bordered container (no DropShadow) with responsive stacking below 600px.
/// </summary>
[ContentProperty(Name = nameof(ActionContent))]
public sealed partial class SettingsCard : UserControl
{
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(SettingsCard),
        new PropertyMetadata(string.Empty, OnContentChanged));

    public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(
        nameof(Description), typeof(string), typeof(SettingsCard),
        new PropertyMetadata(string.Empty, OnContentChanged));

    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(
        nameof(Glyph), typeof(string), typeof(SettingsCard),
        new PropertyMetadata(string.Empty, OnContentChanged));

    public static readonly DependencyProperty ActionContentProperty = DependencyProperty.Register(
        nameof(ActionContent), typeof(object), typeof(SettingsCard),
        new PropertyMetadata(null, OnContentChanged));

    private readonly Border _outerBorder;
    private readonly Grid _layoutGrid;
    private readonly ColumnDefinition _rightColumn;
    private readonly FontIcon _icon;
    private readonly TextBlock _titleBlock;
    private readonly TextBlock _descriptionBlock;
    private readonly ContentPresenter _actionPresenter;

    public SettingsCard()
    {
        _icon = new FontIcon
        {
            FontFamily = new FontFamily("Segoe Fluent Icons"),
            FontSize = 16,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0),
            Visibility = Visibility.Collapsed
        };

        _titleBlock = new TextBlock
        {
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap
        };

        _descriptionBlock = new TextBlock
        {
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed
        };

        var textStack = new StackPanel
        {
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center
        };
        textStack.Children.Add(_titleBlock);
        textStack.Children.Add(_descriptionBlock);

        var leftCluster = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        leftCluster.Children.Add(_icon);
        leftCluster.Children.Add(textStack);

        _actionPresenter = new ContentPresenter
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right
        };

        _rightColumn = new ColumnDefinition { Width = GridLength.Auto };
        _layoutGrid = new Grid { ColumnSpacing = 16 };
        _layoutGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _layoutGrid.ColumnDefinitions.Add(_rightColumn);
        _layoutGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _layoutGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        Grid.SetRow(leftCluster, 0);
        Grid.SetColumn(leftCluster, 0);
        Grid.SetRow(_actionPresenter, 0);
        Grid.SetColumn(_actionPresenter, 1);

        _layoutGrid.Children.Add(leftCluster);
        _layoutGrid.Children.Add(_actionPresenter);

        _outerBorder = new Border
        {
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(20, 16, 20, 16),
            Child = _layoutGrid
        };

        _outerBorder.SizeChanged += OnCardSizeChanged;
        ActualThemeChanged += (_, _) => ApplySurfaceBrushes();

        Content = _outerBorder;
        ApplySurfaceBrushes();
    }

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string Description
    {
        get => (string)GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    public string Glyph
    {
        get => (string)GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    public object? ActionContent
    {
        get => GetValue(ActionContentProperty);
        set => SetValue(ActionContentProperty, value);
    }

    private static void OnContentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is SettingsCard card)
        {
            card.SyncParts();
        }
    }

    private void SyncParts()
    {
        _titleBlock.Text = Title ?? string.Empty;
        _descriptionBlock.Text = Description ?? string.Empty;
        _descriptionBlock.Visibility = string.IsNullOrWhiteSpace(Description) ? Visibility.Collapsed : Visibility.Visible;

        _icon.Glyph = Glyph ?? string.Empty;
        _icon.Visibility = string.IsNullOrWhiteSpace(Glyph) ? Visibility.Collapsed : Visibility.Visible;

        _actionPresenter.Content = ActionContent;
        _actionPresenter.Visibility = ActionContent is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnCardSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.NewSize.Width < 560)
        {
            Grid.SetRow(_actionPresenter, 1);
            Grid.SetColumn(_actionPresenter, 0);
            Grid.SetColumnSpan(_actionPresenter, 2);
            _actionPresenter.HorizontalAlignment = HorizontalAlignment.Left;
            _actionPresenter.Margin = new Thickness(0, 12, 0, 0);
        }
        else
        {
            Grid.SetRow(_actionPresenter, 0);
            Grid.SetColumn(_actionPresenter, 1);
            Grid.SetColumnSpan(_actionPresenter, 1);
            _actionPresenter.HorizontalAlignment = HorizontalAlignment.Right;
            _actionPresenter.Margin = new Thickness(0);
        }
    }

    private void ApplySurfaceBrushes()
    {
        var dark = ActualTheme == ElementTheme.Dark;
        _outerBorder.Background = new SolidColorBrush(
            dark ? Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0xC8, 0xFF, 0xFF, 0xFF));
        _outerBorder.BorderBrush = new SolidColorBrush(
            dark ? Color.FromArgb(0x28, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x14, 0x00, 0x00, 0x00));
        _titleBlock.Foreground = new SolidColorBrush(
            dark ? Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0xE4, 0x00, 0x00, 0x00));
        _descriptionBlock.Foreground = new SolidColorBrush(
            dark ? Color.FromArgb(0xC8, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x96, 0x00, 0x00, 0x00));
        _icon.Foreground = _titleBlock.Foreground;
    }
}
