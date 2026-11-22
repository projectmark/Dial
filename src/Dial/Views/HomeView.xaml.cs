using System.Diagnostics;
using System.Numerics;
using Chassis.Core.Data;
using Chassis.Core.Localization;
using Chassis.Core.Settings;
using Chassis.Core.Versioning;
using Chassis.UI.Theme;
using Dial.Modules.Radio;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Storage.Pickers;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;
using WinRT.Interop;

namespace Dial.Views;

public sealed partial class HomeView : UserControl
{
    private sealed record StationRowVisual(
        RadioStation Station,
        Button? CardButton,
        Border AccentPill,
        TextBlock TitleText,
        TextBlock? TaglineText,
        TextBlock? MetaText,
        Border? IconBox,
        FontIcon? IconGlyph);

    private sealed record EditRowVisual(
        string StationId,
        int Index,
        Border RowBorder,
        CheckBox SelectBox,
        Border TopDropIndicator,
        Border BottomDropIndicator,
        FontIcon GripIcon);

    private const double StickyTopMargin = 12.0;
    private const float StickyTopClipInset = 16.0f;

    private static readonly int[] SleepPresetMinutes = [15, 30, 45, 60, 120, 240, 360];

    private readonly IRadioPlayerService _player;
    private readonly ISleepTimerService _sleepTimer;
    private readonly IListeningStatsService _stats;
    private readonly IPackageShareService<List<RadioStation>> _shareService;
    private readonly ISettingsService _settings;
    private readonly ILocalizationService _localization;
    private readonly ThemeManager _theme;
    private readonly List<StationRowVisual> _stationVisuals = [];
    private readonly List<EditRowVisual> _editRowVisuals = [];
    private readonly List<Button> _sleepPresetButtons = [];
    private readonly HashSet<string> _selectedStationIds = new(StringComparer.OrdinalIgnoreCase);

    private List<string> _draggedStationIds = [];
    private int _lastSelectionAnchorIndex = -1;
    private bool _isSyncingVolume;
    private bool _isUpdatingSliderFromInput;
    private bool? _lastRenderedMuted;
    private (string ViewMode, bool ShowNumbers, string Alignment, string FontSize) _lastListLayoutSignature;
    private string? _lastRenderedCulture;
    private List<(string Id, string Name, string StreamUrl)> _lastRenderedStations = [];
    private CompositionPropertySet? _scrollManipulationProps;
    private Visual? _editToolbarVisual;
    private ExpressionAnimation? _stickyTranslationAnimation;
    private InsetClip? _scrollViewerClip;
    private ExpressionAnimation? _scrollClipAnimation;
    private double _stickyThreshold;
    private double _lastSyncedXamlOffset;
    private bool _isStickyCompositionActive;
    private bool _isSubscribed;
    private bool _isEditMode;
    private bool _isSingleColumn;
    private bool _isCompactTransport;

    public HomeView()
    {
        InitializeComponent();

        _player = App.Services.GetRequiredService<IRadioPlayerService>();
        _sleepTimer = App.Services.GetRequiredService<ISleepTimerService>();
        _stats = App.Services.GetRequiredService<IListeningStatsService>();
        _shareService = App.Services.GetRequiredService<IPackageShareService<List<RadioStation>>>();
        _settings = App.Services.GetRequiredService<ISettingsService>();
        _localization = App.Services.GetRequiredService<ILocalizationService>();
        _theme = App.Services.GetRequiredService<ThemeManager>();

        _theme.BindInteractiveButton(PrevStationBtn, () => false);
        _theme.BindInteractiveButton(PlayPauseBtn, () => _player.Current.State is RadioPlaybackState.Playing or RadioPlaybackState.Buffering);
        _theme.BindInteractiveButton(NextStationBtn, () => false);
        _theme.BindInteractiveButton(MuteBtn, () => _player.Current.IsMuted);
        _theme.BindInteractiveButton(EditModeBtn, () => _isEditMode);
        _theme.BindInteractiveButton(SleepTimerBtn, () => _sleepTimer.IsActive);
        _theme.BindInteractiveButton(StatsBtn, () => false);
        _theme.BindInteractiveButton(MiniPlayerBtn, () => false);
        _theme.BindInteractiveButton(MinimizeTrayBtn, () => false);
        _theme.BindInteractiveButton(AddCustomBtn, () => false);
        _theme.BindInteractiveButton(ExportBtn, () => false);
        _theme.BindInteractiveButton(ImportBtn, () => false);
        _theme.BindInteractiveButton(DoneEditingBtn, () => true);
        _theme.BindInteractiveButton(BulkDeleteBtn, () => false);
        _theme.BindInteractiveButton(SleepCustomStartBtn, () => false);
        _theme.BindInteractiveButton(SleepFlyoutCancelBtn, () => false);
        _theme.BindInteractiveButton(OpenTrackLookupBtn, () => false);

        BuildSleepPresets();
        RebuildStationsUi();

        SizeChanged += OnHomeSizeChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;

        ApplyLocalization();
        RenderState(_player.Current);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_isSubscribed)
        {
            _player.StateChanged += OnPlayerStateChanged;
            _player.VolumeChanged += OnPlayerVolumeChanged;
            _player.BufferLevelChanged += OnPlayerBufferLevelChanged;
            _player.StationsChanged += OnStationsChanged;
            _sleepTimer.RemainingChanged += OnSleepRemainingChanged;
            _stats.SessionElapsedChanged += OnSessionElapsedChanged;
            _settings.SettingsChanged += OnSettingsChanged;
            _theme.ThemeChanged += OnThemeChanged;
            _localization.LanguageChanged += OnLanguageChanged;
            _isSubscribed = true;
        }

        if (ReadListLayoutSignature() != _lastListLayoutSignature || StationsDifferFromRendered())
        {
            RebuildStationsUi();
        }

        if (!string.Equals(_lastRenderedCulture, _localization.CurrentCulture, StringComparison.Ordinal))
        {
            ApplyLocalization();
        }

