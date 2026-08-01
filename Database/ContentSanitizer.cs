using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenCodeDatabaseCleaner.Database;

internal static class ContentSanitizer
{
    internal const string PlaceholderText = "<cleaned>";

    public static string CreatePlaceholderPart() =>
        new JsonObject
        {
            ["type"] = "text",
            ["text"] = PlaceholderText
        }.ToJsonString();

    public static string? SanitizeMessage(string data)
    {
        var root = ParseObject(data);
        var changed = SanitizeMessage(root);
        return changed ? root.ToJsonString() : null;
    }

    public static string? CreatePlaceholderPart(string data)
    {
        var root = ParseObject(data);
        var changed = CreatePlaceholderPart(root);
        return changed ? root.ToJsonString() : null;
    }

    public static string? SanitizeMessageEvent(string data)
    {
        var root = ParseObject(data);
        if (root["info"] is not JsonObject message)
        {
            throw new InvalidDataException("A message event is missing its info payload.");
        }

        var changed = SanitizeMessage(message);
        return changed ? root.ToJsonString() : null;
    }

    public static string? CreatePlaceholderPartEvent(string data)
    {
        var root = ParseObject(data);
        if (root["part"] is not JsonObject part)
        {
            throw new InvalidDataException("A message-part event is missing its part payload.");
        }

        var changed = CreatePlaceholderPart(part);
        return changed ? root.ToJsonString() : null;
    }

    public static string? CreatePlaceholderPartEventForRemovedPart(string data)
    {
        var root = ParseObject(data);
        if (root["part"] is not JsonObject part)
        {
            throw new InvalidDataException("A message-part event is missing its part payload.");
        }

        var id = GetString(part, "id");
        var sessionID = GetString(part, "sessionID");
        var messageID = GetString(part, "messageID");
        if (id is null || sessionID is null || messageID is null)
        {
            throw new InvalidDataException("A message-part event is missing its part identity.");
        }

        var placeholder = new JsonObject
        {
            ["id"] = id,
            ["sessionID"] = sessionID,
            ["messageID"] = messageID,
            ["type"] = "text",
            ["text"] = PlaceholderText
        };

        if (JsonNode.DeepEquals(part, placeholder))
        {
            return null;
        }

        root["part"] = placeholder;
        return root.ToJsonString();
    }

    public static (string? MessageId, string? PartId) GetEventIdentity(string eventType, string data)
    {
        var root = ParseObject(data);

        return eventType switch
        {
            "message.updated.1" when root["info"] is JsonObject message =>
                (GetString(message, "id"), null),
            "message.part.updated.1" when root["part"] is JsonObject part =>
                (GetString(part, "messageID"), GetString(part, "id")),
            _ => (null, null)
        };
    }

    private static bool SanitizeMessage(JsonObject message)
    {
        var changed = false;
        var role = GetString(message, "role");

        if (role == "user")
        {
            changed |= message.Remove("system");
            changed |= message.Remove("summary");

            if (message["format"] is JsonObject format)
            {
                if (GetString(format, "type") == "json_schema")
                {
                    var emptySchema = new JsonObject();
                    if (!JsonNode.DeepEquals(format["schema"], emptySchema))
                    {
                        format["schema"] = emptySchema;
                        changed = true;
                    }
                }
                else
                {
                    changed |= format.Remove("schema");
                }
            }
        }
        else if (role == "assistant")
        {
            changed |= message.Remove("structured");
            changed |= message.Remove("error");

            var emptyPath = new JsonObject
            {
                ["cwd"] = string.Empty,
                ["root"] = string.Empty
            };
            if (!JsonNode.DeepEquals(message["path"], emptyPath))
            {
                message["path"] = emptyPath;
                changed = true;
            }
        }
        else
        {
            throw new InvalidDataException($"Unsupported message role '{role ?? "<missing>"}'.");
        }

        return changed;
    }

    private static bool CreatePlaceholderPart(JsonObject part)
    {
        if (GetString(part, "type") != "text")
        {
            throw new InvalidDataException("The retained message part is not text.");
        }

        var changed = GetString(part, "text") != PlaceholderText;
        if (changed)
        {
            part["text"] = PlaceholderText;
        }

        changed |= part.Remove("metadata");
        changed |= part.Remove("synthetic");
        changed |= part.Remove("ignored");
        return changed;
    }

    private static JsonObject ParseObject(string data)
    {
        try
        {
            return JsonNode.Parse(data) as JsonObject
                ?? throw new InvalidDataException("The JSON value is not an object.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The database contains malformed message JSON.", ex);
        }
    }

    private static string? GetString(JsonObject value, string propertyName) =>
        value[propertyName] is JsonValue property && property.TryGetValue<string>(out var text) ? text : null;
}
