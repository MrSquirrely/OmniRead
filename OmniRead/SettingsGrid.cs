using System.ComponentModel;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;

using HandyControl.Controls;

using OmniRead.Localization;
using OmniRead.Services;

namespace OmniRead;

/// <summary>
/// The reading settings as a HandyControls property grid. The stock grid sorts headings and properties by name;
/// this one keeps the order given by <see cref="SettingOrderAttribute"/>.
/// </summary>
public sealed class SettingsGrid : PropertyGrid {
    public override void OnApplyTemplate() {
        base.OnApplyTemplate();
        Reorder();
    }

    protected override void OnSelectedObjectChanged(object oldValue, object newValue) {
        base.OnSelectedObjectChanged(oldValue, newValue);
        Reorder();
    }

    /// <summary>Numbers the rows in their intended order and sorts by that number; headings follow their first row.</summary>
    private void Reorder() {
        if ((GetTemplateChild("PART_ItemsControl") is not ItemsControl list) || (list.ItemsSource is not ICollectionView view)) {
            return;
        }
        foreach (PropertyItem item in view.OfType<PropertyItem>()) {
            item.Tag = SettingsEditors.Attribute<SettingOrderAttribute>(item)?.Order ?? int.MaxValue;
        }
        using (view.DeferRefresh()) {
            view.SortDescriptions.Clear();
            view.SortDescriptions.Add(new SortDescription(nameof(Tag), ListSortDirection.Ascending));
        }
    }
}

/// <summary>What the editors below need from the window: the fonts on offer and the voices installed.</summary>
public static class SettingsEditors {
    public static IReadOnlyList<FontChoice> Fonts { get; set; } = [];

    public static Func<IReadOnlyList<NarratorVoice>> Voices { get; set; } = static () => [];

    /// <summary>An attribute of the setting a grid row stands for. The grid itself does not pass these on.</summary>
    public static T? Attribute<T>(PropertyItem item) where T : Attribute {
        return item.Value?.GetType().GetProperty(item.PropertyName ?? "")?.GetCustomAttribute<T>();
    }
}

/// <summary>One entry of a drop-down: the value stored in the settings and the text shown for it.</summary>
public sealed record Choice(object Value, string Label);

/// <summary>A slider with its value written beside it, for the numeric settings.</summary>
public sealed class SliderEditor : PropertyEditorBase {
    public override FrameworkElement CreateElement(PropertyItem propertyItem) {
        SettingRangeAttribute range = SettingsEditors.Attribute<SettingRangeAttribute>(propertyItem)
            ?? new SettingRangeAttribute(0, 100, 1, "Format_Ratio");
        Slider slider = new() {
            Minimum = range.Minimum,
            Maximum = range.Maximum,
            SmallChange = range.Step,
            LargeChange = range.Step * 5,
            TickFrequency = range.Step,
            IsSnapToTickEnabled = true,
            VerticalAlignment = VerticalAlignment.Center
        };
        TextBlock value = new() { MinWidth = 78, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        value.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        value.SetBinding(TextBlock.TextProperty, new Binding(nameof(Slider.Value)) {
            Source = slider,
            Converter = new FormatConverter(),
            ConverterParameter = range.Format
        });
        DockPanel panel = new() { Tag = slider };
        DockPanel.SetDock(value, Dock.Right);
        panel.Children.Add(value);
        panel.Children.Add(slider);
        return panel;
    }

    public override void CreateBinding(PropertyItem propertyItem, DependencyObject element) {
        // The value is bound on the slider inside the panel, not on the panel that was handed back.
        base.CreateBinding(propertyItem, (Slider)((DockPanel)element).Tag);
    }

    public override DependencyProperty GetDependencyProperty() {
        return RangeBase.ValueProperty;
    }
}

/// <summary>A drop-down whose entries are given by the derived class, with texts in the reader's language.</summary>
public abstract class ChoiceEditor : PropertyEditorBase {
    protected abstract IEnumerable<Choice> Choices();

