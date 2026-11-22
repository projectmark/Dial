using Chassis.Core.Changelog;
using Chassis.Core.KnowledgeBase;
using Chassis.Core.Localization;
using Chassis.Core.Versioning;
using Chassis.UI.Theme;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Shapes;
using Windows.ApplicationModel.DataTransfer;

namespace Dial.Views;

public sealed partial class HelpView : UserControl
{
    private readonly IKnowledgeBaseService _kb;
    private readonly IChangelogService _changelog;
    private readonly ILocalizationService _localization;
    private readonly ThemeManager _theme;

    private bool _isChangelogMode;
    private bool _isUpdatingCombo;
    private bool _isSubscribed;
    private string? _lastRenderedCulture;
    private bool _lastRenderedIsDark;
    private string _selectedCategoryKey = "All";
    private string? _selectedArticleId;
    private IReadOnlyList<KnowledgeArticle> _visibleArticles = Array.Empty<KnowledgeArticle>();

    public HelpView()
    {
        InitializeComponent();

        _kb = App.Services.GetRequiredService<IKnowledgeBaseService>();
        _changelog = App.Services.GetRequiredService<IChangelogService>();
        _localization = App.Services.GetRequiredService<ILocalizationService>();
        _theme = App.Services.GetRequiredService<ThemeManager>();

        _theme.BindInteractiveButton(DocsModeBtn, () => !_isChangelogMode);
        _theme.BindInteractiveButton(ChangelogModeBtn, () => _isChangelogMode);
        _theme.BindInteractiveButton(CopyRefButton, () => false);

        InitializeCategoryComboOnce();

        SizeChanged += OnHelpSizeChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;

        if (!string.Equals(_lastRenderedCulture, _localization.CurrentCulture, StringComparison.Ordinal) ||
            _lastRenderedIsDark != _theme.IsDarkTheme)
        {
            RefreshLocalizedUi();
        }
        else
        {
            UpdateChangelogUnreadIndicator();
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_isSubscribed)
        {
            _changelog.UnreadStateChanged += OnChangelogUnreadChanged;
            _theme.ThemeChanged += OnThemeChanged;
            _localization.LanguageChanged += OnLanguageChanged;
            _isSubscribed = true;
        }

        if (!string.Equals(_lastRenderedCulture, _localization.CurrentCulture, StringComparison.Ordinal) ||
            _lastRenderedIsDark != _theme.IsDarkTheme)
        {
            RefreshLocalizedUi();
        }
        else
        {
            UpdateChangelogUnreadIndicator();
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_isSubscribed)
        {
            _changelog.UnreadStateChanged -= OnChangelogUnreadChanged;
            _theme.ThemeChanged -= OnThemeChanged;
            _localization.LanguageChanged -= OnLanguageChanged;
            _isSubscribed = false;
        }
    }

    private void OnChangelogUnreadChanged(bool _)
        => DispatcherQueue?.TryEnqueue(() =>
        {
            if (_isSubscribed)
            {
                UpdateChangelogUnreadIndicator();
            }
        });

    private void UpdateChangelogUnreadIndicator()
    {
        ChangelogUnreadDot.Visibility = _changelog.HasNewVersion ? Visibility.Visible : Visibility.Collapsed;
        ChangelogUnreadDot.Fill = _theme.GetAdaptiveAccentBrush();
    }

    private void OnThemeChanged() => ApplyThemeVisuals(refreshContent: true);

    private void OnLanguageChanged(string _) => RefreshLocalizedUi();

    public void SwitchToMode(bool changelogMode)
    {
        var sameMode = _isChangelogMode == changelogMode;
        _isChangelogMode = changelogMode;
        DocsFilterBar.Visibility = changelogMode ? Visibility.Collapsed : Visibility.Visible;
        DocsSplitGrid.Visibility = changelogMode ? Visibility.Collapsed : Visibility.Visible;
        ChangelogScroll.Visibility = changelogMode ? Visibility.Visible : Visibility.Collapsed;

        if (changelogMode)
        {
            _changelog.MarkCurrentAsViewed();
            if (!sameMode || ChangelogPanel.Children.Count == 0)
            {
                BuildChangelogTimeline();
            }
        }
        else if (!sameMode || ArticleListPanel.Children.Count == 0)
        {
            RefreshArticles();
        }

        UpdateChangelogUnreadIndicator();
        _theme.RefreshInteractiveButton(DocsModeBtn);
        _theme.RefreshInteractiveButton(ChangelogModeBtn);
    }

