using System.Globalization;
using Avalonia.Media;

namespace ScimStudio.App.Controls;

/// <summary>
/// The interface's icons, as stroked paths on a 24-unit grid in the manner of Lucide (ISC licence). Kept as code rather than resources, so a
/// view model can hand one to the navigation and XAML can name one with <c>x:Static</c>, from the same place.
/// </summary>
public static class Icons {
    public static Geometry User { get; } = Parse($"M19 21v-2a4 4 0 0 0-4-4H9a4 4 0 0 0-4 4v2 {Circle(12, 7, 4)}");

    public static Geometry Users { get; } = Parse(
        $"M16 21v-2a4 4 0 0 0-4-4H6a4 4 0 0 0-4 4v2 {Circle(9, 7, 4)} M22 21v-2a4 4 0 0 0-3-3.87 M16 3.13a4 4 0 0 1 0 7.75");

    public static Geometry UserPlus { get; } = Parse($"M16 21v-2a4 4 0 0 0-4-4H6a4 4 0 0 0-4 4v2 {Circle(9, 7, 4)} M19 8v6 M22 11h-6");

    public static Geometry UserMinus { get; } = Parse($"M16 21v-2a4 4 0 0 0-4-4H6a4 4 0 0 0-4 4v2 {Circle(9, 7, 4)} M22 11h-6");

    public static Geometry Server { get; } = Parse($"{Rect(2, 2, 20, 8, 2)} {Rect(2, 14, 20, 8, 2)} M6 6h.01 M6 18h.01");

    public static Geometry ShieldCheck { get; } = Parse(
        "M20 13c0 5-3.5 7.5-7.66 8.95a1 1 0 0 1-.67-.01C7.5 20.5 4 18 4 13V6a1 1 0 0 1 1-1c2 0 4.5-1.2 6.24-2.72a1.17 1.17 0 0 1 1.52 0"
        + "C14.51 3.81 17 5 19 5a1 1 0 0 1 1 1z M9 12l2 2 4-4");

    public static Geometry Sparkles { get; } = Parse(
        "M9.94 15.5A2 2 0 0 0 8.5 14.06l-6.14-1.58a.5.5 0 0 1 0-.96L8.5 9.94A2 2 0 0 0 9.94 8.5l1.58-6.14a.5.5 0 0 1 .96 0L14.06 8.5"
        + "A2 2 0 0 0 15.5 9.94l6.14 1.58a.5.5 0 0 1 0 .96L15.5 14.06a2 2 0 0 0-1.44 1.44l-1.58 6.14a.5.5 0 0 1-.96 0z M20 3v4 M22 5h-4");

    public static Geometry Activity { get; } = Parse("M22 12h-4l-3 9L9 3l-3 9H2");

    public static Geometry Sliders { get; } = Parse("M21 4h-7 M10 4H3 M21 12h-9 M8 12H3 M21 20h-5 M12 20H3 M14 2v4 M8 10v4 M16 18v4");

    public static Geometry Plus { get; } = Parse("M5 12h14 M12 5v14");

    public static Geometry Trash { get; } = Parse("M3 6h18 M19 6v14c0 1-1 2-2 2H7c-1 0-2-1-2-2V6 M8 6V4c0-1 1-2 2-2h4c1 0 2 1 2 2v2");

    public static Geometry Refresh { get; } = Parse(
        "M3 12a9 9 0 0 1 9-9 9.75 9.75 0 0 1 6.74 2.74L21 8 M21 3v5h-5 M21 12a9 9 0 0 1-9 9 9.75 9.75 0 0 1-6.74-2.74L3 16 M8 16H3v5");

    public static Geometry Copy { get; } = Parse($"{Rect(8, 8, 14, 14, 2)} M4 16c-1.1 0-2-.9-2-2V4c0-1.1.9-2 2-2h10c1.1 0 2 .9 2 2");

    public static Geometry Download { get; } = Parse("M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4 M7 10l5 5 5-5 M12 15V3");

    public static Geometry Play { get; } = Parse("M6 3l14 9-14 9z");

    public static Geometry Stop { get; } = Parse(Rect(5, 5, 14, 14, 2));

    public static Geometry Search { get; } = Parse($"{Circle(11, 11, 8)} M21 21l-4.3-4.3");

    public static Geometry Close { get; } = Parse("M18 6L6 18 M6 6l12 12");

    public static Geometry Check { get; } = Parse("M20 6L9 17l-5-5");

    public static Geometry Alert { get; } = Parse("M21.73 18l-8-14a2 2 0 0 0-3.48 0l-8 14A2 2 0 0 0 4 21h16a2 2 0 0 0 1.73-3 M12 9v4 M12 17h.01");

    public static Geometry Info { get; } = Parse($"{Circle(12, 12, 10)} M12 16v-4 M12 8h.01");

    public static Geometry Minus { get; } = Parse("M5 12h14");

    public static Geometry Ban { get; } = Parse($"{Circle(12, 12, 10)} M4.93 4.93l14.14 14.14");

    public static Geometry Dot { get; } = Parse(Circle(12, 12, 4));

