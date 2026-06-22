namespace LceMcp;

internal sealed record SyncWindowPlan(
    int RequestedSinceDays,
    int EffectiveSinceDays,
    bool AutoExpandedForGap,
    IReadOnlyDictionary<int, int> EffectiveSinceDaysByFolder);