    private void InitializeCategoryComboOnce()
    {
        _isUpdatingCombo = true;
        CategoryCombo.Items.Add(new ComboBoxItem { Tag = "All", Content = _localization["Help.CategoryAll"] });
        foreach (var cat in _kb.GetCategories())
        {
            CategoryCombo.Items.Add(new ComboBoxItem { Tag = cat, Content = _localization[cat] });
        }
        CategoryCombo.SelectedIndex = 0;
        _isUpdatingCombo = false;
    }

    private void RefreshLocalizedUi()
    {
        _lastRenderedCulture = _localization.CurrentCulture;
        _lastRenderedIsDark = _theme.IsDarkTheme;
        HeaderTitle.Text = _localization["Help.Title"];
        HeaderSubtitle.Text = _localization["Help.Subtitle"];
        DocsModeBtn.Content = _localization["Help.Tab.Docs"];
        ChangelogModeLabel.Text = _localization["Help.Tab.Changelog"];
        SearchBox.PlaceholderText = _localization["Help.SearchPlaceholder"];
        CopyRefButton.Content = _localization["Help.CopyReference"];
        AutomationProperties.SetName(SearchBox, _localization["Help.SearchPlaceholder"]);
        AutomationProperties.SetName(DocsModeBtn, _localization["Help.Tab.Docs"]);
        AutomationProperties.SetName(ChangelogModeBtn, _localization["Help.Tab.Changelog"]);
        AutomationProperties.SetName(CopyRefButton, _localization["Help.CopyReference"]);

        _isUpdatingCombo = true;
        foreach (var obj in CategoryCombo.Items)
        {
            if (obj is ComboBoxItem item && item.Tag is string key)
            {
                item.Content = key == "All" ? _localization["Help.CategoryAll"] : _localization[key];
            }
        }
        _isUpdatingCombo = false;

        ApplyThemeVisuals(refreshContent: false);
        if (_isChangelogMode)
        {
            BuildChangelogTimeline();
        }
        else
        {
            RefreshArticles();
        }
    }

    private void RefreshArticles()
    {
        _visibleArticles = _kb.Search(SearchBox.Text, _selectedCategoryKey, _localization);
        ArticleListPanel.Children.Clear();

        if (_visibleArticles.Count == 0)
        {
            ArticleListPanel.Children.Add(new TextBlock
            {
                Text = _localization["Help.NoResults"],
                Foreground = _theme.GetTextSecondaryBrush(),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(4)
            });
            DetailTitle.Text = _localization["Help.NoResults"];
            DetailSummary.Text = string.Empty;
            DetailBody.Text = string.Empty;
            CopyRefContainer.Visibility = Visibility.Collapsed;
            DetailBadgePill.Visibility = Visibility.Collapsed;
            return;
        }

        if (_selectedArticleId is null || !_visibleArticles.Any(a => a.Id == _selectedArticleId))
        {
            _selectedArticleId = _visibleArticles[0].Id;
        }

        foreach (var article in _visibleArticles)
        {
            var isSelected = string.Equals(article.Id, _selectedArticleId, StringComparison.OrdinalIgnoreCase);

            var accentBar = new Border
            {
                Width = 3,
                CornerRadius = new CornerRadius(2),
                Background = _theme.GetAdaptiveAccentBrush(),
                Visibility = isSelected ? Visibility.Visible : Visibility.Collapsed
            };

            var textStack = new StackPanel { Spacing = 4 };
            textStack.Children.Add(new TextBlock
            {
                Text = _localization[article.TitleKey],
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
                Foreground = isSelected ? _theme.GetAdaptiveAccentBrush() : _theme.GetTextPrimaryBrush()
            });
            textStack.Children.Add(new TextBlock
            {
                Text = _localization[article.SummaryKey],
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Foreground = _theme.GetTextSecondaryBrush()
            });

            var rowGrid = new Grid { ColumnSpacing = 10 };
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(accentBar, 0);
            Grid.SetColumn(textStack, 1);
            rowGrid.Children.Add(accentBar);
            rowGrid.Children.Add(textStack);

            var itemBtn = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(12, 10, 12, 10),
                CornerRadius = new CornerRadius(8),
                Content = rowGrid
            };
            AutomationProperties.SetName(itemBtn, _localization[article.TitleKey]);
            var capturedId = article.Id;
            itemBtn.Click += (_, _) =>
            {
                _selectedArticleId = capturedId;
                RefreshArticles();
            };

            _theme.BindInteractiveButton(itemBtn, () => string.Equals(_selectedArticleId, capturedId, StringComparison.OrdinalIgnoreCase));
            ArticleListPanel.Children.Add(itemBtn);
        }

