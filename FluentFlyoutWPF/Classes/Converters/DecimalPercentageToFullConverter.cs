// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.Windows.Data;

namespace FluentFlyoutWPF.Classes.Converters;

public class DecimalPercentageToFullConverter : IValueConverter
{
    // Convert decimal percentage (0.5) to full percentage (50)
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (TryToDouble(value, out double number))
        {
            return (int)Math.Round(number * 100);
        }

        return 0;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (!TryToDouble(value, out double number))
        {
            return 0f;
        }

        double normalized = number / 100.0;
        if (targetType == typeof(double))
            return normalized;
        if (targetType == typeof(decimal))
            return (decimal)normalized;
        if (targetType == typeof(int))
            return (int)Math.Round(normalized);
        return (float)normalized;
    }

    private static bool TryToDouble(object? value, out double result)
    {
        switch (value)
        {
            case float f:
                result = f;
                return true;
            case double d:
                result = d;
                return true;
            case int i:
                result = i;
                return true;
            case decimal m:
                result = (double)m;
                return true;
            default:
                result = 0;
                return false;
        }
    }
}