    public static Geometry Link { get; } = Parse(
        "M10 13a5 5 0 0 0 7.54.54l3-3a5 5 0 0 0-7.07-7.07l-1.72 1.71 M14 11a5 5 0 0 0-7.54-.54l-3 3a5 5 0 0 0 7.07 7.07l1.71-1.71");

    public static Geometry LogOut { get; } = Parse("M9 21H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h4 M16 17l5-5-5-5 M21 12H9");

    public static Geometry Power { get; } = Parse("M12 2v10 M18.4 6.6a9 9 0 1 1-12.77.04");

    public static Geometry ChevronRight { get; } = Parse("M9 18l6-6-6-6");

    public static Geometry Braces { get; } = Parse(
        "M8 3H7a2 2 0 0 0-2 2v5a2 2 0 0 1-2 2 2 2 0 0 1 2 2v5c0 1.1.9 2 2 2h1 M16 21h1a2 2 0 0 0 2-2v-5c0-1.1.9-2 2-2a2 2 0 0 1-2-2V5"
        + "a2 2 0 0 0-2-2h-1");

    public static Geometry Terminal { get; } = Parse("M4 17l6-6-6-6 M12 19h8");

    public static Geometry Flask { get; } = Parse(
        "M10 2v7.53a2 2 0 0 1-.21.9L4.72 20.55a1 1 0 0 0 .9 1.45h12.76a1 1 0 0 0 .9-1.45l-5.07-10.13A2 2 0 0 1 14 9.53V2 M8.5 2h7 M7 16h10");

    public static Geometry Sun { get; } = Parse(
        $"{Circle(12, 12, 4)} M12 2v2 M12 20v2 M4.93 4.93l1.41 1.41 M17.66 17.66l1.41 1.41 M2 12h2 M20 12h2 M6.34 17.66l-1.41 1.41 "
        + "M19.07 4.93l-1.41 1.41");

    public static Geometry Moon { get; } = Parse("M12 3a6 6 0 0 0 9 9 9 9 0 1 1-9-9z");

    public static Geometry Monitor { get; } = Parse($"{Rect(2, 3, 20, 14, 2)} M8 21h8 M12 17v4");

    public static Geometry Globe { get; } = Parse($"{Circle(12, 12, 10)} M12 2a14.5 14.5 0 0 0 0 20 14.5 14.5 0 0 0 0-20 M2 12h20");

    public static Geometry Eye { get; } = Parse(
        $"M2.06 12.35a1 1 0 0 1 0-.7 10.75 10.75 0 0 1 19.88 0 1 1 0 0 1 0 .7 10.75 10.75 0 0 1-19.88 0 {Circle(12, 12, 3)}");

    public static Geometry Shuffle { get; } = Parse(
        "M18 14l4 4-4 4 M18 2l4 4-4 4 M2 18h1.97a4 4 0 0 0 3.3-1.7l5.45-8.6a4 4 0 0 1 3.3-1.7H22 M2 6h1.97a4 4 0 0 1 3.27 1.7 "
        + "M22 18h-6.04a4 4 0 0 1-3.3-1.8l-.36-.45");

    public static Geometry Undo { get; } = Parse("M3 7v6h6 M21 17a9 9 0 0 0-9-9 9 9 0 0 0-6 2.3L3 13");

    public static Geometry Save { get; } = Parse(
        "M15.2 3a2 2 0 0 1 1.4.6l3.8 3.8a2 2 0 0 1 .6 1.4V19a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2z M17 21v-7a1 1 0 0 0-1-1H8"
        + "a1 1 0 0 0-1 1v7 M7 3v4a1 1 0 0 0 1 1h7");

    public static Geometry Send { get; } = Parse(
        "M14.54 21.69a.5.5 0 0 0 .94-.02l6.5-19a.5.5 0 0 0-.64-.64l-19 6.5a.5.5 0 0 0-.02.94l7.93 3.18a2 2 0 0 1 1.11 1.11z M21.85 2.15"
        + "l-10.94 10.94");

    public static Geometry List { get; } = Parse("M3 12h.01 M3 18h.01 M3 6h.01 M8 12h13 M8 18h13 M8 6h13");

    public static Geometry Filter { get; } = Parse("M22 3H2l8 9.46V19l4 2v-8.54z");

    public static Geometry SortDescending { get; } = Parse("M3 16l4 4 4-4 M7 20V4 M11 4h10 M11 8h7 M11 12h4");

    private static Geometry Parse(string data) {
        return Geometry.Parse(data);
    }

    private static string Circle(double cx, double cy, double r) {
        return string.Create(CultureInfo.InvariantCulture, $"M{cx - r} {cy}a{r} {r} 0 1 0 {2 * r} 0a{r} {r} 0 1 0 {-2 * r} 0z");
    }

    private static string Rect(double x, double y, double width, double height, double r) {
        return string.Create(CultureInfo.InvariantCulture,
            $"M{x + r} {y}h{width - (2 * r)}a{r} {r} 0 0 1 {r} {r}v{height - (2 * r)}a{r} {r} 0 0 1 {-r} {r}h{-(width - (2 * r))}"
            + $"a{r} {r} 0 0 1 {-r} {-r}v{-(height - (2 * r))}a{r} {r} 0 0 1 {r} {-r}z");
    }
}
