using FloorballDJ.Models;

namespace FloorballDJ.Services;

internal static class AutoplayShortcutService
{
    internal static bool ScopesOverlap(Guid? first, Guid? second) =>
        first is null || second is null || first == second;

    internal static bool IsAvailable(AutoplayProfile profile, AppSettings settings) =>
        profile.RandomPoolSetupId is null || profile.RandomPoolSetupId == settings.ActiveRandomPoolSetup?.Id;

    internal static AutoplayProfile? Find(AppSettings settings, string? pressed) =>
        (settings.AutoplayProfiles ?? [])
            .Where(profile => IsAvailable(profile, settings) && ShortcutService.MatchesCanonical(profile.Shortcut, pressed))
            // Prefer an explicit match if an imported profile contains an overlapping global shortcut.
            .OrderByDescending(profile => profile.RandomPoolSetupId.HasValue)
            .FirstOrDefault();
}
