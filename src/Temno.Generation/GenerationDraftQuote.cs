namespace Temno.Generation;

/// <summary>A quote belongs to one revision of one account's project draft.</summary>
public sealed class GenerationDraftQuote
{
    private object? _project;
    private string? _owner;
    public long Revision { get; private set; }
    public string? QuoteId { get; private set; }
    public int Credits { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }

    public void SetContext(object? project, string? owner)
    {
        if (ReferenceEquals(_project, project) && _owner == owner) return;
        _project = project;
        _owner = owner;
        Invalidate();
    }

    public void Invalidate()
    {
        Revision++;
        QuoteId = null;
        Credits = 0;
        ExpiresAt = default;
    }

    public long BeginQuote()
    {
        Invalidate();
        return Revision;
    }

    public bool Accept(long revision, string id, int credits, DateTimeOffset expiry, DateTimeOffset now)
    {
        if (revision != Revision || string.IsNullOrWhiteSpace(id) || credits < 0 || expiry <= now)
            return false;
        QuoteId = id;
        Credits = credits;
        ExpiresAt = expiry;
        return true;
    }

    public bool IsValid(DateTimeOffset now) => QuoteId is not null && ExpiresAt > now;
}
