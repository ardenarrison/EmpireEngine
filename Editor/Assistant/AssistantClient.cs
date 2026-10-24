using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Editor.Assistant;

public sealed record ChatMessage(string Role, string Text);   // Role is "user" or "assistant"

public sealed record AssistantReply(string Text, int InputTokens, int OutputTokens, string? StopReason);

// A problem with a plain-English message that can be shown as it is. It never contains the API key.
public sealed class AssistantException(string message) : Exception(message);

// Talks to the Claude API (docs.anthropic.com), using the user's own API key. Nothing here knows about Empire's key,
// because Empire has none: every user brings their own, and what they send is billed to their own Anthropic account.
//
// It only ever sends to api.anthropic.com over https (a tests-only exception allows this computer's own address), sends
// only what the caller hands it, and keeps no copy of the conversation or the key.
public static class AssistantClient
{
    public const string Endpoint = "https://api.anthropic.com/v1/messages";
    public const string ApiVersion = "2023-06-01";
    public const string DefaultModel = "claude-sonnet-5-5";
    public const int DefaultMaxTokens = 4096;

    // The models offered in the editor. The box also takes a model name typed in, so a newer one works without an update.
    public static readonly string[] Models = ["claude-sonnet-5-5", "claude-opus-5-5", "claude-haiku-5-5", "claude-fable-5-1"];

    public static HttpClient CreateClient(string editorVersion)
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };   // a long answer from a big model
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("EmpireEngineEditor", editorVersion));
        return client;
    }

    // Only Anthropic's own address, over https. (This computer's own address is allowed for tests and local proxies.)
    public static bool IsAllowedEndpoint(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (uri.IsLoopback && uri.Scheme is "http" or "https") return true;
        return uri.Scheme == "https" && uri.Host.Equals("api.anthropic.com", StringComparison.OrdinalIgnoreCase);
    }

    public static async Task<AssistantReply> SendAsync(HttpClient http, string apiKey, string model, string system,
        IReadOnlyList<ChatMessage> messages, int maxTokens, CancellationToken ct, string endpoint = Endpoint)
    {
        if (!IsAllowedEndpoint(endpoint))
            throw new AssistantException("The assistant only talks to api.anthropic.com, so it won't send to that address.");
        if (AssistantKeyStore.ValidateKey(apiKey) is { } keyProblem)
            throw new AssistantException(keyProblem);
        if (string.IsNullOrWhiteSpace(model))
            throw new AssistantException("Pick a model first.");
        if (messages.Count == 0 || messages[^1].Role != "user")
            throw new AssistantException("There is nothing to send.");

        ct.ThrowIfCancellationRequested();
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(BuildRequestJson(model, system, messages, maxTokens), Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("x-api-key", apiKey);
        request.Headers.Add("anthropic-version", ApiVersion);

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new AssistantException($"Couldn't reach Anthropic ({Redact(ex.Message, apiKey)}). Check your internet connection and try again.");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new AssistantException("Anthropic didn't answer in time. Try again in a moment.");
        }

        using (response)
        {
            string body = await response.Content.ReadAsStringAsync(ct);
            ct.ThrowIfCancellationRequested();
            if (!response.IsSuccessStatusCode)
                throw new AssistantException(DescribeError(response.StatusCode, body, apiKey));
            return ParseReply(body);
        }
    }

    public static string BuildRequestJson(string model, string system, IReadOnlyList<ChatMessage> messages, int maxTokens)
    {
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream))
        {
            json.WriteStartObject();
            json.WriteString("model"u8, model);
            json.WriteNumber("max_tokens"u8, maxTokens);
            if (system.Length > 0) json.WriteString("system"u8, system);
            json.WriteStartArray("messages"u8);
            foreach (var message in messages)
            {
                json.WriteStartObject();
                json.WriteString("role"u8, message.Role);
                json.WriteString("content"u8, message.Text);
                json.WriteEndObject();
            }
            json.WriteEndArray();
            json.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    public static AssistantReply ParseReply(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var text = new StringBuilder();
            if (root.TryGetProperty("content"u8, out var content) && content.ValueKind == JsonValueKind.Array)
                foreach (var block in content.EnumerateArray())
                    if (block.TryGetProperty("type"u8, out var type) && type.GetString() == "text" && block.TryGetProperty("text"u8, out var t))
                        text.Append(t.GetString());
            int input = 0, output = 0;
            if (root.TryGetProperty("usage"u8, out var usage))
            {
                if (usage.TryGetProperty("input_tokens"u8, out var i) && i.TryGetInt32(out int iv)) input = iv;
                if (usage.TryGetProperty("output_tokens"u8, out var o) && o.TryGetInt32(out int ov)) output = ov;
            }
            string? stop = root.TryGetProperty("stop_reason"u8, out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
            if (text.Length == 0)
                return new AssistantReply(stop == "refusal" ? "(The model declined to answer that.)" : "(The reply was empty.)", input, output, stop);
            return new AssistantReply(text.ToString(), input, output, stop);
        }
        catch (JsonException)
        {
            throw new AssistantException("Anthropic's answer wasn't in the expected form, so it couldn't be read.");
        }
    }

    // Turns an error answer into something a person can act on. The API's own message is included (it says things like
    // "your credit balance is too low"), after the key has been removed from it.
    public static string DescribeError(HttpStatusCode status, string body, string apiKey)
    {
        string detail = "";
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error"u8, out var error) && error.TryGetProperty("message"u8, out var message))
                detail = Redact(message.GetString() ?? "", apiKey).Trim();
        }
        catch (JsonException) { }
        if (detail.Length > 400) detail = detail[..400] + "...";

        string summary = (int)status switch
        {
            401 => "Anthropic rejected the API key. Check that it is correct and still active in the Anthropic Console.",
            403 => "This API key isn't allowed to do that. Check its permissions in the Anthropic Console.",
            404 => "Anthropic doesn't know that model name. Pick another model.",
            413 => "That was too big to send. Turn off an attachment or shorten the message.",
            429 => "Anthropic says too many requests were made, or the usage limit was reached. Wait a moment and try again.",
            500 or 502 or 503 or 504 or 529 => "Anthropic's service is busy or having trouble. Try again in a moment.",
            _ => $"Anthropic answered with an error ({(int)status}).",
        };
        return detail.Length > 0 ? $"{summary} ({detail})" : summary;
    }

    // Removes the key from text that is about to be shown, in case an error message echoes it back.
    public static string Redact(string text, string apiKey) =>
        apiKey.Length >= 8 ? text.Replace(apiKey, "[key removed]") : text;
}
