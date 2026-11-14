// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using FluentFlyout.Classes.Settings;
using System.Collections.Concurrent;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace FluentFlyout.Classes.Utils
{
    internal static class StringWidth
    {
        private const string DefaultFontFamily = "Segoe UI Variable, Microsoft YaHei UI, Yu Gothic UI, Malgun Gothic";
        private static readonly ConcurrentDictionary<(string FontFamily, int FontWeight), Typeface> _cachedTypefaces = new();

        /// <summary>
        /// Gets the width of the specified string when rendered with the specified font weight.
        /// </summary>
        /// <param name="text">Text to measure</param>
        /// <param name="fontWeight">Weight of the font. Defaults to 500 (Medium)</param>
        /// <param name="fontSize">Size of the font in device-independent units (pixels). Defaults to 14.</param>
        /// <param name="pixelsPerDip">Pixels per device-independent pixel scale factor. Defaults to 1.0.</param>
        /// <returns>The width of the specified text, in device-independent units (pixels), including a small padding.</returns>
        public static double GetStringWidth(string? text, int fontWeight = 500, int fontSize = 14, double pixelsPerDip = 1.0)
        {
            if (string.IsNullOrEmpty(text) || fontSize <= 0) return 0;

            if (pixelsPerDip <= 0 || double.IsNaN(pixelsPerDip) || double.IsInfinity(pixelsPerDip))
            {
                pixelsPerDip = 1.0;
            }

            var formattedText = new FormattedText(
                text,
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                GetCurrentTypeface(fontWeight),
                fontSize,
                Brushes.Black,
                null,
                pixelsPerDip);

            return formattedText.Width;
        }

        // Returns the current Typeface based on the font family and weight, caching it for performance.
        private static Typeface GetCurrentTypeface(int fontWeight)
        {
            string currentFontFamily = string.IsNullOrWhiteSpace(SettingsManager.Current.FontFamily)
                ? DefaultFontFamily
                : SettingsManager.Current.FontFamily;
            int clampedWeight = Math.Clamp(fontWeight, 1, 999);

            return _cachedTypefaces.GetOrAdd((currentFontFamily, clampedWeight), static key =>
            {
                var fontFamily = new FontFamily(key.FontFamily);
                return new Typeface(fontFamily, FontStyles.Normal, FontWeight.FromOpenTypeWeight(key.FontWeight), FontStretches.Normal);
            });
        }
    }
}