        RenderState(_player.Current);
    }

    private bool StationsDifferFromRendered()
    {
        var current = _player.Stations;
        if (current.Count != _lastRenderedStations.Count)
        {
            return true;
        }

        for (var i = 0; i < current.Count; i++)
        {
            var a = current[i];
            var b = _lastRenderedStations[i];
            if (!string.Equals(a.Id, b.Id, StringComparison.Ordinal) ||
                !string.Equals(a.Name, b.Name, StringComparison.Ordinal) ||
                !string.Equals(a.StreamUrl, b.StreamUrl, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_isSubscribed)
        {
            _player.StateChanged -= OnPlayerStateChanged;
            _player.VolumeChanged -= OnPlayerVolumeChanged;
            _player.BufferLevelChanged -= OnPlayerBufferLevelChanged;
            _player.StationsChanged -= OnStationsChanged;
            _sleepTimer.RemainingChanged -= OnSleepRemainingChanged;
            _stats.SessionElapsedChanged -= OnSessionElapsedChanged;
            _settings.SettingsChanged -= OnSettingsChanged;
            _theme.ThemeChanged -= OnThemeChanged;
            _localization.LanguageChanged -= OnLanguageChanged;
            _isSubscribed = false;
        }
    }

    private void OnPlayerStateChanged(RadioStateSnapshot snapshot)
        => DispatcherQueue?.TryEnqueue(() =>
        {
            if (_isSubscribed)
            {
                RenderState(snapshot);
            }
        });

    private void OnPlayerVolumeChanged(double volume, bool isMuted)
        => DispatcherQueue?.TryEnqueue(() =>
        {
            if (_isSubscribed && !_isUpdatingSliderFromInput)
            {
                ApplyVolumeUi(volume, isMuted, updateSlider: true);
            }
        });

    private void OnPlayerBufferLevelChanged(double _, int __)
        => DispatcherQueue?.TryEnqueue(() =>
        {
            if (_isSubscribed)
            {
                UpdateHeroMetadata(_player.Current, _stats.CurrentSessionElapsed);
            }
        });

    private void OnStationsChanged()
        => DispatcherQueue?.TryEnqueue(() =>
        {
            if (_isSubscribed)
            {
                RebuildStationsUi();
                RenderState(_player.Current);
            }
        });

    private void OnSleepRemainingChanged(TimeSpan? remaining)
        => DispatcherQueue?.TryEnqueue(() =>
        {
            if (_isSubscribed)
            {
                UpdateSleepTimerUi(remaining);
            }
        });

    private void OnSessionElapsedChanged(TimeSpan elapsed)
        => DispatcherQueue?.TryEnqueue(() =>
        {
            if (_isSubscribed)
            {
                UpdateHeroMetadata(_player.Current, elapsed);
            }
        });

    private void OnSettingsChanged(AppSettings _)
        => DispatcherQueue?.TryEnqueue(() =>
        {
            if (!_isSubscribed)
            {
                return;
            }

            var currentSig = ReadListLayoutSignature();
            if (currentSig != _lastListLayoutSignature)
            {
                RebuildStationsUi();
                RenderState(_player.Current);
            }
            else
            {
                UpdateTrackLookupButton(_player.Current);
            }
        });

    private (string ViewMode, bool ShowNumbers, string Alignment, string FontSize) ReadListLayoutSignature()
        => (
            _settings.GetOption(RadioModule.ViewModeOptionKey, "list"),
            _settings.GetOption(RadioModule.ShowNumbersOptionKey, true),
            _settings.GetOption(RadioModule.AlignmentOptionKey, "left"),
            _settings.GetOption(RadioModule.FontSizeOptionKey, "normal"));

    private void OnThemeChanged() => RenderState(_player.Current);

    private void OnLanguageChanged(string _)
    {
        ApplyLocalization();
        RebuildStationsUi();
        RenderState(_player.Current);
    }

    private void BuildSleepPresets()
    {
        SleepPresetsGrid.Children.Clear();
        _sleepPresetButtons.Clear();

        for (var i = 0; i < SleepPresetMinutes.Length; i++)
        {
            var minutes = SleepPresetMinutes[i];
            var label = minutes >= 60 ? $"{minutes / 60}u" : $"{minutes} min";
            var btn = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Height = 32,
                CornerRadius = new CornerRadius(6),
                Content = label
            };
            AutomationProperties.SetName(btn, label);
            var captured = minutes;
            btn.Click += (_, _) =>
            {
                _sleepTimer.Start(TimeSpan.FromMinutes(captured));
                SleepTimerFlyout.Hide();
            };

            _theme.BindInteractiveButton(btn, () => false);
            Grid.SetRow(btn, i / 2);
            Grid.SetColumn(btn, i % 2);
            SleepPresetsGrid.Children.Add(btn);
            _sleepPresetButtons.Add(btn);
        }
    }

    private void RebuildStationsUi()
    {
        StationsListPanel.Children.Clear();
        StationsGrid.Children.Clear();
        StationsGrid.ColumnDefinitions.Clear();
        StationsGrid.RowDefinitions.Clear();
        _stationVisuals.Clear();
        _editRowVisuals.Clear();

        var stations = _player.Stations;
        _lastRenderedStations = stations.Select(s => (s.Id, s.Name, s.StreamUrl)).ToList();
        _selectedStationIds.RemoveWhere(id => !stations.Any(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase)));
        if (_lastSelectionAnchorIndex >= stations.Count)
        {
            _lastSelectionAnchorIndex = stations.Count - 1;
        }

        _lastListLayoutSignature = ReadListLayoutSignature();
        var viewMode = _lastListLayoutSignature.ViewMode;
        var useGrid = !_isEditMode && string.Equals(viewMode, "grid", StringComparison.OrdinalIgnoreCase);

        StationsListPanel.Visibility = useGrid ? Visibility.Collapsed : Visibility.Visible;
        StationsListPanel.Spacing = _isEditMode ? 2 : 6;
        StationsGrid.Visibility = useGrid ? Visibility.Visible : Visibility.Collapsed;

        if (useGrid)
        {
            BuildGridStations(stations);
        }
        else
        {
            BuildListStations(stations);
        }

        if (_isEditMode)
        {
            SyncEditSelectionVisuals();
        }
    }

    private void BuildListStations(IReadOnlyList<RadioStation> stations)
    {
        var showNumbers = _settings.GetOption(RadioModule.ShowNumbersOptionKey, true);
        var alignment = _settings.GetOption(RadioModule.AlignmentOptionKey, "left").ToLowerInvariant();
        var fontSizeMode = _settings.GetOption(RadioModule.FontSizeOptionKey, "normal").ToLowerInvariant();

        var fontSize = fontSizeMode switch
        {
            "compact" => 13.0,
            "large" => 18.0,
            _ => 15.0
        };

        var rowPadding = fontSizeMode switch
        {
            "compact" => new Thickness(12, 8, 12, 8),
            "large" => new Thickness(16, 14, 16, 14),
            _ => new Thickness(14, 10, 14, 10)
        };

        var textAlign = alignment switch
        {
            "center" => TextAlignment.Center,
            "right" => TextAlignment.Right,
            _ => TextAlignment.Left
        };

        var horizAlign = alignment switch
        {
            "center" => HorizontalAlignment.Center,
            "right" => HorizontalAlignment.Right,
            _ => HorizontalAlignment.Left
        };

        for (var i = 0; i < stations.Count; i++)
        {
            var station = stations[i];
            var displayName = showNumbers ? $"{i + 1:00}. {station.Name}" : station.Name;

            var accentPill = new Border
            {
                Width = 3,
                CornerRadius = new CornerRadius(2),
                VerticalAlignment = VerticalAlignment.Stretch
            };

            var titleText = new TextBlock
            {
                Text = displayName,
                FontSize = fontSize,
                FontWeight = FontWeights.SemiBold,
                TextAlignment = textAlign,
                HorizontalAlignment = horizAlign,
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap
            };

            if (_isEditMode)
            {
                var capturedStation = station;
                var capturedIndex = i;

                var gripIcon = new FontIcon
                {
                    FontFamily = new FontFamily("Segoe Fluent Icons"),
                    Glyph = "\uE784",
                    FontSize = 13,
                    VerticalAlignment = VerticalAlignment.Center
                };

                var selectBox = new CheckBox
                {
                    MinWidth = 0,
                    Width = 24,
                    Padding = new Thickness(0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                AutomationProperties.SetName(selectBox, displayName);
                selectBox.Click += (_, _) => HandleEditItemSelection(capturedStation.Id, capturedIndex, IsShiftDown());

                titleText.TextAlignment = TextAlignment.Left;
                titleText.HorizontalAlignment = HorizontalAlignment.Left;

                var titleTapTarget = new Grid
                {
                    Background = new SolidColorBrush(Colors.Transparent),
                    VerticalAlignment = VerticalAlignment.Stretch,
                    HorizontalAlignment = HorizontalAlignment.Stretch
                };
                titleTapTarget.Children.Add(titleText);
                titleTapTarget.Tapped += (_, _) => HandleEditItemSelection(capturedStation.Id, capturedIndex, IsShiftDown());

                var actionsPanel = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 4,
                    VerticalAlignment = VerticalAlignment.Center
                };

                var upBtn = CreateIconActionButton("\uE70E", _localization["Radio.Edit.MoveUp"], i > 0, async () =>
                {
                    await _player.MoveStationAsync(capturedStation.Id, -1);
                });
                var downBtn = CreateIconActionButton("\uE70D", _localization["Radio.Edit.MoveDown"], i < stations.Count - 1, async () =>
                {
                    await _player.MoveStationAsync(capturedStation.Id, 1);
                });
                var renameBtn = CreateIconActionButton("\uE8AC", _localization["Radio.Edit.Rename"], true, async () =>
                {
                    await PromptRenameStationAsync(capturedStation);
                });
                var deleteBtn = CreateIconActionButton("\uE74D", _localization["Radio.Edit.Delete"], stations.Count > 1, async () =>
                {
                    await _player.RemoveStationAsync(capturedStation.Id);
                });

                actionsPanel.Children.Add(upBtn);
                actionsPanel.Children.Add(downBtn);
                actionsPanel.Children.Add(renameBtn);
                actionsPanel.Children.Add(deleteBtn);

                var rowGrid = new Grid { ColumnSpacing = 8 };
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                Grid.SetColumn(gripIcon, 0);
                Grid.SetColumn(selectBox, 1);
                Grid.SetColumn(accentPill, 2);
                Grid.SetColumn(titleTapTarget, 3);
                Grid.SetColumn(actionsPanel, 4);

                rowGrid.Children.Add(gripIcon);
                rowGrid.Children.Add(selectBox);
                rowGrid.Children.Add(accentPill);
                rowGrid.Children.Add(titleTapTarget);
                rowGrid.Children.Add(actionsPanel);

                var topDropIndicator = new Border
                {
                    Height = 2,
                    CornerRadius = new CornerRadius(1),
                    Margin = new Thickness(8, 0, 8, 2),
                    Opacity = 0,
                    IsHitTestVisible = false
                };

                var bottomDropIndicator = new Border
                {
                    Height = 2,
                    CornerRadius = new CornerRadius(1),
                    Margin = new Thickness(8, 2, 8, 0),
                    Opacity = 0,
                    IsHitTestVisible = false
                };

                var rowBorder = new Border
                {
                    CornerRadius = new CornerRadius(8),
                    BorderThickness = new Thickness(1),
                    Padding = new Thickness(12, 8, 12, 8),
                    Background = _theme.GetCardBackgroundBrush(),
                    BorderBrush = _theme.GetCardBorderBrush(),
                    CanDrag = true,
                    AllowDrop = true,
                    Child = rowGrid
                };

                rowBorder.DragStarting += (_, args) =>
                {
                    var currentStations = _player.Stations;
                    if (_selectedStationIds.Contains(capturedStation.Id))
                    {
                        _draggedStationIds = currentStations
                            .Where(s => _selectedStationIds.Contains(s.Id))
                            .Select(s => s.Id)
                            .ToList();
                    }
                    else
                    {
                        _draggedStationIds = [capturedStation.Id];
                    }

                    args.AllowedOperations = DataPackageOperation.Move;
                    args.Data.RequestedOperation = DataPackageOperation.Move;
                    args.Data.SetText(_draggedStationIds.Count == 1
                        ? capturedStation.Name
                        : _localization.GetString("Radio.Edit.DragCount", _draggedStationIds.Count));

                    foreach (var visual in _editRowVisuals)
                    {
                        if (_draggedStationIds.Contains(visual.StationId, StringComparer.OrdinalIgnoreCase))
                        {
                            visual.RowBorder.Opacity = 0.45;
                        }
                    }
                };

                rowBorder.DragOver += (_, args) =>
                {
                    if (_draggedStationIds.Count == 0)
                    {
                        return;
                    }

                    args.AcceptedOperation = DataPackageOperation.Move;
                    args.DragUIOverride.IsCaptionVisible = true;
                    args.DragUIOverride.IsContentVisible = true;
                    args.DragUIOverride.Caption = _draggedStationIds.Count == 1
                        ? _localization["Radio.Edit.MoveHere"]
                        : _localization.GetString("Radio.Edit.DragCount", _draggedStationIds.Count);

                    var pos = args.GetPosition(rowBorder);
                    var insertAfter = pos.Y >= rowBorder.ActualHeight / 2.0;
                    ShowDropIndicatorForRow(capturedIndex, insertAfter);
                };

                rowBorder.DragLeave += (_, _) =>
                {
                    topDropIndicator.Opacity = 0;
                    bottomDropIndicator.Opacity = 0;
                };

                rowBorder.Drop += async (_, args) =>
                {
                    ClearAllDropIndicators();
                    if (_draggedStationIds.Count == 0)
                    {
                        return;
                    }

                    var pos = args.GetPosition(rowBorder);
                    var insertAfter = pos.Y >= rowBorder.ActualHeight / 2.0;
                    var targetInsertionIndex = insertAfter ? capturedIndex + 1 : capturedIndex;

                    var idsToMove = _draggedStationIds.ToList();
                    _draggedStationIds.Clear();
                    RestoreAllRowOpacities();

                    await _player.MoveStationsAsync(idsToMove, targetInsertionIndex);
                };

                rowBorder.DropCompleted += (_, _) =>
                {
                    _draggedStationIds.Clear();
                    ClearAllDropIndicators();
                    RestoreAllRowOpacities();
                };

                var wrapper = new StackPanel { Spacing = 0 };
                wrapper.Children.Add(topDropIndicator);
                wrapper.Children.Add(rowBorder);
                wrapper.Children.Add(bottomDropIndicator);

                StationsListPanel.Children.Add(wrapper);
                _stationVisuals.Add(new StationRowVisual(station, null, accentPill, titleText, null, null, null, null));
                _editRowVisuals.Add(new EditRowVisual(
                    station.Id,
                    i,
                    rowBorder,
                    selectBox,
                    topDropIndicator,
                    bottomDropIndicator,
                    gripIcon));
            }
            else
            {
                var metaText = new TextBlock
                {
                    FontSize = 11,
                    FontWeight = FontWeights.SemiBold,
                    VerticalAlignment = VerticalAlignment.Center,
                    Visibility = alignment == "left" ? Visibility.Visible : Visibility.Collapsed
                };

                var rowGrid = new Grid { ColumnSpacing = 10 };
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                Grid.SetColumn(accentPill, 0);
                Grid.SetColumn(titleText, 1);
                Grid.SetColumn(metaText, 2);
                rowGrid.Children.Add(accentPill);
                rowGrid.Children.Add(titleText);
                rowGrid.Children.Add(metaText);

                var capturedId = station.Id;
                var rowBtn = new Button
                {
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    Padding = rowPadding,
                    CornerRadius = new CornerRadius(8),
                    Content = rowGrid
                };
                rowBtn.Click += async (_, _) => await _player.PlayStationAsync(capturedId);
                AutomationProperties.SetName(rowBtn, displayName);
                if (!string.IsNullOrWhiteSpace(station.Tagline))
                {
                    ToolTipService.SetToolTip(rowBtn, station.Tagline);
                }

                _theme.BindInteractiveButton(
                    rowBtn,
                    () => string.Equals(_player.Current.ActiveStation?.Id, capturedId, StringComparison.OrdinalIgnoreCase));

                StationsListPanel.Children.Add(rowBtn);
                _stationVisuals.Add(new StationRowVisual(station, rowBtn, accentPill, titleText, null, metaText, null, null));
            }
        }
    }

    private void BuildGridStations(IReadOnlyList<RadioStation> stations)
    {
        var showNumbers = _settings.GetOption(RadioModule.ShowNumbersOptionKey, true);
        var fontSizeMode = _settings.GetOption(RadioModule.FontSizeOptionKey, "normal").ToLowerInvariant();
        var titleFontSize = fontSizeMode switch
        {
            "compact" => 14.0,
            "large" => 17.0,
            _ => 15.0
        };

        for (var i = 0; i < stations.Count; i++)
        {
            var station = stations[i];
            var displayName = showNumbers ? $"{i + 1:00}. {station.Name}" : station.Name;

            var accentPill = new Border
            {
                Width = 3,
                CornerRadius = new CornerRadius(2),
                VerticalAlignment = VerticalAlignment.Stretch
            };

            var iconGlyph = new FontIcon
            {
                FontFamily = new FontFamily("Segoe Fluent Icons"),
                Glyph = station.IconGlyph,
                FontSize = 18
            };

            var iconBox = new Border
            {
                Width = 40,
                Height = 40,
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1),
                VerticalAlignment = VerticalAlignment.Top,
                Child = iconGlyph
            };

            var titleText = new TextBlock
            {
                Text = displayName,
                FontSize = titleFontSize,
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap
            };

            var metaText = new TextBlock
            {
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            };

            var titleRow = new Grid { ColumnSpacing = 8 };
            titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(titleText, 0);
            Grid.SetColumn(metaText, 1);
            titleRow.Children.Add(titleText);
            titleRow.Children.Add(metaText);

            var taglineText = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(station.Tagline) ? station.StreamUrl : station.Tagline,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap
            };

            var textStack = new StackPanel { Spacing = 4 };
            textStack.Children.Add(titleRow);
            textStack.Children.Add(taglineText);

            var cardGrid = new Grid { ColumnSpacing = 12 };
            cardGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            cardGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            cardGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(accentPill, 0);
            Grid.SetColumn(iconBox, 1);
            Grid.SetColumn(textStack, 2);
            cardGrid.Children.Add(accentPill);
            cardGrid.Children.Add(iconBox);
            cardGrid.Children.Add(textStack);

            var capturedId = station.Id;
            var cardBtn = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Stretch,
                Padding = new Thickness(16, 14, 16, 14),
                CornerRadius = new CornerRadius(8),
                Content = cardGrid
            };
            cardBtn.Click += async (_, _) => await _player.PlayStationAsync(capturedId);
            AutomationProperties.SetName(cardBtn, displayName);
            ToolTipService.SetToolTip(cardBtn, station.Tagline);

            _theme.BindInteractiveButton(
                cardBtn,
                () => string.Equals(_player.Current.ActiveStation?.Id, capturedId, StringComparison.OrdinalIgnoreCase));

            StationsGrid.Children.Add(cardBtn);
            _stationVisuals.Add(new StationRowVisual(station, cardBtn, accentPill, titleText, taglineText, metaText, iconBox, iconGlyph));
        }

        ArrangeStationGridLayout();
    }

    private Button CreateIconActionButton(string glyph, string tooltip, bool isEnabled, Func<Task> action)
    {
        var btn = new Button
        {
            Width = 32,
            Height = 32,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(6),
            IsEnabled = isEnabled,
            Content = new FontIcon
            {
                FontFamily = new FontFamily("Segoe Fluent Icons"),
                Glyph = glyph,
                FontSize = 13
            }
        };
        AutomationProperties.SetName(btn, tooltip);
        ToolTipService.SetToolTip(btn, tooltip);
        _theme.BindInteractiveButton(btn, () => false);
        btn.Click += async (_, _) => await action();
        return btn;
    }

    private void ApplyLocalization()
    {
        _lastRenderedCulture = _localization.CurrentCulture;
        StationsHeading.Text = _localization["Radio.StationsHeading"];
        MinimizeTrayLabel.Text = _localization["Radio.MinimizeToTray"];
        PreviewBadgeText.Text = _localization["Radio.PreviewBadge"];

        AddCustomLabel.Text = _localization["Radio.Edit.AddCustom"];
        ExportLabel.Text = _localization["Radio.Edit.Export"];
        ImportLabel.Text = _localization["Radio.Edit.Import"];
        DoneEditingLabel.Text = _localization["Radio.Edit.Done"];
        SelectAllCheckBox.Content = _localization["Radio.Edit.SelectAll"];
        SelectionHintText.Text = _localization["Radio.Edit.SelectionHint"];
        UpdateBulkActionLabels();

        ExportCodeMenuItem.Text = _localization["Radio.Share.ExportCode"];
        ExportFileMenuItem.Text = _localization["Radio.Share.ExportFile"];
        ImportRadiOhFileMenuItem.Text = _localization["Radio.Share.ImportRadiOhFile"];
        ImportCodeMenuItem.Text = _localization["Radio.Share.ImportCode"];
        ImportFileMenuItem.Text = _localization["Radio.Share.ImportFile"];

        SleepFlyoutHeading.Text = _localization["Radio.Sleep.Heading"];
        SleepCustomBox.PlaceholderText = _localization["Radio.Sleep.CustomPlaceholder"];
        SleepCustomStartBtn.Content = _localization["Radio.Sleep.StartCustom"];
        SleepFlyoutCancelBtn.Content = _localization["Radio.Sleep.Cancel"];
        StatsFlyoutHeading.Text = _localization["Radio.Stats.Heading"];

        AutomationProperties.SetName(PrevStationBtn, _localization["Radio.Previous"]);
        ToolTipService.SetToolTip(PrevStationBtn, _localization["Radio.Previous"]);

        AutomationProperties.SetName(NextStationBtn, _localization["Radio.Next"]);
        ToolTipService.SetToolTip(NextStationBtn, _localization["Radio.Next"]);

        AutomationProperties.SetName(VolumeSlider, _localization["Radio.Volume"]);
        ToolTipService.SetToolTip(VolumeSlider, _localization["Radio.Volume"]);

        var editTooltip = _isEditMode ? _localization["Radio.Edit.Done"] : _localization["Radio.Edit.Toggle"];
        AutomationProperties.SetName(EditModeBtn, editTooltip);
        ToolTipService.SetToolTip(EditModeBtn, editTooltip);

        AutomationProperties.SetName(SleepTimerBtn, _localization["Radio.Sleep.Tooltip"]);
        ToolTipService.SetToolTip(SleepTimerBtn, _localization["Radio.Sleep.Tooltip"]);

        AutomationProperties.SetName(StatsBtn, _localization["Radio.Stats.Tooltip"]);
        ToolTipService.SetToolTip(StatsBtn, _localization["Radio.Stats.Tooltip"]);

        AutomationProperties.SetName(MiniPlayerBtn, _localization["Radio.MiniPlayer"]);
        ToolTipService.SetToolTip(MiniPlayerBtn, _localization["Radio.MiniPlayer"]);

        AutomationProperties.SetName(MinimizeTrayBtn, _localization["Radio.MinimizeToTray"]);
        ToolTipService.SetToolTip(MinimizeTrayBtn, _localization["Radio.MinimizeToTray"]);

        AutomationProperties.SetName(SleepCancelInlineBtn, _localization["Radio.Sleep.Cancel"]);
        ToolTipService.SetToolTip(SleepCancelInlineBtn, _localization["Radio.Sleep.Cancel"]);
    }

    private void RenderState(RadioStateSnapshot snapshot)
    {
        var primary = _theme.GetTextPrimaryBrush();
        var secondary = _theme.GetTextSecondaryBrush();
        var accent = _theme.GetAdaptiveAccentBrush();
        var accentTint = _theme.GetAdaptiveAccentTintBrush();

        HeroCard.Background = _theme.GetCardBackgroundBrush();
        HeroCard.BorderBrush = _theme.GetCardBorderBrush();
        EditToolbar.Background = new SolidColorBrush(_theme.IsDarkTheme
            ? Color.FromArgb(0xF2, 0x24, 0x24, 0x24)
            : Color.FromArgb(0xF5, 0xF7, 0xF8, 0xFA));
        EditToolbar.BorderBrush = _theme.GetCardBorderBrush();
        StickyToolbarOverlay.Visibility = _isEditMode ? Visibility.Visible : Visibility.Collapsed;
        EditToolbar.Visibility = _isEditMode ? Visibility.Visible : Visibility.Collapsed;
        EditToolbarAnchor.Visibility = _isEditMode ? Visibility.Visible : Visibility.Collapsed;
        StationsHeading.Foreground = primary;
        UpdateStickyEditToolbar();

        var station = snapshot.ActiveStation;
        var isLiveOrBuffering = snapshot.State is RadioPlaybackState.Playing or RadioPlaybackState.Buffering;

        HeroStationTitle.Text = station?.Name ?? _localization["Radio.NoStationSelected"];
        HeroStationTitle.Foreground = primary;
        HeroIconGlyph.Glyph = station?.IconGlyph ?? "\uE7F6";
        HeroIconGlyph.Foreground = accent;
        HeroIconBox.Background = accentTint;
        HeroIconBox.BorderBrush = accent;

        var (statusLabel, statusBrush) = snapshot.State switch
        {
            RadioPlaybackState.Playing => (_localization["Radio.Status.Live"], _theme.GetSuccessBrush()),
            RadioPlaybackState.Buffering => (_localization["Radio.Status.Buffering"], _theme.GetCautionBrush()),
            RadioPlaybackState.Error => (_localization["Radio.Status.Error"], _theme.GetCriticalBrush()),
            _ => (_localization["Radio.Status.Stopped"], secondary)
        };

        StatusBadgeText.Text = statusLabel;
        StatusBadgeText.Foreground = statusBrush;
        StatusDot.Fill = statusBrush;
        StatusBadge.BorderBrush = statusBrush;
        StatusBadge.Background = _theme.GetCardBackgroundBrush();

        PreviewBadge.Visibility = snapshot.IsPreview ? Visibility.Visible : Visibility.Collapsed;
        PreviewBadge.BorderBrush = accent;
        PreviewBadge.Background = accentTint;
        PreviewBadgeText.Foreground = accent;

        if (snapshot.State == RadioPlaybackState.Error && !string.IsNullOrWhiteSpace(snapshot.ErrorMessage))
        {
            HeroNowPlayingText.Text = snapshot.ErrorMessage;
            HeroNowPlayingText.Foreground = _theme.GetCriticalBrush();
        }
        else if (!string.IsNullOrWhiteSpace(snapshot.NowPlaying))
        {
            HeroNowPlayingText.Text = snapshot.NowPlaying;
            HeroNowPlayingText.Foreground = primary;
        }
        else
        {
            HeroNowPlayingText.Text = string.IsNullOrWhiteSpace(station?.Tagline)
                ? _localization["Radio.SelectStationHint"]
                : station.Tagline;
            HeroNowPlayingText.Foreground = secondary;
        }

        UpdateHeroMetadata(snapshot, _stats.CurrentSessionElapsed);
        UpdateTrackLookupButton(snapshot);
        UpdateSleepTimerUi(_sleepTimer.IsActive ? _sleepTimer.Remaining : null);

        PlayPauseGlyph.Glyph = isLiveOrBuffering ? "\uE769" : "\uE768";
        var playPauseLabel = isLiveOrBuffering ? _localization["Radio.Stop"] : _localization["Radio.Play"];
        AutomationProperties.SetName(PlayPauseBtn, playPauseLabel);
        ToolTipService.SetToolTip(PlayPauseBtn, playPauseLabel);

        MuteGlyph.Glyph = snapshot.IsMuted ? "\uE74F" : "\uE767";
        var muteLabel = snapshot.IsMuted ? _localization["Radio.Unmute"] : _localization["Radio.Mute"];
        AutomationProperties.SetName(MuteBtn, muteLabel);
        ToolTipService.SetToolTip(MuteBtn, muteLabel);
        _lastRenderedMuted = snapshot.IsMuted;

        EditModeGlyph.Glyph = _isEditMode ? "\uE711" : "\uE70F";

        ApplyVolumeUi(snapshot.Volume, snapshot.IsMuted, updateSlider: !_isUpdatingSliderFromInput);

        _theme.RefreshInteractiveButton(PrevStationBtn);
        _theme.RefreshInteractiveButton(PlayPauseBtn);
        _theme.RefreshInteractiveButton(NextStationBtn);
        _theme.RefreshInteractiveButton(MuteBtn);
        _theme.RefreshInteractiveButton(EditModeBtn);
        _theme.RefreshInteractiveButton(SleepTimerBtn);
        _theme.RefreshInteractiveButton(StatsBtn);
        _theme.RefreshInteractiveButton(MiniPlayerBtn);
        _theme.RefreshInteractiveButton(MinimizeTrayBtn);
        _theme.RefreshInteractiveButton(AddCustomBtn);
        _theme.RefreshInteractiveButton(ExportBtn);
        _theme.RefreshInteractiveButton(ImportBtn);
        _theme.RefreshInteractiveButton(DoneEditingBtn);
        _theme.RefreshInteractiveButton(BulkDeleteBtn);
        _theme.RefreshInteractiveButton(SleepCustomStartBtn);
        _theme.RefreshInteractiveButton(SleepFlyoutCancelBtn);

        foreach (var presetBtn in _sleepPresetButtons)
        {
            _theme.RefreshInteractiveButton(presetBtn);
        }

        foreach (var item in _stationVisuals)
        {
            var isSelected = !snapshot.IsPreview &&
                             station is not null &&
                             string.Equals(item.Station.Id, station.Id, StringComparison.OrdinalIgnoreCase);

            if (item.CardButton is not null)
            {
                _theme.RefreshInteractiveButton(item.CardButton);
            }

            item.AccentPill.Background = isSelected ? accent : new SolidColorBrush(Colors.Transparent);
            item.TitleText.Foreground = isSelected ? accent : primary;

            if (item.IconBox is not null && item.IconGlyph is not null)
            {
                item.IconBox.Background = isSelected ? accentTint : _theme.GetCardBackgroundBrush();
                item.IconBox.BorderBrush = isSelected ? accent : _theme.GetCardBorderBrush();
                item.IconGlyph.Foreground = isSelected ? accent : primary;
            }

            if (item.TaglineText is not null)
            {
                item.TaglineText.Foreground = secondary;
            }

            if (item.MetaText is not null)
            {
                if (isSelected)
                {
                    item.MetaText.Text = isLiveOrBuffering
                        ? _localization["Radio.Status.Live"]
                        : _localization["Radio.ActiveBadge"];
                    item.MetaText.Foreground = isLiveOrBuffering ? _theme.GetSuccessBrush() : accent;
                }
                else
                {
                    item.MetaText.Text = item.Station.BitrateKbps > 0
                        ? $"{item.Station.BitrateKbps}k"
                        : item.Station.CountryCode;
                    item.MetaText.Foreground = secondary;
                }
            }
        }

        if (_isEditMode)
        {
            SelectionHintText.Foreground = secondary;
            SyncEditSelectionVisuals();
        }
    }

    private void UpdateHeroMetadata(RadioStateSnapshot snapshot, TimeSpan sessionElapsed)
    {
        var station = snapshot.ActiveStation;
        if (station is null)
        {
            HeroMetaText.Text = string.Empty;
            return;
        }

        var parts = new List<string>
        {
            station.BitrateKbps > 0
                ? _localization.GetString("Radio.LiveStreamLabel", station.BitrateKbps)
                : $"{station.CountryCode} • {station.Codec}"
        };

        if (snapshot.State is RadioPlaybackState.Playing or RadioPlaybackState.Buffering &&
            snapshot.TargetBufferSeconds > 0)
        {
            var bufferedFormatted = Math.Max(0, snapshot.BufferedSeconds).ToString("0.0", System.Globalization.CultureInfo.CurrentUICulture);
            parts.Add(_localization.GetString("Radio.BufferStatus", bufferedFormatted, snapshot.TargetBufferSeconds));
        }

        if (snapshot.State == RadioPlaybackState.Playing && sessionElapsed > TimeSpan.Zero)
        {
            parts.Add(_localization.GetString("Radio.SessionTime", FormatDuration(sessionElapsed)));
        }

        HeroMetaText.Text = string.Join("  •  ", parts);
        HeroMetaText.Foreground = _theme.GetTextSecondaryBrush();
    }

    private void UpdateSleepTimerUi(TimeSpan? remaining)
    {
        if (remaining.HasValue && remaining.Value > TimeSpan.Zero)
        {
            var accent = _theme.GetAdaptiveAccentBrush();
            SleepStatusPill.Visibility = Visibility.Visible;
            SleepStatusPill.BorderBrush = accent;
            SleepStatusPill.Background = _theme.GetAdaptiveAccentTintBrush();
            SleepStatusIcon.Foreground = accent;
            SleepStatusText.Foreground = accent;
            SleepStatusText.Text = FormatDuration(remaining.Value);
            SleepFlyoutCancelBtn.Visibility = Visibility.Visible;
        }
        else
        {
            SleepStatusPill.Visibility = Visibility.Collapsed;
            SleepFlyoutCancelBtn.Visibility = Visibility.Collapsed;
        }

        _theme.RefreshInteractiveButton(SleepTimerBtn);
    }

    private static string FormatDuration(TimeSpan ts)
        => ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours}:{ts.Minutes:00}:{ts.Seconds:00}"
            : $"{ts.Minutes}:{ts.Seconds:00}";

    private void OnHomeSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var singleCol = e.NewSize.Width < 640;
        if (singleCol != _isSingleColumn)
        {
            _isSingleColumn = singleCol;
            ArrangeStationGridLayout();
        }

        var compactTransport = e.NewSize.Width < 700;
        if (compactTransport != _isCompactTransport)
        {
            _isCompactTransport = compactTransport;
            ArrangeTransportLayout();
        }

        MinimizeTrayLabel.Visibility = e.NewSize.Width < 800 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ArrangeStationGridLayout()
    {
        if (StationsGrid.Visibility != Visibility.Visible)
        {
            return;
        }

        StationsGrid.ColumnDefinitions.Clear();
        StationsGrid.RowDefinitions.Clear();

        if (_isSingleColumn)
        {
            StationsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (var i = 0; i < _stationVisuals.Count; i++)
            {
                if (_stationVisuals[i].CardButton is { } btn)
                {
                    StationsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                    Grid.SetRow(btn, i);
                    Grid.SetColumn(btn, 0);
                }
            }
        }
        else
        {
            StationsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            StationsGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var rows = Math.Max(1, (_stationVisuals.Count + 1) / 2);
            for (var r = 0; r < rows; r++)
            {
                StationsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            }

            for (var i = 0; i < _stationVisuals.Count; i++)
            {
                if (_stationVisuals[i].CardButton is { } btn)
                {
                    Grid.SetRow(btn, i / 2);
                    Grid.SetColumn(btn, i % 2);
                }
            }
        }
    }

    private void ArrangeTransportLayout()
    {
        if (_isCompactTransport)
        {
            Grid.SetRow(VolumePanel, 1);
            Grid.SetColumn(VolumePanel, 0);
            Grid.SetColumnSpan(VolumePanel, 2);

            Grid.SetRow(QuickToolsPanel, 0);
            Grid.SetColumn(QuickToolsPanel, 1);
            Grid.SetColumnSpan(QuickToolsPanel, 2);
        }
        else
        {
            Grid.SetRow(VolumePanel, 0);
            Grid.SetColumn(VolumePanel, 1);
            Grid.SetColumnSpan(VolumePanel, 1);

            Grid.SetRow(QuickToolsPanel, 0);
            Grid.SetColumn(QuickToolsPanel, 2);
            Grid.SetColumnSpan(QuickToolsPanel, 1);
        }
    }

    private async Task PromptAddCustomStationAsync()
    {
        if (XamlRoot is null) return;

        var nameBox = new TextBox { PlaceholderText = _localization["Radio.Edit.NamePlaceholder"] };
        var urlBox = new TextBox { PlaceholderText = _localization["Radio.Edit.UrlPlaceholder"] };
        var countryBox = new TextBox { PlaceholderText = "NL", MaxLength = 4 };
        var tagsBox = new TextBox { PlaceholderText = "rock, indie" };
        var errorText = new TextBlock
        {
            Foreground = _theme.GetCriticalBrush(),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed
        };

        var panel = new StackPanel { Spacing = 10, MinWidth = 340 };
        panel.Children.Add(new TextBlock { Text = _localization["Radio.Edit.NameLabel"], FontWeight = FontWeights.SemiBold });
        panel.Children.Add(nameBox);
        panel.Children.Add(new TextBlock { Text = _localization["Radio.Edit.UrlLabel"], FontWeight = FontWeights.SemiBold });
        panel.Children.Add(urlBox);
        panel.Children.Add(new TextBlock { Text = _localization["Radio.Edit.CountryLabel"], FontWeight = FontWeights.SemiBold });
        panel.Children.Add(countryBox);
        panel.Children.Add(new TextBlock { Text = _localization["Radio.Edit.TagsLabel"], FontWeight = FontWeights.SemiBold });
        panel.Children.Add(tagsBox);
        panel.Children.Add(errorText);

        var dialog = new ContentDialog
        {
            Title = _localization["Radio.Edit.AddDialogTitle"],
            PrimaryButtonText = _localization["Common.Add"],
            CloseButtonText = _localization["Common.Cancel"],
            DefaultButton = ContentDialogButton.Primary,
            RequestedTheme = _theme.IsDarkTheme ? ElementTheme.Dark : ElementTheme.Light,
            Content = panel,
            XamlRoot = XamlRoot
        };

        dialog.PrimaryButtonClick += (_, args) =>
        {
            var name = nameBox.Text?.Trim() ?? string.Empty;
            var url = urlBox.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(name) ||
                !Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                args.Cancel = true;
                errorText.Text = _localization["Radio.Edit.ValidationNameUrl"];
                errorText.Visibility = Visibility.Visible;
            }
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            var country = string.IsNullOrWhiteSpace(countryBox.Text) ? "INT" : countryBox.Text.Trim().ToUpperInvariant();
            var tags = tagsBox.Text?.Trim() ?? string.Empty;
            var station = new RadioStation(
                Id: $"custom-{Guid.NewGuid():N}",
                Name: nameBox.Text.Trim(),
                Tagline: string.IsNullOrWhiteSpace(tags) ? $"{country} • Stream" : tags,
                StreamUrl: urlBox.Text.Trim(),
                FallbackStreamUrl: string.Empty,
                IconGlyph: "\uE7F6",
                AccentHex: "#0078D4",
                BitrateKbps: 0,
                CountryCode: country,
                Codec: "STREAM",
                Tags: tags);
            await _player.AddStationAsync(station);
        }
    }

    private async Task PromptRenameStationAsync(RadioStation station)
    {
        if (XamlRoot is null) return;

        var nameBox = new TextBox { Text = station.Name };
        var errorText = new TextBlock
        {
            Foreground = _theme.GetCriticalBrush(),
            FontSize = 12,
            Visibility = Visibility.Collapsed
        };

        var panel = new StackPanel { Spacing = 8, MinWidth = 320 };
        panel.Children.Add(new TextBlock { Text = _localization["Radio.Edit.NameLabel"], FontWeight = FontWeights.SemiBold });
        panel.Children.Add(nameBox);
        panel.Children.Add(errorText);

        var dialog = new ContentDialog
        {
            Title = _localization["Radio.Edit.RenameDialogTitle"],
            PrimaryButtonText = _localization["Common.Save"],
            CloseButtonText = _localization["Common.Cancel"],
            DefaultButton = ContentDialogButton.Primary,
            RequestedTheme = _theme.IsDarkTheme ? ElementTheme.Dark : ElementTheme.Light,
            Content = panel,
            XamlRoot = XamlRoot
        };

        dialog.PrimaryButtonClick += (_, args) =>
        {
            if (string.IsNullOrWhiteSpace(nameBox.Text))
            {
                args.Cancel = true;
                errorText.Text = _localization["Radio.Edit.ValidationName"];
                errorText.Visibility = Visibility.Visible;
            }
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await _player.UpdateStationAsync(station with { Name = nameBox.Text.Trim() });
        }
    }

    private void ShowEditFeedback(string message, bool isError = false)
    {
        EditStatusText.Text = message;
        EditStatusText.Foreground = isError ? _theme.GetCriticalBrush() : _theme.GetSuccessBrush();
        EditStatusText.Visibility = Visibility.Visible;
    }

    private async void PrevStationBtn_Click(object sender, RoutedEventArgs e)
        => await _player.PreviousStationAsync();

    private async void PlayPauseBtn_Click(object sender, RoutedEventArgs e)
        => await _player.TogglePlayPauseAsync();

    private async void NextStationBtn_Click(object sender, RoutedEventArgs e)
        => await _player.NextStationAsync();

    private void UpdateTrackLookupButton(RadioStateSnapshot snapshot)
    {
        var enabled = _settings.GetOption(RadioOptions.TrackLookupEnabled, true);
        var hasTrack = snapshot.State != RadioPlaybackState.Error && !string.IsNullOrWhiteSpace(snapshot.NowPlaying);

        if (!enabled || !hasTrack)
        {
            OpenTrackLookupBtn.Visibility = Visibility.Collapsed;
            return;
        }

        var serviceId = _settings.GetOption(RadioOptions.TrackLookupService, MusicLookupHelper.SpotifyId);
        var serviceName = MusicLookupHelper.GetDisplayName(serviceId);
        var tooltip = _localization.GetString("Radio.TrackLookup.Tooltip", snapshot.NowPlaying!, serviceName);

        OpenTrackLookupLabel.Text = serviceName;
        AutomationProperties.SetName(OpenTrackLookupBtn, tooltip);
        ToolTipService.SetToolTip(OpenTrackLookupBtn, tooltip);
        OpenTrackLookupBtn.Visibility = Visibility.Visible;
        _theme.RefreshInteractiveButton(OpenTrackLookupBtn);
    }

    private void OpenTrackLookupBtn_Click(object sender, RoutedEventArgs e)
    {
        var serviceId = _settings.GetOption(RadioOptions.TrackLookupService, MusicLookupHelper.SpotifyId);
        var url = MusicLookupHelper.BuildSearchUrl(serviceId, _player.Current.NowPlaying);
        if (string.IsNullOrEmpty(url))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // Ignore shell launch errors on systems without an associated browser handler.
        }
    }

    private void MuteBtn_Click(object sender, RoutedEventArgs e)
        => _player.ToggleMute();

    private void ApplyVolumeUi(double volume, bool isMuted, bool updateSlider)
    {
        var percent = (int)Math.Round(volume * 100);
        if (updateSlider && Math.Abs(VolumeSlider.Value - percent) >= 0.5)
        {
            _isSyncingVolume = true;
            VolumeSlider.Value = percent;
            _isSyncingVolume = false;
        }

        VolumeValueText.Text = $"{percent}%";
        VolumeValueText.Foreground = isMuted ? _theme.GetTextDisabledBrush() : _theme.GetTextSecondaryBrush();

        if (_lastRenderedMuted != isMuted)
        {
            _lastRenderedMuted = isMuted;
            MuteGlyph.Glyph = isMuted ? "\uE74F" : "\uE767";
            var muteLabel = isMuted ? _localization["Radio.Unmute"] : _localization["Radio.Mute"];
            AutomationProperties.SetName(MuteBtn, muteLabel);
            ToolTipService.SetToolTip(MuteBtn, muteLabel);
            _theme.RefreshInteractiveButton(MuteBtn);
        }
    }

    private void VolumeSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_isSyncingVolume)
        {
            return;
        }

        _isUpdatingSliderFromInput = true;
        try
        {
            var percent = (int)Math.Round(e.NewValue);
            VolumeValueText.Text = $"{percent}%";
            _player.SetVolume(e.NewValue / 100.0);
        }
        finally
        {
            _isUpdatingSliderFromInput = false;
        }
    }

    private void HomeScrollViewer_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
    {
        if (!_isEditMode || EditToolbar.Visibility != Visibility.Visible || !_isStickyCompositionActive)
        {
            return;
        }

        // During active scrolling (e.IsIntermediate == true), let the Windows Compositor
        // ExpressionAnimation drive 100% of the visual position at screen refresh rate
        // without UI-thread property updates that could desynchronize frames.
        if (!e.IsIntermediate)
        {
            SyncStickyXamlOffset();
        }
    }

    private void StickyLayoutAnchor_SizeChanged(object sender, SizeChangedEventArgs e)
        => UpdateStickyEditToolbar();

    private void EditToolbar_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_isEditMode && e.NewSize.Height > 0 && Math.Abs(EditToolbarAnchor.Height - e.NewSize.Height) > 0.5)
        {
            EditToolbarAnchor.Height = e.NewSize.Height;
        }

        UpdateStickyEditToolbar();
    }

    private void EditToolbar_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (_isEditMode && _isStickyCompositionActive)
        {
            SyncStickyXamlOffset();
        }
    }

    private void EditToolbar_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var delta = e.GetCurrentPoint(this).Properties.MouseWheelDelta;
        if (delta != 0 && HomeScrollViewer.ScrollableHeight > 0)
        {
            var targetOffset = Math.Clamp(
                HomeScrollViewer.VerticalOffset - delta,
                0.0,
                HomeScrollViewer.ScrollableHeight);
            HomeScrollViewer.ChangeView(null, targetOffset, null, disableAnimation: false);
            e.Handled = true;
        }
    }

    private void EnsureStickyCompositionInitialized()
    {
        if (_stickyTranslationAnimation is not null)
        {
            return;
        }

        ElementCompositionPreview.SetIsTranslationEnabled(EditToolbar, true);
        _scrollManipulationProps = ElementCompositionPreview.GetScrollViewerManipulationPropertySet(HomeScrollViewer);
        _editToolbarVisual = ElementCompositionPreview.GetElementVisual(EditToolbar);
        var scrollViewerVisual = ElementCompositionPreview.GetElementVisual(HomeScrollViewer);
        var compositor = _editToolbarVisual.Compositor;

        _stickyTranslationAnimation = compositor.CreateExpressionAnimation(
            "Vector3(0.0f, Max(0.0f, threshold + scroll.Translation.Y) - xamlOffset, 0.0f)");
        _stickyTranslationAnimation.SetReferenceParameter("scroll", _scrollManipulationProps);
        _stickyTranslationAnimation.SetScalarParameter("threshold", 0.0f);
        _stickyTranslationAnimation.SetScalarParameter("xamlOffset", 0.0f);

        _scrollViewerClip = compositor.CreateInsetClip();
        scrollViewerVisual.Clip = _scrollViewerClip;

        _scrollClipAnimation = compositor.CreateExpressionAnimation(
            "Clamp(-scroll.Translation.Y - (threshold + 4.0f), 0.0f, maxClip)");
        _scrollClipAnimation.SetReferenceParameter("scroll", _scrollManipulationProps);
        _scrollClipAnimation.SetScalarParameter("threshold", 0.0f);
        _scrollClipAnimation.SetScalarParameter("maxClip", StickyTopClipInset);
    }

    private void SyncStickyXamlOffset(bool force = false)
    {
        if (_stickyTranslationAnimation is null || _editToolbarVisual is null)
        {
            return;
        }

        var offset = HomeScrollViewer.VerticalOffset;
        var targetXamlOffset = offset < _stickyThreshold ? _stickyThreshold - offset : 0.0;
        if (!force && Math.Abs(_lastSyncedXamlOffset - targetXamlOffset) <= 0.05)
        {
            return;
        }

        _lastSyncedXamlOffset = targetXamlOffset;
        StickyEditTransform.Y = targetXamlOffset;
        _stickyTranslationAnimation.SetScalarParameter("xamlOffset", (float)targetXamlOffset);
        _editToolbarVisual.StartAnimation("Translation", _stickyTranslationAnimation);
    }

    private void UpdateStickyEditToolbar()
    {
        if (!_isEditMode || EditToolbar.Visibility != Visibility.Visible)
        {
            StickyToolbarOverlay.Visibility = Visibility.Collapsed;
            EditToolbarAnchor.Visibility = Visibility.Collapsed;
            EditToolbarAnchor.Height = 0;
            StickyEditTransform.Y = 0;
            _lastSyncedXamlOffset = 0;

            if (_isStickyCompositionActive)
            {
                _editToolbarVisual?.StopAnimation("Translation");
                _editToolbarVisual?.Properties.InsertVector3("Translation", Vector3.Zero);
                _scrollViewerClip?.StopAnimation("TopInset");
                if (_scrollViewerClip is not null)
                {
                    _scrollViewerClip.TopInset = 0.0f;
                }

                _isStickyCompositionActive = false;
            }

            return;
        }

        try
        {
            if (EditToolbar.ActualHeight > 0 && Math.Abs(EditToolbarAnchor.Height - EditToolbar.ActualHeight) > 0.5)
            {
                EditToolbarAnchor.Height = EditToolbar.ActualHeight;
            }

            if (ScrollContentRoot.ActualWidth > 0 && Math.Abs(StickyToolbarOverlay.Width - ScrollContentRoot.ActualWidth) > 0.5)
            {
                StickyToolbarOverlay.Width = ScrollContentRoot.ActualWidth;
                StickyToolbarOverlay.HorizontalAlignment = HorizontalAlignment.Left;
            }

            var headerTop = FavoritesHeaderGrid
                .TransformToVisual(ScrollContentRoot)
                .TransformPoint(new Point(0, 0))
                .Y;
            var anchorTop = headerTop + FavoritesHeaderGrid.ActualHeight + 10.0;
            _stickyThreshold = Math.Max(0.0, anchorTop - StickyTopMargin);

            EnsureStickyCompositionInitialized();
            _stickyTranslationAnimation!.SetScalarParameter("threshold", (float)_stickyThreshold);
            _scrollClipAnimation!.SetScalarParameter("threshold", (float)_stickyThreshold);

            SyncStickyXamlOffset(force: true);
            _scrollViewerClip!.StartAnimation("TopInset", _scrollClipAnimation);
            _isStickyCompositionActive = true;
        }
        catch
        {
            StickyEditTransform.Y = 0;
        }
    }

    private void EditModeBtn_Click(object sender, RoutedEventArgs e)
    {
        _isEditMode = !_isEditMode;
        if (!_isEditMode)
        {
            _selectedStationIds.Clear();
            _lastSelectionAnchorIndex = -1;
            _draggedStationIds.Clear();
        }

        EditStatusText.Visibility = Visibility.Collapsed;
        ApplyLocalization();
        RebuildStationsUi();
        RenderState(_player.Current);
    }

    private static bool IsShiftDown()
        => InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)
            .HasFlag(CoreVirtualKeyStates.Down);

    private void HandleEditItemSelection(string stationId, int index, bool isShiftDown)
    {
        var stations = _player.Stations;
        if (isShiftDown && _lastSelectionAnchorIndex >= 0 && _lastSelectionAnchorIndex < stations.Count)
        {
            var start = Math.Min(_lastSelectionAnchorIndex, index);
            var end = Math.Max(_lastSelectionAnchorIndex, index);
            for (var k = start; k <= end; k++)
            {
                _selectedStationIds.Add(stations[k].Id);
            }
        }
        else
        {
            if (!_selectedStationIds.Remove(stationId))
            {
                _selectedStationIds.Add(stationId);
            }

            _lastSelectionAnchorIndex = index;
        }

        SyncEditSelectionVisuals();
    }

    private void SyncEditSelectionVisuals()
    {
        var accent = _theme.GetAdaptiveAccentBrush();
        var accentTint = _theme.GetAdaptiveAccentTintBrush();
        var cardBg = _theme.GetCardBackgroundBrush();
        var cardBorder = _theme.GetCardBorderBrush();
        var secondary = _theme.GetTextSecondaryBrush();

        foreach (var row in _editRowVisuals)
        {
            var isChecked = _selectedStationIds.Contains(row.StationId);
            row.SelectBox.IsChecked = isChecked;
            row.RowBorder.Background = isChecked ? accentTint : cardBg;
            row.RowBorder.BorderBrush = isChecked ? accent : cardBorder;
            row.GripIcon.Foreground = isChecked ? accent : secondary;
        }

        var total = _editRowVisuals.Count;
        var selectedCount = _selectedStationIds.Count;
        SelectAllCheckBox.IsChecked = total == 0 || selectedCount == 0
            ? false
            : selectedCount == total
                ? true
                : null;

        UpdateBulkActionLabels();
    }

    private void UpdateBulkActionLabels()
    {
        var count = _selectedStationIds.Count;
        BulkDeleteBtn.IsEnabled = count > 0;
        BulkDeleteLabel.Text = count > 0
            ? _localization.GetString("Radio.Edit.BulkDeleteCount", count)
            : _localization["Radio.Edit.BulkDelete"];
        AutomationProperties.SetName(BulkDeleteBtn, BulkDeleteLabel.Text);
        _theme.RefreshInteractiveButton(BulkDeleteBtn);
    }

    private void ShowDropIndicatorForRow(int targetIndex, bool insertAfter)
    {
        var accent = _theme.GetAdaptiveAccentBrush();
        foreach (var row in _editRowVisuals)
        {
            if (row.Index == targetIndex)
            {
                row.TopDropIndicator.Background = accent;
                row.BottomDropIndicator.Background = accent;
                row.TopDropIndicator.Opacity = insertAfter ? 0 : 1;
                row.BottomDropIndicator.Opacity = insertAfter ? 1 : 0;
            }
            else
            {
                row.TopDropIndicator.Opacity = 0;
                row.BottomDropIndicator.Opacity = 0;
            }
        }
    }

    private void ClearAllDropIndicators()
    {
        foreach (var row in _editRowVisuals)
        {
            row.TopDropIndicator.Opacity = 0;
            row.BottomDropIndicator.Opacity = 0;
        }
    }

    private void RestoreAllRowOpacities()
    {
        foreach (var row in _editRowVisuals)
        {
            row.RowBorder.Opacity = 1.0;
        }
    }

    private void SelectAllCheckBox_Click(object sender, RoutedEventArgs e)
    {
        var stations = _player.Stations;
        if (_selectedStationIds.Count == stations.Count && stations.Count > 0)
        {
            _selectedStationIds.Clear();
            _lastSelectionAnchorIndex = -1;
        }
        else
        {
            foreach (var s in stations)
            {
                _selectedStationIds.Add(s.Id);
            }

            _lastSelectionAnchorIndex = stations.Count > 0 ? 0 : -1;
        }

        SyncEditSelectionVisuals();
    }

    private async void BulkDeleteBtn_Click(object sender, RoutedEventArgs e)
    {
        if (XamlRoot is null || _selectedStationIds.Count == 0)
        {
            return;
        }

        var stations = _player.Stations;
        var toDelete = _selectedStationIds.ToList();
        if (stations.Count - toDelete.Count < 1)
        {
            ShowEditFeedback(_localization["Radio.Edit.KeepOneWarning"], isError: true);
            return;
        }

        var dialog = new ContentDialog
        {
            Title = _localization["Radio.Edit.BulkDeleteConfirmTitle"],
            Content = _localization.GetString("Radio.Edit.BulkDeleteConfirmBody", toDelete.Count),
            PrimaryButtonText = _localization.GetString("Radio.Edit.BulkDeleteCount", toDelete.Count),
            CloseButtonText = _localization["Common.Cancel"],
            DefaultButton = ContentDialogButton.Close,
            RequestedTheme = _theme.IsDarkTheme ? ElementTheme.Dark : ElementTheme.Light,
            XamlRoot = XamlRoot
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        _selectedStationIds.Clear();
        _lastSelectionAnchorIndex = -1;
        await _player.RemoveStationsAsync(toDelete);
        ShowEditFeedback(_localization.GetString("Radio.Edit.BulkDeletedFeedback", toDelete.Count));
    }

    private async void AddCustomBtn_Click(object sender, RoutedEventArgs e)
        => await PromptAddCustomStationAsync();

    private void ExportCodeMenuItem_Click(object sender, RoutedEventArgs e)
    {
        var list = _player.Stations.ToList();
        var code = _shareService.ExportToShareCode(list);
        var package = new DataPackage();
        package.SetText(code);
        Clipboard.SetContent(package);
        ShowEditFeedback(_localization.GetString("Radio.Share.CopiedCode", list.Count));
    }

    private async void ExportFileMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (MainWindow.Instance is null) return;

        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = $"{AppVersion.EntryAssemblyName.ToLowerInvariant()}-favorites"
        };
        picker.FileTypeChoices.Add(_localization["Radio.Share.PackageFileType"], new List<string> { ".chassispack" });
        InitializeWithWindow.Initialize(picker, MainWindow.Instance.Hwnd);

        var file = await picker.PickSaveFileAsync();
        if (file is null) return;

        await _shareService.ExportToFileAsync(_player.Stations.ToList(), file.Path);
        ShowEditFeedback(_localization["Radio.Share.ExportedFile"]);
    }

    private async void ImportCodeMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (XamlRoot is null) return;

        var codeBox = new TextBox
        {
            PlaceholderText = _localization["Radio.Share.CodePlaceholder"],
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 96,
            MaxHeight = 220
        };

        var panel = new StackPanel { Spacing = 10, MinWidth = 380 };
        panel.Children.Add(codeBox);

        var dialog = new ContentDialog
        {
            Title = _localization["Radio.Share.ImportTitle"],
            PrimaryButtonText = _localization["Radio.Edit.Import"],
            CloseButtonText = _localization["Common.Cancel"],
            DefaultButton = ContentDialogButton.Primary,
            RequestedTheme = _theme.IsDarkTheme ? ElementTheme.Dark : ElementTheme.Light,
            Content = panel,
            XamlRoot = XamlRoot
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        try
        {
            var imported = RadioBackupImporter.ParseAny(codeBox.Text, _shareService);
            await ConfirmAndAppendImportedStationsAsync(imported);
        }
        catch
        {
            ShowEditFeedback(_localization["Radio.Share.InvalidInput"], isError: true);
        }
    }

    private async void ImportRadiOhFileMenuItem_Click(object sender, RoutedEventArgs e)
        => await PickAndImportFileAsync(new[] { ".json", ".txt" });

    private async void ImportFileMenuItem_Click(object sender, RoutedEventArgs e)
        => await PickAndImportFileAsync(new[] { ".chassispack", ".json" });

    private async Task PickAndImportFileAsync(IEnumerable<string> extensions)
    {
        if (MainWindow.Instance is null) return;

        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary
        };
        foreach (var ext in extensions)
        {
            picker.FileTypeFilter.Add(ext);
        }
        InitializeWithWindow.Initialize(picker, MainWindow.Instance.Hwnd);

        var file = await picker.PickSingleFileAsync();
        if (file is null) return;

        try
        {
            var rawText = await File.ReadAllTextAsync(file.Path);
            var imported = RadioBackupImporter.ParseAny(rawText, _shareService);
            await ConfirmAndAppendImportedStationsAsync(imported);
        }
        catch
        {
            ShowEditFeedback(_localization["Radio.Share.InvalidInput"], isError: true);
        }
    }

    private async Task ConfirmAndAppendImportedStationsAsync(IReadOnlyList<RadioStation> parsed)
    {
        if (XamlRoot is null) return;

        var existingUrls = new HashSet<string>(_player.Stations.Select(s => s.StreamUrl), StringComparer.OrdinalIgnoreCase);
        var newStations = new List<RadioStation>();
        foreach (var station in parsed)
        {
            if (existingUrls.Add(station.StreamUrl))
            {
                newStations.Add(station);
            }
        }

        if (newStations.Count == 0)
        {
            ShowEditFeedback(_localization["Radio.Share.AllAlreadyPresent"]);
            return;
        }

        var skippedCount = parsed.Count - newStations.Count;
        var summaryText = skippedCount > 0
            ? _localization.GetString("Radio.Share.ConfirmBodyWithSkipped", newStations.Count, skippedCount)
            : _localization.GetString("Radio.Share.ConfirmBody", newStations.Count);

        var previewLines = newStations
            .Take(6)
            .Select(s => $"• {s.Name}")
            .ToList();
        if (newStations.Count > 6)
        {
            previewLines.Add(_localization.GetString("Radio.Share.ConfirmMore", newStations.Count - 6));
        }

        var bodyStack = new StackPanel { Spacing = 10, MinWidth = 360 };
        bodyStack.Children.Add(new TextBlock
        {
            Text = summaryText,
            TextWrapping = TextWrapping.Wrap,
            Foreground = _theme.GetTextPrimaryBrush()
        });
        bodyStack.Children.Add(new TextBlock
        {
            Text = string.Join(Environment.NewLine, previewLines),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = _theme.GetTextSecondaryBrush()
        });

        var confirmDialog = new ContentDialog
        {
            Title = _localization["Radio.Share.ConfirmTitle"],
            PrimaryButtonText = _localization.GetString("Radio.Share.ConfirmAddButton", newStations.Count),
            CloseButtonText = _localization["Common.Cancel"],
            DefaultButton = ContentDialogButton.Primary,
            RequestedTheme = _theme.IsDarkTheme ? ElementTheme.Dark : ElementTheme.Light,
            Content = bodyStack,
            XamlRoot = XamlRoot
        };

        if (await confirmDialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        await _player.ImportStationsAsync(newStations, replaceExisting: false);
        ShowEditFeedback(_localization.GetString("Radio.Share.ImportedCount", newStations.Count));
    }

    private void SleepTimerFlyout_Opening(object sender, object e)
    {
        SleepFlyoutHeading.Foreground = _theme.GetTextPrimaryBrush();
        SleepFlyoutCancelBtn.Visibility = _sleepTimer.IsActive ? Visibility.Visible : Visibility.Collapsed;
        foreach (var btn in _sleepPresetButtons)
        {
            _theme.RefreshInteractiveButton(btn);
        }
        _theme.RefreshInteractiveButton(SleepCustomStartBtn);
        _theme.RefreshInteractiveButton(SleepFlyoutCancelBtn);
    }

    private void SleepCustomStartBtn_Click(object sender, RoutedEventArgs e)
    {
        if (int.TryParse(SleepCustomBox.Text?.Trim(), out var minutes) && minutes > 0)
        {
            _sleepTimer.Start(TimeSpan.FromMinutes(minutes));
            SleepCustomBox.Text = string.Empty;
            SleepTimerFlyout.Hide();
        }
    }

    private void SleepFlyoutCancelBtn_Click(object sender, RoutedEventArgs e)
    {
        _sleepTimer.Cancel();
        SleepTimerFlyout.Hide();
    }

    private void SleepCancelInlineBtn_Click(object sender, RoutedEventArgs e)
        => _sleepTimer.Cancel();

    private void StatsFlyout_Opening(object sender, object e)
    {
        StatsFlyoutHeading.Foreground = _theme.GetTextPrimaryBrush();
        StatsTotalText.Foreground = _theme.GetAdaptiveAccentBrush();
        StatsListPanel.Children.Clear();

        var items = _stats.GetStats();
        if (items.Count == 0)
        {
            StatsListPanel.Children.Add(new TextBlock
            {
                Text = _localization["Radio.Stats.Empty"],
                FontSize = 12,
                Foreground = _theme.GetTextSecondaryBrush()
            });
            StatsTotalText.Text = string.Empty;
            return;
        }

        long totalSeconds = 0;
        foreach (var stat in items.Take(12))
        {
            totalSeconds += stat.TotalSeconds;
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var nameBlock = new TextBlock
            {
                Text = stat.StationName,
                FontSize = 13,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = _theme.GetTextPrimaryBrush()
            };
            var timeBlock = new TextBlock
            {
                Text = FormatDuration(TimeSpan.FromSeconds(stat.TotalSeconds)),
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = _theme.GetTextSecondaryBrush()
            };

            Grid.SetColumn(nameBlock, 0);
            Grid.SetColumn(timeBlock, 1);
            row.Children.Add(nameBlock);
            row.Children.Add(timeBlock);
            StatsListPanel.Children.Add(row);
        }

        StatsTotalText.Text = _localization.GetString("Radio.Stats.Total", FormatDuration(TimeSpan.FromSeconds(totalSeconds)));
    }

    private void MiniPlayerBtn_Click(object sender, RoutedEventArgs e)
        => MainWindow.Instance?.ToggleMiniPlayer();

    private void MinimizeTrayBtn_Click(object sender, RoutedEventArgs e)
        => MainWindow.Instance?.CollapseToTray(showNotification: true);
}
