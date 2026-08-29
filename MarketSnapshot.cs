namespace DalaLenoUndercut;

public sealed record MarketSnapshot(
    uint ItemId,
    string ItemName,
    bool IsHq,
    uint? CurrentLowestPrice,
    uint? SuggestedPrice,
    int MatchingListings,
    DateTime CapturedAt);