    public override FrameworkElement CreateElement(PropertyItem propertyItem) {
        return new System.Windows.Controls.ComboBox {
            ItemsSource = Choices().ToList(),
            DisplayMemberPath = nameof(Choice.Label),
            SelectedValuePath = nameof(Choice.Value)
        };
    }

    public override DependencyProperty GetDependencyProperty() {
        return Selector.SelectedValueProperty;
    }
}

public sealed class ViewModeEditor : ChoiceEditor {
    protected override IEnumerable<Choice> Choices() {
        return [
            new Choice(ViewMode.Paged, Localizer.Get("Settings_View_Pages")),
            new Choice(ViewMode.Scroll, Localizer.Get("Settings_View_Scrolling"))
        ];
    }
}

public sealed class ColumnsEditor : ChoiceEditor {
    protected override IEnumerable<Choice> Choices() {
        return [
            new Choice(ColumnMode.Auto, Localizer.Get("Settings_Columns_Auto")),
            new Choice(ColumnMode.One, Localizer.Get("Settings_Columns_One")),
            new Choice(ColumnMode.Two, Localizer.Get("Settings_Columns_Two"))
        ];
    }
}

public sealed class ThemeEditor : ChoiceEditor {
    protected override IEnumerable<Choice> Choices() {
        return [
            new Choice(AppTheme.System, Localizer.Get("Settings_Theme_System")),
            new Choice(AppTheme.Light, Localizer.Get("Settings_Theme_Light")),
            new Choice(AppTheme.Dark, Localizer.Get("Settings_Theme_Dark"))
        ];
    }
}

public sealed class LanguageEditor : ChoiceEditor {
    protected override IEnumerable<Choice> Choices() {
        return Localizer.Languages.Select(static language =>
            new Choice(language.Code, (language.Code.Length == 0) ? Localizer.Get("Settings_Language_System") : language.Name));
    }
}

public sealed class VoiceEditor : ChoiceEditor {
    protected override IEnumerable<Choice> Choices() {
        return SettingsEditors.Voices().Select(static voice => new Choice(voice.Id, voice.Name))
            .Prepend(new Choice("", Localizer.Get("Settings_Voice_Default")));
    }
}

/// <summary>The eye-comfort profiles, each shown with a swatch of its page and ink.</summary>
public sealed class ProfileEditor : PropertyEditorBase {
    public override FrameworkElement CreateElement(PropertyItem propertyItem) {
        return new System.Windows.Controls.ComboBox {
            ItemsSource = EyeProfile.All,
            SelectedValuePath = nameof(EyeProfile.Id),
            ItemTemplate = (DataTemplate)Application.Current.FindResource("ProfileItemTemplate")
        };
    }

    public override DependencyProperty GetDependencyProperty() {
        return Selector.SelectedValueProperty;
    }
}

/// <summary>
/// The font list, each font shown in itself. A font is known by its name and where it comes from, so choosing
/// one sets two settings and nothing is bound in the usual way.
/// </summary>
public sealed class FontEditor : PropertyEditorBase {
    public override FrameworkElement CreateElement(PropertyItem propertyItem) {
        ReaderSettings settings = (ReaderSettings)propertyItem.Value;
        System.Windows.Controls.ComboBox list = new() {
            ItemsSource = SettingsEditors.Fonts,
            ItemTemplate = (DataTemplate)Application.Current.FindResource("FontItemTemplate"),
            MaxDropDownHeight = 480
        };
        VirtualizingPanel.SetIsVirtualizing(list, true);
        if (SettingsEditors.Fonts.Count > 0) {
            list.SelectedItem = FontLibrary.Find(SettingsEditors.Fonts, settings.FontName, settings.FontOrigin);
        }
        list.SelectionChanged += (_, _) => {
            if (list.SelectedItem is FontChoice choice) {
                settings.FontOrigin = choice.Origin;
                settings.FontName = choice.Name;
            }
        };
        return list;
    }

    public override void CreateBinding(PropertyItem propertyItem, DependencyObject element) {
        // Nothing to bind: the selection handler above writes both settings.
    }

    public override DependencyProperty GetDependencyProperty() {
        return Selector.SelectedItemProperty;
    }
}