using System.Globalization;

namespace Strata.Smart;

/// <summary>Three-valued result: true, false, or unknown (a signal is missing, stale or in error).</summary>
public readonly record struct Truth(bool? Value, string Explanation)
{
    public static Truth Unknown(string why) => new(null, why);
    public bool IsTrue => Value == true;
    public bool IsFalse => Value == false;
}

/// <summary>Everything a condition or action may look at while a rule is evaluated.</summary>
public sealed class RuleContext(SignalStore signals, DateTime now, RuleDefinition rule, IReadOnlyDictionary<string, string> parameters)
{
    public SignalStore Signals { get; } = signals;
    public DateTime Now { get; } = now;
    public RuleDefinition Rule { get; } = rule;
    public IReadOnlyDictionary<string, string> Parameters { get; } = parameters;

    public double Number(string id)
    {
        if (Parameters.TryGetValue(id, out var s) && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return v;
        var p = Rule.Parameters.FirstOrDefault(x => x.Id == id);
        return p is null ? 0 : double.Parse(p.Default, CultureInfo.InvariantCulture);
    }

    public string Text(string id) =>
        Parameters.TryGetValue(id, out var s) ? s : Rule.Parameters.FirstOrDefault(x => x.Id == id)?.Default ?? "";

    public bool Flag(string id) => Number(id) != 0;

    public T? Value<T>(SignalKey<T> key) where T : class => Signals.ValueOrDefault(key);
}

/// <summary>A condition over one or more signals. Rules re-evaluate only when one of <see cref="SignalIds"/> changes.</summary>
public abstract class Condition
{
    public abstract IEnumerable<string> SignalIds { get; }
    public abstract Truth Evaluate(RuleContext ctx);

    public static Condition On<T>(SignalKey<T> key, Func<T, RuleContext, bool> test, Func<T, RuleContext, string> explain,
        params SignalKey[] alsoDependsOn) => new SignalCondition<T>(key, test, explain, alsoDependsOn);

    public static Condition All(params Condition[] parts) => new AllCondition(parts);
    public static Condition Any(params Condition[] parts) => new AnyCondition(parts);
    public static Condition Not(Condition inner) => new NotCondition(inner);

    public static Condition operator &(Condition a, Condition b) => All(a, b);
    public static Condition operator |(Condition a, Condition b) => Any(a, b);
    public static Condition operator !(Condition a) => Not(a);
}

internal sealed class SignalCondition<T>(SignalKey<T> key, Func<T, RuleContext, bool> test, Func<T, RuleContext, string> explain,
    SignalKey[] extra) : Condition
{
    public override IEnumerable<string> SignalIds => extra.Select(k => k.Id).Prepend(key.Id).Distinct();

    public override Truth Evaluate(RuleContext ctx)
    {
        var snapshot = ctx.Signals.Get(key);
        if (snapshot is null) return Truth.Unknown($"no {key.Description.ToLowerInvariant()} yet");
        if (snapshot.Error is not null) return Truth.Unknown($"{key.Description.ToLowerInvariant()} unavailable ({snapshot.Error})");
        if (!snapshot.IsFresh(ctx.Now)) return Truth.Unknown($"{key.Description.ToLowerInvariant()} is out of date");
        if (snapshot.Value is not T value) return Truth.Unknown($"{key.Id} has an unexpected value");
        return new Truth(test(value, ctx), explain(value, ctx));
    }
}

internal sealed class AllCondition(Condition[] parts) : Condition
{
    public override IEnumerable<string> SignalIds => parts.SelectMany(p => p.SignalIds).Distinct();

    public override Truth Evaluate(RuleContext ctx)
    {
        var results = parts.Select(p => p.Evaluate(ctx)).ToList();
        var failed = results.FirstOrDefault(r => r.IsFalse);
        if (failed.Value is false) return new Truth(false, failed.Explanation);
        var unknown = results.FirstOrDefault(r => r.Value is null);
        if (results.Any(r => r.Value is null)) return Truth.Unknown(unknown.Explanation);
        return new Truth(true, Join(results.Select(r => r.Explanation), " and "));
    }

    internal static string Join(IEnumerable<string> parts, string sep) => string.Join(sep, parts.Where(p => p.Length > 0));
}

internal sealed class AnyCondition(Condition[] parts) : Condition
{
    public override IEnumerable<string> SignalIds => parts.SelectMany(p => p.SignalIds).Distinct();

    public override Truth Evaluate(RuleContext ctx)
    {
        var results = parts.Select(p => p.Evaluate(ctx)).ToList();
        var hits = results.Where(r => r.IsTrue).ToList();
        if (hits.Count > 0) return new Truth(true, AllCondition.Join(hits.Select(r => r.Explanation), " and "));
        if (results.Any(r => r.Value is null)) return Truth.Unknown(results.First(r => r.Value is null).Explanation);
        return new Truth(false, AllCondition.Join(results.Select(r => r.Explanation), " and "));
    }
}

internal sealed class NotCondition(Condition inner) : Condition
{
    public override IEnumerable<string> SignalIds => inner.SignalIds;

    public override Truth Evaluate(RuleContext ctx)
    {
        var r = inner.Evaluate(ctx);
        return r.Value is null ? r : new Truth(!r.Value, r.Explanation);
    }
}
