namespace Convenciones.Ai.DocumentIntelligence;

/// <summary>Scripted layout reader for tests and local runs.</summary>
public sealed class FakeDocumentLayoutClient : IDocumentLayoutClient
{
    private readonly Queue<DocumentLayout> _layouts = new();

    public int Calls { get; private set; }

    public FakeDocumentLayoutClient Enqueue(DocumentLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        _layouts.Enqueue(layout);
        return this;
    }

    public Task<DocumentLayout> AnalyzeAsync(Stream pdf, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pdf);
        Calls++;

        if (_layouts.Count == 0)
        {
            throw new InvalidOperationException("FakeDocumentLayoutClient has no scripted layout left.");
        }

        return Task.FromResult(_layouts.Dequeue());
    }
}
