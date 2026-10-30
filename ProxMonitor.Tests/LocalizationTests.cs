using System.Text.RegularExpressions;
using ProxMonitor.Core;

namespace ProxMonitor.Tests;

/// <summary>Every Russian text in the app's sources has an English translation with the same placeholders.</summary>
public partial class LocalizationTests
{
    static readonly string Root = FindRoot();

    static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ProxMonitor.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("ProxMonitor.slnx not found");
    }

    [GeneratedRegex("[А-Яа-яЁё]")] private static partial Regex Cyrillic();
    [GeneratedRegex(@"\{app:T '([^']*)'\}")] private static partial Regex XamlText();
    [GeneratedRegex(@"(\$?)""((?:[^""\\]|\\.)*)""")] private static partial Regex CsString();
    [GeneratedRegex(@"\{(\d+)")] private static partial Regex Placeholder();

    static IEnumerable<(string File, string Text, bool Interpolated)> Texts()
    {
        foreach (var f in Directory.GetFiles(Path.Combine(Root, "ProxMonitor.App"), "*.axaml"))
            foreach (Match m in XamlText().Matches(File.ReadAllText(f)))
                yield return (Path.GetFileName(f), m.Groups[1].Value, false);

        string[] skip = ["Strings.en.cs", "Localization.cs", "TExtension.cs"];
        foreach (var project in new[] { "ProxMonitor.App", "ProxMonitor.Core" })
            foreach (var f in Directory.GetFiles(Path.Combine(Root, project), "*.cs").Where(f => !skip.Contains(Path.GetFileName(f))))
                foreach (var line in File.ReadAllLines(f).Where(l => !l.TrimStart().StartsWith("//")))
                    foreach (Match m in CsString().Matches(line))
                        if (Cyrillic().IsMatch(m.Groups[2].Value))
                            yield return (Path.GetFileName(f), m.Groups[2].Value.Replace("\\n", "\n"), m.Groups[1].Value == "$");
    }

    [Fact]
    public void Every_russian_text_is_translated()
    {
        var missing = Texts().Where(t => !L.Dictionary.ContainsKey(t.Text)).Select(t => $"{t.File}: {t.Text}").Distinct().ToList();
        Assert.True(missing.Count == 0, "No English for:\n" + string.Join("\n", missing));
    }

    [Fact]
    public void Russian_texts_are_not_interpolated()
    {
        // $"..." can't be looked up; it must be L.F("... {0}", value).
        var bad = Texts().Where(t => t.Interpolated).Select(t => $"{t.File}: {t.Text}").ToList();
        Assert.True(bad.Count == 0, "Use L.F instead of $\"\":\n" + string.Join("\n", bad));
    }

    [Fact]
    public void Translations_keep_placeholders_and_have_no_russian()
    {
        foreach (var (ru, en) in L.Dictionary)
        {
            Assert.Equal(Placeholder().Matches(ru).Select(m => m.Value).Order(), Placeholder().Matches(en).Select(m => m.Value).Order());
            if (ru != "Русский") Assert.False(Cyrillic().IsMatch(en), $"Russian left in: {en}");
        }
    }

    [Fact]
    public void Switching_language_translates()
    {
        try
        {
            L.English = true;
            Assert.Equal("Properties", L.T("Свойства"));
            Assert.Equal("CPU load 95% (limit 90%)", L.F("Нагрузка CPU {0:0}% (порог {1:0}%)", 95.2, 90));
            Assert.Equal("не в словаре", L.T("не в словаре")); // unknown text falls back to Russian
        }
        finally
        {
            L.English = false;
        }
    }
}
