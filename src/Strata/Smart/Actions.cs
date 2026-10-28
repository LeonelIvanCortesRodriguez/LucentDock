using Strata.Core;

namespace Strata.Smart;

/// <summary>What automations may do to the dock. Every stateful operation returns a handle that undoes it.</summary>
public interface IDockActions
{
    IDisposable PushLayout(LayoutLayer layer);
    IDisposable RequestTheme(string owner, DockStyle style);
    /// <summary>Show an "Open X?" chip on the dock; null when no pinned app matches.</summary>
    IDisposable? ShowSuggestion(string owner, string appName, string reason);
    void StartFocus();
}

public sealed class ActionContext(RuleContext rule, IDockActions dock, NotificationCenter notifications, bool resuming)
{
    public RuleContext Rule { get; } = rule;
    public IDockActions Dock { get; } = dock;
    public NotificationCenter Notifications { get; } = notifications;
    /// <summary>True when re-applying an active automation after a restart: one-shot actions are skipped.</summary>
    public bool Resuming { get; } = resuming;
}

/// <param name="Effect">Undo handle for reversible actions; disposed when the automation ends.</param>
public sealed record ActionResult(string Summary, IDisposable? Effect = null, bool Failed = false);

public abstract class RuleAction
{
    /// <summary>Plain-language description for the settings page.</summary>
    public abstract string Description { get; }
    /// <summary>False for one-shot actions (notifications) that must not repeat when an automation resumes.</summary>
    public virtual bool IsEffect => true;
    public abstract ActionResult Execute(ActionContext ctx);
}

/// <summary>Surface widgets: move them to the front (adding them if hidden) and optionally highlight some.</summary>
public sealed class PromoteWidgets(IReadOnlyList<string> widgets, Func<RuleContext, IReadOnlyList<string>>? emphasize = null,
    string? durationMinutesParam = null) : RuleAction
{
    public IReadOnlyList<string> Widgets { get; } = widgets;
    public override string Description => $"Show {string.Join(", ", Widgets)} first" +
                                          (durationMinutesParam is null ? "" : " for a while");

    public override ActionResult Execute(ActionContext ctx)
    {
        DateTime? expires = durationMinutesParam is null ? null : ctx.Rule.Now.AddMinutes(ctx.Rule.Number(durationMinutesParam));
        var highlight = emphasize?.Invoke(ctx.Rule) ?? [];
        var layer = new LayoutLayer(ctx.Rule.Rule.Id, ctx.Rule.Rule.Priority, Widgets, [], highlight, expires);
        return new ActionResult($"showed {string.Join(", ", Widgets)}", ctx.Dock.PushLayout(layer));
    }
}

/// <summary>Hide widgets while the automation is active (optionally only while its <paramref name="enabledParam"/> toggle is on).</summary>
public sealed class CollapseWidgets(IReadOnlyList<string> widgets, string? enabledParam = null) : RuleAction
{
    public IReadOnlyList<string> Widgets { get; } = widgets;
    public override string Description => $"Hide {string.Join(", ", Widgets)}" + (enabledParam is null ? "" : " (optional)");

    public override ActionResult Execute(ActionContext ctx)
    {
        if (enabledParam is not null && !ctx.Rule.Flag(enabledParam)) return new ActionResult("hiding is off for this automation");
        var layer = new LayoutLayer(ctx.Rule.Rule.Id, ctx.Rule.Rule.Priority, [], Widgets, []);
        return new ActionResult($"hid {string.Join(", ", Widgets)}", ctx.Dock.PushLayout(layer));
    }
}

public sealed class SwitchTheme(Func<RuleContext, DockStyle> style) : RuleAction
{
    public override string Description => "Switch the dock style";

    public override ActionResult Execute(ActionContext ctx)
    {
        var s = style(ctx.Rule);
        return new ActionResult($"switched to {s}", ctx.Dock.RequestTheme(ctx.Rule.Rule.Id, s));
    }
}

public sealed class SuggestApp(string appParam, string reason) : RuleAction
{
    public override string Description => "Suggest opening an app";

    public override ActionResult Execute(ActionContext ctx)
    {
        string app = ctx.Rule.Text(appParam).Trim();
        if (app.Length == 0) return new ActionResult("no app to suggest (not configured)");
        var handle = ctx.Dock.ShowSuggestion(ctx.Rule.Rule.Id, app, reason);
        return handle is null
            ? new ActionResult($"no pinned app named \"{app}\"")
            : new ActionResult($"suggested {app}", handle);
    }
}

public sealed class StartFocus : RuleAction
{
    public override string Description => "Start a focus session";
    public override bool IsEffect => false;

    public override ActionResult Execute(ActionContext ctx)
    {
        if (ctx.Resuming) return new ActionResult("(not repeated on resume)");
        ctx.Dock.StartFocus();
        return new ActionResult("started a focus session");
    }
}

/// <summary>A notification routed through <see cref="NotificationCenter"/> (quiet hours + de-duplication).</summary>
public sealed class Notify(
    Func<RuleContext, (string Title, string Body)> content,
    Func<RuleContext, string> key,
    TimeSpan dedupWindow,
    string? enabledParam = null,
    bool urgent = false) : RuleAction
{
    public override string Description => "Send a notification" + (enabledParam is null ? "" : " (optional)");
    public override bool IsEffect => false;

    public override ActionResult Execute(ActionContext ctx)
    {
        if (ctx.Resuming) return new ActionResult("(not repeated on resume)");
        if (enabledParam is not null && !ctx.Rule.Flag(enabledParam)) return new ActionResult("notification off for this automation");
        var (title, body) = content(ctx.Rule);
        var outcome = ctx.Notifications.Send(key(ctx.Rule), title, body, ctx.Rule.Rule.Id, dedupWindow, urgent);
        return new ActionResult(NotificationCenter.Describe(outcome), Failed: outcome == NotifyOutcome.Failed);
    }
}
