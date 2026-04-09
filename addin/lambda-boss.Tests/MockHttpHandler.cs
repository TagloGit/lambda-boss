using System.Net;
using System.Net.Http;

namespace LambdaBoss.Tests;

/// <summary>
///     A test HttpMessageHandler that returns canned responses based on URL patterns.
/// </summary>
internal sealed class MockHttpHandler : HttpMessageHandler
{
    private readonly Dictionary<string, (HttpStatusCode Status, string Content)> _responses = new();

    public void Register(string urlContains, string content, HttpStatusCode status = HttpStatusCode.OK)
    {
        _responses[urlContains] = (status, content);
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.ToString();

        foreach (var (pattern, (status, content)) in _responses)
        {
            if (url.Contains(pattern))
            {
                return Task.FromResult(new HttpResponseMessage(status)
                {
                    Content = new StringContent(content)
                });
            }
        }

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent($"No mock registered for: {url}")
        });
    }
}
