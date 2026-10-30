namespace ProxMonitor.Core;

/// <summary>
/// Russian is the source language: every text is written in Russian and looked up in <see cref="En"/> for English.
/// The language is chosen once at startup; switching it restarts the app.
/// </summary>
public static partial class L
{
    public static bool English { get; set; }

    public static string T(string ru) => English && En.TryGetValue(ru, out var en) ? en : ru;

    /// <summary>Translated format string, e.g. F("Порог {0}%", 90).</summary>
    public static string F(string ru, params object?[] args) => string.Format(T(ru), args);

    /// <summary>Every Russian text in the app; a test fails if one is missing.</summary>
    public static IReadOnlyDictionary<string, string> Dictionary => En;
}
