// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.Windows.Data;
using Wpf.Ui.Controls;

namespace FluentFlyoutWPF.Classes.Converters;

public class VolumeToSpeakerSymbolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        double? volume = value switch
        {
            float f => f,
            double d => d,
            int i => i,
            decimal m => (double)m,
            _ => null
        };

        if (volume.HasValue)
        {
            return volume.Value switch
            {
                < 0.10 => SymbolRegular.Speaker028,
                < 0.50 => SymbolRegular.Speaker128,
                _ => SymbolRegular.Speaker228
            };
        }

        return SymbolRegular.Speaker228;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}