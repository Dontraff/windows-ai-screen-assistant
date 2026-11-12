using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace ScreenAssistant.Core.AIClient;

internal static class ResponseParser
{
    private const int MaxAnswerLength = 64 * 1024;

    public static string ReadCompletion(JsonElement root)
    {
        if (FirstChoice(root) is { } choice
            && Property(choice, "message") is { } message
            && ReadString(message, "content") is { } text
            && !string.IsNullOrWhiteSpace(text) && text.Length <= MaxAnswerLength)
        {
            return text.Trim();
        }

        throw new AiException("API не вернул текст в choices[0].message.content.");
    }

    public static async Task<string> ReadStreamAsync(Stream stream, Func<string, Task> report,
        CancellationToken token)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        var answer = new StringBuilder();
        var pending = new StringBuilder();
        var data = new StringBuilder();
        var lastUpdate = Stopwatch.GetTimestamp();
        var emitted = false;
        var finished = false;
        var done = false;

        while (!done)
        {
            var line = await reader.ReadLineAsync(token).ConfigureAwait(false);
            if (string.IsNullOrEmpty(line))
            {
                if (data.Length > 0)
                {
                    var payload = data.ToString();
                    data.Clear();
                    done = payload.Trim() == "[DONE]";
                    if (!done)
                    {
                        using var json = JsonDocument.Parse(payload);
                        var root = json.RootElement;
                        var choice = FirstChoice(root);
                        var reason = choice is { } value ? ReadString(value, "finish_reason") : null;
                        if (Property(root, "error") is { ValueKind: not JsonValueKind.Null }
                            || reason is "error" or "content_filter")
                        {
                            throw new AiException("AI Tunnel сообщил об ошибке во время генерации.");
                        }

                        finished |= reason is "stop" or "length";
                        if (choice is { } item && Property(item, "delta") is { } delta
                            && ReadString(delta, "content") is { Length: > 0 } chunk)
                        {
                            if (answer.Length + chunk.Length > MaxAnswerLength)
                                throw new AiException("Потоковый ответ превысил допустимый размер.");

                            answer.Append(chunk);
                            pending.Append(chunk);
                            if (!emitted || Stopwatch.GetElapsedTime(lastUpdate) >= TimeSpan.FromMilliseconds(50))
                            {
                                await report(pending.ToString()).ConfigureAwait(false);
                                pending.Clear();
                                emitted = true;
                                lastUpdate = Stopwatch.GetTimestamp();
                            }
                        }
                    }
                }

                if (line is null)
                    break;
            }
            else if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                if (data.Length + line.Length > 512 * 1024)
                    throw new AiException("Фрагмент потока превысил допустимый размер.");

                if (data.Length > 0)
                    data.Append('\n');
                var start = line.Length > 5 && line[5] == ' ' ? 6 : 5;
                data.Append(line.AsSpan(start));
            }
        }

        token.ThrowIfCancellationRequested();
        if (!done && !finished)
            throw new AiException("Поток API завершился до получения полного ответа.");

        var text = answer.ToString().Trim();
        if (text.Length == 0)
            throw new AiException("API завершил поток без текстового ответа.");

        if (pending.Length > 0)
            await report(pending.ToString()).ConfigureAwait(false);

        return text;
    }

    private static JsonElement? FirstChoice(JsonElement root) =>
        Property(root, "choices") is { ValueKind: JsonValueKind.Array } choices
        && choices.GetArrayLength() > 0 ? choices[0] : null;

    private static JsonElement? Property(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property)
            ? property : null;

    private static string? ReadString(JsonElement value, string name) =>
        Property(value, name) is { ValueKind: JsonValueKind.String } property
            ? property.GetString() : null;
}
