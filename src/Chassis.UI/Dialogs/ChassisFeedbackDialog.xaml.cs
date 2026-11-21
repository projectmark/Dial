using Chassis.Core.Feedback;
using Chassis.Core.Localization;
using Chassis.UI.Theme;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace Chassis.UI.Dialogs;

/// <summary>
/// Layer 2 Acrylic feedback and bug-report dialog writing diagnostic bundles into %AppData%/&lt;App&gt;/feedback/.
/// </summary>
public sealed partial class ChassisFeedbackDialog : ContentDialog
{
    private readonly IFeedbackService _feedback;
    private readonly ILocalizationService _localization;
    private readonly ThemeManager _theme;
    private readonly IntPtr _ownerHwnd;
    private string? _attachmentPath;

    public ChassisFeedbackDialog(
        IFeedbackService feedback,
        ILocalizationService localization,
        ThemeManager theme,
        IntPtr ownerHwnd)
    {
        _feedback = feedback;
        _localization = localization;
        _theme = theme;
        _ownerHwnd = ownerHwnd;

        InitializeComponent();
        ApplyLocalizationAndTheme();
    }

    public string? SavedReportPath { get; private set; }

    private void ApplyLocalizationAndTheme()
    {
        RequestedTheme = _theme.IsDarkTheme ? ElementTheme.Dark : ElementTheme.Light;

        Title = _localization["Feedback.Title"];
        PrimaryButtonText = _localization["Feedback.Submit"];
        CloseButtonText = _localization["Common.Cancel"];
        DefaultButton = ContentDialogButton.Primary;

        SubtitleBlock.Text = _localization["Feedback.Subtitle"];
        SubtitleBlock.Foreground = _theme.GetTextSecondaryBrush();

        CategoryLabelBlock.Text = _localization["Feedback.Category"];
        CatGeneralItem.Content = _localization["Feedback.Category.General"];
        CatBugItem.Content = _localization["Feedback.Category.Bug"];
        CatFeatureItem.Content = _localization["Feedback.Category.Feature"];

        SubjectLabelBlock.Text = _localization["Feedback.Subject"];
        SubjectBox.PlaceholderText = _localization["Feedback.SubjectPlaceholder"];
        AutomationProperties.SetName(SubjectBox, _localization["Feedback.Subject"]);

        DescriptionLabelBlock.Text = _localization["Feedback.Description"];
        DescriptionBox.PlaceholderText = _localization["Feedback.DescriptionPlaceholder"];
        AutomationProperties.SetName(DescriptionBox, _localization["Feedback.Description"]);

        DiagnosticsCheckBox.Content = _localization["Feedback.IncludeDiagnostics"];
        AttachButton.Content = _localization["Feedback.AttachFile"];
        AttachmentNameBlock.Foreground = _theme.GetTextSecondaryBrush();
        ValidationBlock.Foreground = _theme.GetCriticalBrush();

        _theme.BindInteractiveButton(AttachButton, () => false);
    }

    private async void AttachButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary
        };
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, _ownerHwnd);

        var file = await picker.PickSingleFileAsync();
        if (file is not null)
        {
            _attachmentPath = file.Path;
            AttachmentNameBlock.Text = file.Name;
        }
    }

    private async void ContentDialog_PrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var deferral = args.GetDeferral();
        try
        {
            var subject = SubjectBox.Text?.Trim() ?? string.Empty;
            var description = DescriptionBox.Text?.Trim() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(subject) || string.IsNullOrWhiteSpace(description))
            {
                ValidationBlock.Text = _localization["Feedback.ValidationRequired"];
                ValidationBlock.Visibility = Visibility.Visible;
                args.Cancel = true;
                return;
            }

            var selectedCategory = (CategoryCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "General";
            SavedReportPath = await _feedback.SaveFeedbackAsync(
                selectedCategory,
                subject,
                description,
                DiagnosticsCheckBox.IsChecked == true,
                _attachmentPath);
        }
        finally
        {
            deferral.Complete();
        }
    }
}
