namespace FloorballDJ.Models;

public sealed record AudioFileStatus(string DeckName, string Title, string FilePath, bool IsMissing,
    string Problem, string SearchHint, string SuggestedRoot);
