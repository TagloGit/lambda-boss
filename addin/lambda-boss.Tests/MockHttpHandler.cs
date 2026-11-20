using System.Net;

namespace LambdaBoss.Tests;

/// <summary>
///     A test HttpMessageHandler that returns canned responses based on URL patterns.
/// </summary>
internal sealed class MockHttpHandler : HttpMessageHandler
{
    private readonly Dictionary<string, (HttpStatusCode Status, string Content)> _responses = [];
    private readonly Dictionary<string, Dictionary<string, string>> _headers = [];

    /// <summary>Every request URL seen, in order.</summary>
    public List<string> Requests { get; } = [];

    public int ApiRequestCount => Requests.Count(u => u.Contains("api.github.com"));

    public void Register(string urlContains, string content, HttpStatusCode status = HttpStatusCode.OK)
    {
        _responses[urlContains] = (status, content);
    }

    public void Register(string urlContains, string content, HttpStatusCode status,
        Dictionary<string, string> headers)
    {
        _responses[urlContains] = (status, content);
        _headers[urlContains] = headers;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.ToString();
        Requests.Add(url);

        foreach (var (pattern, (status, content)) in _responses)
            if (url.Contains(pattern))
            {
                var response = new HttpResponseMessage(status)
                {
                    Content = new StringContent(content)
                };
                if (_headers.TryGetValue(pattern, out var headers))
                    foreach (var (k, v) in headers)
                        response.Headers.TryAddWithoutValidation(k, v);
                return Task.FromResult(response);
            }

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent($"No mock registered for: {url}")
        });
    }
}