        var selected = _visibleArticles.First(a => a.Id == _selectedArticleId);
        RenderSelectedArticle(selected);
    }

    private void RenderSelectedArticle(KnowledgeArticle article)
    {
        DetailCategoryText.Text = _localization[article.CategoryKey];
        DetailTitle.Text = _localization[article.TitleKey];
        DetailSummary.Text = _localization[article.SummaryKey];
        DetailBody.Text = _localization[article.BodyKey];

        var badgeText = !string.IsNullOrWhiteSpace(article.BadgeKey)
            ? _localization[article.BadgeKey]
            : article.BadgeText;
        if (!string.IsNullOrWhiteSpace(badgeText))
        {
            DetailBadgeText.Text = badgeText;
            DetailBadgePill.Visibility = Visibility.Visible;
        }
        else
        {
            DetailBadgePill.Visibility = Visibility.Collapsed;
        }

        var copyRef = !string.IsNullOrWhiteSpace(article.CopyableReferenceKey)
            ? _localization[article.CopyableReferenceKey]
            : article.CopyableReference;
        if (!string.IsNullOrWhiteSpace(copyRef))
        {
            CopyRefText.Text = copyRef;
            CopyRefButton.Content = _localization["Help.CopyReference"];
            CopyRefContainer.Visibility = Visibility.Visible;
        }
        else
        {
            CopyRefContainer.Visibility = Visibility.Collapsed;
        }
    }

    private void BuildChangelogTimeline()
    {
        ChangelogPanel.Children.Clear();
        var releases = _changelog.GetReleases();

        for (var i = 0; i < releases.Count; i++)
        {
            var rel = releases[i];
            var displayVersion = rel.Version.StartsWith('v') ? rel.Version : $"v{rel.Version}";
            var isLatest = rel.IsLatest || string.Equals(rel.Version, AppVersion.InformationalVersion, StringComparison.OrdinalIgnoreCase);

            var timelineCol = new Grid { Width = 24 };
            timelineCol.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            timelineCol.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var nodeDot = new Ellipse
            {
                Width = 10,
                Height = 10,
                Margin = new Thickness(0, 6, 0, 0),
                Fill = isLatest ? _theme.GetAdaptiveAccentBrush() : _theme.GetTextSecondaryBrush(),
                HorizontalAlignment = HorizontalAlignment.Center
            };
            Grid.SetRow(nodeDot, 0);
            timelineCol.Children.Add(nodeDot);

            if (i < releases.Count - 1)
            {
                var line = new Rectangle
                {
                    Width = 2,
                    Margin = new Thickness(0, 6, 0, 0),
                    Fill = _theme.GetCardBorderBrush(),
                    HorizontalAlignment = HorizontalAlignment.Center
                };
                Grid.SetRow(line, 1);
                timelineCol.Children.Add(line);
            }

            var headerRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            headerRow.Children.Add(new TextBlock
            {
                Text = displayVersion,
                FontSize = 18,
                FontWeight = FontWeights.SemiBold,
                Foreground = _theme.GetTextPrimaryBrush()
            });

            if (isLatest)
            {
                headerRow.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(10),
                    BorderThickness = new Thickness(1),
                    BorderBrush = _theme.GetAdaptiveAccentBrush(),
                    Background = _theme.GetAdaptiveAccentTintBrush(),
                    Padding = new Thickness(10, 2, 10, 2),
                    Child = new TextBlock
                    {
                        Text = _localization["Help.Changelog.LatestBadge"],
                        FontSize = 11,
                        FontWeight = FontWeights.SemiBold,
                        Foreground = _theme.GetAdaptiveAccentBrush()
                    }
                });
            }

            headerRow.Children.Add(new TextBlock
            {
                Text = rel.ReleaseDate.ToString("dd-MM-yyyy"),
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = _theme.GetTextSecondaryBrush()
            });

            var changesStack = new StackPanel { Spacing = 8, Margin = new Thickness(0, 8, 0, 0) };
            foreach (var ch in rel.Changes)
            {
                var badgeBrush = ch.ChangeType.ToUpperInvariant() switch
                {
                    "ADDED" => _theme.GetSuccessBrush(),
                    "CHANGED" => _theme.GetCautionBrush(),
                    _ => _theme.GetAdaptiveAccentBrush()
                };

                var row = new Grid { ColumnSpacing = 10 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                var chip = new Border
                {
                    CornerRadius = new CornerRadius(4),
                    BorderThickness = new Thickness(1),
                    BorderBrush = badgeBrush,
                    Padding = new Thickness(8, 2, 8, 2),
                    Child = new TextBlock
                    {
                        Text = _localization[$"Help.Changelog.{ch.ChangeType}"],
                        FontSize = 11,
                        FontWeight = FontWeights.SemiBold,
                        Foreground = badgeBrush
                    }
                };
                Grid.SetColumn(chip, 0);

                var desc = new TextBlock
                {
                    Text = _localization[ch.DescriptionKey],
                    FontSize = 13,
                    TextWrapping = TextWrapping.Wrap,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = _theme.GetTextPrimaryBrush()
                };
                Grid.SetColumn(desc, 1);

                row.Children.Add(chip);
                row.Children.Add(desc);
                changesStack.Children.Add(row);
            }

            var cardBody = new StackPanel { Spacing = 6 };
            cardBody.Children.Add(headerRow);
            cardBody.Children.Add(changesStack);

            var card = new Border
            {
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1),
                Background = _theme.GetCardBackgroundBrush(),
                BorderBrush = _theme.GetCardBorderBrush(),
                Padding = new Thickness(20),
                Child = cardBody
            };

            var outerRow = new Grid { ColumnSpacing = 12 };
            outerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            outerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(timelineCol, 0);
            Grid.SetColumn(card, 1);
            outerRow.Children.Add(timelineCol);
            outerRow.Children.Add(card);

            ChangelogPanel.Children.Add(outerRow);
        }
    }

    private void ApplyThemeVisuals(bool refreshContent = true)
    {
        _lastRenderedIsDark = _theme.IsDarkTheme;
        HeaderTitle.Foreground = _theme.GetTextPrimaryBrush();
        HeaderSubtitle.Foreground = _theme.GetTextSecondaryBrush();

        DetailCard.Background = _theme.GetCardBackgroundBrush();
        DetailCard.BorderBrush = _theme.GetCardBorderBrush();
        DetailTitle.Foreground = _theme.GetTextPrimaryBrush();
        DetailSummary.Foreground = _theme.GetAdaptiveAccentBrush();
        DetailBody.Foreground = _theme.GetTextPrimaryBrush();

        DetailCategoryPill.Background = _theme.GetCardBackgroundBrush();
        DetailCategoryPill.BorderBrush = _theme.GetCardBorderBrush();
        DetailCategoryText.Foreground = _theme.GetTextSecondaryBrush();

        DetailBadgePill.Background = _theme.GetAdaptiveAccentTintBrush();
        DetailBadgePill.BorderBrush = _theme.GetAdaptiveAccentBrush();
        DetailBadgeText.Foreground = _theme.GetAdaptiveAccentBrush();

        CopyRefContainer.Background = _theme.GetCardBackgroundBrush();
        CopyRefContainer.BorderBrush = _theme.GetCardBorderBrush();
        CopyRefText.Foreground = _theme.GetTextPrimaryBrush();

        _theme.RefreshInteractiveButton(DocsModeBtn);
        _theme.RefreshInteractiveButton(ChangelogModeBtn);
        _theme.RefreshInteractiveButton(CopyRefButton);
        UpdateChangelogUnreadIndicator();

        if (!refreshContent)
        {
            return;
        }

        if (_isChangelogMode)
        {
            BuildChangelogTimeline();
        }
        else if (_visibleArticles.Count > 0)
        {
            RefreshArticles();
        }
    }

    private void OnHelpSizeChanged(object sender, SizeChangedEventArgs e)
    {
        DocsLeftCol.Width = e.NewSize.Width < 760 ? new GridLength(220) : new GridLength(300);
    }

    private void DocsModeBtn_Click(object sender, RoutedEventArgs e) => SwitchToMode(false);
    private void ChangelogModeBtn_Click(object sender, RoutedEventArgs e) => SwitchToMode(true);
    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => RefreshArticles();

    private void CategoryCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingCombo)
        {
            return;
        }

        _selectedCategoryKey = (CategoryCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "All";
        RefreshArticles();
    }

    private void CopyRefButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(CopyRefText.Text))
        {
            return;
        }

        try
        {
            var package = new DataPackage();
            package.SetText(CopyRefText.Text);
            Clipboard.SetContent(package);
            CopyRefButton.Content = _localization["Help.Copied"];
        }
        catch
        {
            // Ignore transient clipboard lock errors from external applications.
        }
    }
}
