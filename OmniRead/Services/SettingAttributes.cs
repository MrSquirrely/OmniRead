using System.ComponentModel;

using OmniRead.Localization;

namespace OmniRead.Services;

/// <summary>Puts a setting under a heading in the settings grid; the heading's text comes from the string resources.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class SettingCategoryAttribute(string key) : CategoryAttribute(key) {
    protected override string GetLocalizedString(string value) {
        return Localizer.Get(value);
    }
}

/// <summary>The setting's label in the settings grid, taken from the string resources.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class SettingNameAttribute(string key) : DisplayNameAttribute {
    public override string DisplayName => Localizer.Get(key);
}

/// <summary>A sentence of help shown under the grid while the setting is selected.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class SettingHelpAttribute(string key) : DescriptionAttribute {
    public override string Description => Localizer.Get(key);
}

/// <summary>Where the setting stands in the grid. The grid would otherwise sort headings and settings by name.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class SettingOrderAttribute(int order) : Attribute {
    public int Order { get; } = order;
}

/// <summary>Limits and step of a numeric setting's slider, and the resource key of the format its value is shown in.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class SettingRangeAttribute(double minimum, double maximum, double step, string format) : Attribute {
    public double Minimum { get; } = minimum;

    public double Maximum { get; } = maximum;

    public double Step { get; } = step;

    public string Format { get; } = format;
}