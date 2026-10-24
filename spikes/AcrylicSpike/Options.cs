namespace AcrylicSpike;

internal enum Variant
{
    /// <summary>Transparent host window + SystemBackdropElement acrylic disc (the M0 design).</summary>
    Acrylic,
    /// <summary>Transparent host window + plain semi-transparent disc: same geometry, no backdrop (cost baseline).</summary>
    None,
    /// <summary>Opaque host window + the same acrylic disc: control case if the element renders nothing on a transparent host.</summary>
    Opaque,
}

/// <param name="IdleMs">Extra hide time between cycle 1 and cycle 2 (0 = none): does a long idle undo the warm-up?</param>
internal sealed record Options(Variant Variant, int Runs, int SizePx, bool DefaultConfig, int IdleMs)
{
    public static Options Parse(string[] args)
    {
        var variant = Variant.Acrylic;
        var runs = 20;
        var size = 600;
        var defaultConfig = false;
        var idleMs = 0;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--variant" when i + 1 < args.Length: variant = Enum.Parse<Variant>(args[++i], ignoreCase: true); break;
                case "--runs" when i + 1 < args.Length: runs = int.Parse(args[++i]); break;
                case "--size" when i + 1 < args.Length: size = int.Parse(args[++i]); break;
                case "--default-config": defaultConfig = true; break;
                case "--idle-ms" when i + 1 < args.Length: idleMs = int.Parse(args[++i]); break;
            }
        }

        return new Options(variant, runs, size, defaultConfig, idleMs);
    }
}
