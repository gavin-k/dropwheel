using System.Text.Json;
using System.Text.RegularExpressions;
using DropWheel.Core.Localization;
using FluentAssertions;

namespace DropWheel.Core.Tests.Localization;

/// <summary>
/// Reads the source of each project that speaks to the user: every <c>L.T("…")</c> needs its English in the
/// project's Strings.en.json, every English is still used and keeps the Chinese's placeholders, and no Chinese is
/// left in a literal the catalog can't see.
/// </summary>
public sealed partial class CatalogTests
{
    public static TheoryData<string> Projects => new() { "src/DropWheel.Core", "src/DropWheel.Engines", "src/DropWheel.App", "src/DropWheel.Shell" };

    /// <summary>Chinese literals that are meant to stay Chinese in English too, by project-relative file.</summary>
    private static readonly Dictionary<string, string[]> Untranslated = new()
    {
        // The language picker names Chinese in Chinese, so someone who reads only it can find it.
        ["Settings/SettingsWindow.cs"] = ["简体中文"],
        // A group's name is the user's data in layout.json; the one DropWheel makes up carries both languages.
        ["Wheel/WheelLayout.cs"] = ["更多"],
    };

    [Theory]
    [MemberData(nameof(Projects))]
    public void EveryTextHasItsEnglish(string project)
    {
        var english = Catalog(project);
        Texts(project).Where(t => !english.ContainsKey(t.Text)).Select(t => $"{t.File}: {t.Text}").Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(Projects))]
    public void EveryEnglishIsStillUsed(string project)
    {
        var used = Texts(project).Select(t => t.Text).ToHashSet();
        Catalog(project).Keys.Where(k => !used.Contains(k)).Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(Projects))]
    public void EnglishKeepsThePlaceholders(string project)
    {
        foreach (var (chinese, english) in Catalog(project))
        {
            var indices = Indices(chinese);
            Indices(english).Should().BeEquivalentTo(indices, $"“{english}” translates “{chinese}”");
            var args = Enumerable.Range(0, indices.Count == 0 ? 0 : indices.Max() + 1).Select(n => (object?)(n + 2)).ToArray();
            Loc.Text(chinese, new Dictionary<string, string> { [chinese] = english }, args).Should().NotContain("|");
        }
    }

    [Theory]
    [MemberData(nameof(Projects))]
    public void TextIsAlwaysOnePlainLiteral(string project)
    {
        var wrong = new List<string>();
        foreach (var (file, literals, code) in Sources(project))
        {
            foreach (Match call in Call().Matches(code))
            {
                var literal = Argument(call, literals, code);
                var after = literal is null ? "" : code[literal.End..].TrimStart();
                if (literal is not { Plain: true } || after.Length == 0 || after[0] is not (',' or ')'))
                {
                    wrong.Add($"{file}:{Line(code, call.Index)}");
                }
            }
        }

        wrong.Should().BeEmpty("L.T takes one plain literal, so the catalog can be checked against it");
    }

    [Theory]
    [MemberData(nameof(Projects))]
    public void NoChineseIsLeftOutsideTheCatalog(string project)
    {
        var left = new List<string>();
        foreach (var (file, literals, code) in Sources(project))
        {
            var keys = Keys(literals, code);
            var allowed = Untranslated.GetValueOrDefault(file, []);
            left.AddRange(literals
                .Where(l => !keys.Contains(l) && Chinese().IsMatch(l.Text) && !allowed.Contains(l.Text))
                .Select(l => $"{file}:{Line(code, l.Start)}: {l.Text}"));
        }

        left.Should().BeEmpty();
    }

    [Theory]
    [MemberData(nameof(Projects))]
    public void EachProjectEmbedsItsCatalogAndCompilesL(string project)
    {
        var csproj = File.ReadAllText(Path.Combine(Root, project, Path.GetFileName(project) + ".csproj"));
        csproj.Should().Contain("<Compile Include=\"../Shared/L.cs\"")
            .And.Contain($"<EmbeddedResource Include=\"{Loc.CatalogName}\" LogicalName=\"{Loc.CatalogName}\" WithCulture=\"false\" />");
    }

    [Fact]
    public void TheCatalogIsEmbedded() =>
        Loc.Catalog(typeof(Loc).Assembly).Should().BeEquivalentTo(Catalog("src/DropWheel.Core"));

    [Fact]
    public void TheLexerSeesOnlyLiterals()
    {
        const string source = """"
            // "注释"
            var a = L.T("甲 \"{0}\"", $"{(b ? "乙" : @"丙""")}丁");
            /* "注释" */ var c = '"'; var d = $$"""戊{{e}}""";
            """";
        var (literals, code) = CSharpLiterals.Read(source);
        literals.Select(l => l.Text).Should().Equal("甲 \"{0}\"", "乙", "丙\"", "丁", "戊{{e}}");
        literals.Select(l => l.Plain).Should().Equal(true, true, false, false, false);
        code.Should().NotContain("注释").And.Contain("L.T(").And.Contain("(b ?");
    }

    private static IEnumerable<(string File, string Text)> Texts(string project) =>
        Sources(project).SelectMany(s => Keys(s.Literals, s.Code).Where(l => l.Plain).Select(l => (s.File, l.Text)));

    private static HashSet<Literal> Keys(IReadOnlyList<Literal> literals, string code) =>
        Call().Matches(code).Select(m => Argument(m, literals, code)).OfType<Literal>().ToHashSet();

    /// <summary>The literal that opens a call's arguments, if its first argument starts with one.</summary>
    private static Literal? Argument(Match call, IReadOnlyList<Literal> literals, string code)
    {
        var at = call.Index + call.Length;
        return literals.FirstOrDefault(l => l.Start >= at) is { } next && string.IsNullOrWhiteSpace(code[at..next.Start]) ? next : null;
    }

    private static IEnumerable<(string File, IReadOnlyList<Literal> Literals, string Code)> Sources(string project)
    {
        var dir = Path.Combine(Root, project);
        foreach (var path in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(dir, path).Replace('\\', '/');
            if (relative.StartsWith("bin/", StringComparison.Ordinal) || relative.StartsWith("obj/", StringComparison.Ordinal))
            {
                continue;
            }

            var (literals, code) = CSharpLiterals.Read(File.ReadAllText(path));
            yield return (relative, literals, code);
        }
    }

    private static Dictionary<string, string> Catalog(string project)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, project, Loc.CatalogName)));
        return json.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? "");
    }

    private static HashSet<int> Indices(string template) =>
        Placeholder().Matches(template).Select(m => int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)).ToHashSet();

    private static int Line(string code, int index) => code.AsSpan(0, index).Count('\n') + 1;

    private static string Root { get; } = FindRoot();

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "DropWheel.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new DirectoryNotFoundException("No DropWheel.slnx above " + AppContext.BaseDirectory);
    }

    [GeneratedRegex(@"\bL\.T\(")]
    private static partial Regex Call();

    [GeneratedRegex(@"[　-〿一-鿿＀-￯]")]
    private static partial Regex Chinese();

    [GeneratedRegex(@"\{(\d+)[^{}]*\}")]
    private static partial Regex Placeholder();
}
