namespace DropWheel.Acceptance;

/// <summary>PASS/FAIL table printed at the end of a scenario.</summary>
internal sealed class Checks
{
    private readonly List<(string Name, bool Ok, string Detail)> _results = [];

    public bool Check(string name, bool ok, string detail)
    {
        _results.Add((name, ok, detail));
        Log.Info($"{(ok ? "PASS" : "FAIL")}  {name}: {detail}");
        return ok;
    }

    public int Report(string title)
    {
        Log.Info("");
        Log.Info($"==== {title} ====");
        foreach (var (name, ok, detail) in _results)
        {
            Log.Info($"  {(ok ? "✅" : "❌")} {name}: {detail}");
        }

        var passed = _results.Count > 0 && _results.All(r => r.Ok);
        Log.Info(passed ? "RESULT: PASS" : "RESULT: FAIL");
        return passed ? 0 : 1;
    }

    public static bool Wait(Func<bool> condition, int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition())
            {
                return true;
            }

            Thread.Sleep(10);
        }

        return condition();
    }
}
