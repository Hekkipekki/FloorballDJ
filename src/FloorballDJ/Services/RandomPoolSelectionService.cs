using FloorballDJ.Models;

namespace FloorballDJ.Services;

public sealed record RandomPoolCandidate(Jingle Jingle, Guid DeckId);

public sealed record RandomDeckRunState(Guid DeckId, int ConsecutiveCount);

public static class RandomPoolSelectionService
{
    public static RandomPoolCandidate[] GetEligibleCandidates(
        IReadOnlyCollection<RandomPoolCandidate> candidates,
        bool trackSession,
        Guid? previousJingleId,
        bool deckVariationEnabled,
        int maxConsecutiveFromSameDeck,
        RandomDeckRunState? deckRun)
    {
        var all = candidates.DistinctBy(candidate => candidate.Jingle.Id).ToArray();
        if (all.Length == 0) return [];

        RandomPoolCandidate[] eligible;
        var forceAnotherDeck = deckVariationEnabled && deckRun is not null &&
                               deckRun.ConsecutiveCount >= Math.Clamp(maxConsecutiveFromSameDeck, 2, 10);
        if (forceAnotherDeck)
        {
            var alternatives = all.Where(candidate => candidate.DeckId != deckRun!.DeckId).ToArray();
            if (alternatives.Length > 0)
            {
                var unplayedAlternatives = trackSession
                    ? alternatives.Where(candidate => candidate.Jingle.SessionPlayCount == 0).ToArray()
                    : [];
                eligible = unplayedAlternatives.Length > 0 ? unplayedAlternatives : alternatives;
                return ExcludePreviousWhenPossible(eligible, previousJingleId);
            }
        }

        var unplayed = trackSession
            ? all.Where(candidate => candidate.Jingle.SessionPlayCount == 0).ToArray()
            : [];
        eligible = unplayed.Length > 0 ? unplayed : all;
        return ExcludePreviousWhenPossible(eligible, previousJingleId);
    }

    public static RandomDeckRunState AdvanceRun(RandomDeckRunState? current, Guid selectedDeckId) =>
        current?.DeckId == selectedDeckId
            ? current with { ConsecutiveCount = current.ConsecutiveCount + 1 }
            : new RandomDeckRunState(selectedDeckId, 1);

    private static RandomPoolCandidate[] ExcludePreviousWhenPossible(
        RandomPoolCandidate[] candidates, Guid? previousJingleId)
    {
        if (candidates.Length <= 1 || previousJingleId is not Guid previousId) return candidates;
        var withoutPrevious = candidates.Where(candidate => candidate.Jingle.Id != previousId).ToArray();
        return withoutPrevious.Length > 0 ? withoutPrevious : candidates;
    }
}
