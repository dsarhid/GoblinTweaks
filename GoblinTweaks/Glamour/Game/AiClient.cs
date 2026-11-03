using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;

namespace GoblinTweaks.Glamour.Game;

/// <summary>
/// Sends a request to an OpenAI-compatible chat completions endpoint with the user's key. The key travels only in
/// the Authorization header; only status codes and failure kinds are logged, never headers or bodies.
/// </summary>
internal sealed class AiClient
{
    // The free models think before answering; with a full list of candidates that takes about a minute.
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(120) };

    /// <summary>
    /// Asks once, and once more if the reply is not valid JSON. Returns the parsed result, or null with the reason.
    /// <paramref name="baseUrl"/> is the provider's full chat completions URL.
    /// </summary>
    public async Task<(AiParseResult? Result, AiFailure Failure)> AskAsync(
        string baseUrl, string model, string apiKey, string? reasoningEffort, AiRequestInput input, CancellationToken token)
    {
        if (!AiProtocol.SafeUrl(baseUrl) || !Uri.TryCreate(baseUrl, UriKind.Absolute, out var url))
        {
            Svc.Log.Info("GoblinGlamour: the provider URL must be https (plain http only to this PC).");
            return (null, new AiFailure(AiFailureKind.BadResponse, null));
        }

        // A reply that is not JSON is asked again once, except from the free provider (no key): it answers once
        // every 45 seconds, so an instant second request would only hit that limit.
        var attempts = apiKey.Length > 0 ? 2 : 1;
        var body = AiProtocol.BuildBody(model, input, reasoningEffort);
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            int status;
            string text;
            TimeSpan? retryAfter = null;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, url);
                request.Content = new StringContent(body, Encoding.UTF8, "application/json");
                if (apiKey.Length > 0)   // the free provider needs none
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                using var response = await Http.SendAsync(request, token).ConfigureAwait(false);
                status = (int)response.StatusCode;
                text = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
                retryAfter = response.Headers.RetryAfter?.Delta
                             ?? (response.Headers.RetryAfter?.Date is { } date ? date - DateTimeOffset.UtcNow : null);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                Svc.Log.Info($"GoblinGlamour: AI request failed ({ex.GetType().Name}).");
                return (null, new AiFailure(AiFailureKind.Network, null));
            }

            var failure = AiProtocol.Classify(status, text, retryAfter);
            if (failure.Kind != AiFailureKind.None)
            {
                Svc.Log.Info($"GoblinGlamour: AI answered HTTP {status} ({failure.Kind}): {AiProtocol.ErrorMessage(text)}");
                return (null, failure);
            }

            var result = AiProtocol.Parse(text, input.Candidates, input.Stains);
            if (result.Ok)
            {
                Svc.Log.Info($"GoblinGlamour: AI answered HTTP {status} with {result.Pieces.Count} pieces.");
                return (result, AiFailure.None);
            }
            Svc.Log.Info($"GoblinGlamour: AI reply was not valid JSON (attempt {attempt + 1}).");
        }
        return (null, new AiFailure(AiFailureKind.BadResponse, null));
    }

    /// <summary>
    /// The chat models the provider has now for this key (its "models" list, next to "chat/completions"), or null
    /// when it cannot be read.
    /// </summary>
    public async Task<IReadOnlyList<string>?> ListModelsAsync(
        string chatUrl, string apiKey, IReadOnlyList<string> suggested, bool freeOnly, CancellationToken token)
    {
        const string chat = "chat/completions";
        if (!AiProtocol.SafeUrl(chatUrl) || !chatUrl.EndsWith(chat, StringComparison.Ordinal)
            || !Uri.TryCreate(chatUrl[..^chat.Length] + "models", UriKind.Absolute, out var url))
            return null;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            using var response = await Http.SendAsync(request, token).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                Svc.Log.Info($"GoblinGlamour: model list answered HTTP {(int)response.StatusCode}: {AiProtocol.ErrorMessage(text)}");
                return null;
            }
            var models = AiProtocol.ChatModels(text, suggested, freeOnly);
            Svc.Log.Info($"GoblinGlamour: the provider has {models.Count} chat models.");
            return models.Count > 0 ? models : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Svc.Log.Info($"GoblinGlamour: model list failed ({ex.GetType().Name}).");
            return null;
        }
    }
}
