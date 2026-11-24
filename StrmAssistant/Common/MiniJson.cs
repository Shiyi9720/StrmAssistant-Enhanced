using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace StrmAssistant.Common
{
    /// <summary>
    /// A tiny mutable JSON tree used by the local metadata enhancer. System.Text.Json 5 (the
    /// version shipped next to the plugin through its dependency closure) has JsonDocument but no
    /// JsonNode, so patch merging and certification fallback build this tree and write it back.
    /// Values parsed from a document keep a reference to their JsonElement; callers must keep the
    /// owning JsonDocument alive until the tree has been serialized.
    /// </summary>
    internal abstract class MiniJsonNode
    {
        public abstract void WriteTo(Utf8JsonWriter writer);
    }

    internal sealed class MiniJsonObject : MiniJsonNode
    {
        public Dictionary<string, MiniJsonNode> Members { get; } =
            new Dictionary<string, MiniJsonNode>(StringComparer.Ordinal);

        public override void WriteTo(Utf8JsonWriter writer)
        {
            writer.WriteStartObject();

            foreach (var pair in Members)
            {
                writer.WritePropertyName(pair.Key);
                pair.Value.WriteTo(writer);
            }

            writer.WriteEndObject();
        }
    }

    internal sealed class MiniJsonArray : MiniJsonNode
    {
        public List<MiniJsonNode> Items { get; } = new List<MiniJsonNode>();

        public override void WriteTo(Utf8JsonWriter writer)
        {
            writer.WriteStartArray();

            foreach (var item in Items)
            {
                item?.WriteTo(writer);
            }

            writer.WriteEndArray();
        }
    }

    internal sealed class MiniJsonElementValue : MiniJsonNode
    {
        public JsonElement Element { get; }

        public MiniJsonElementValue(JsonElement element)
        {
            Element = element;
        }

        public override void WriteTo(Utf8JsonWriter writer)
        {
            Element.WriteTo(writer);
        }
    }

    internal sealed class MiniJsonStringValue : MiniJsonNode
    {
        public string Value { get; }

        public MiniJsonStringValue(string value)
        {
            Value = value;
        }

        public override void WriteTo(Utf8JsonWriter writer)
        {
            writer.WriteStringValue(Value);
        }
    }

    internal sealed class MiniJsonNumberValue : MiniJsonNode
    {
        private readonly long _value;

        public MiniJsonNumberValue(long value)
        {
            _value = value;
        }

        public override void WriteTo(Utf8JsonWriter writer)
        {
            writer.WriteNumberValue(_value);
        }
    }

    internal sealed class MiniJsonBoolValue : MiniJsonNode
    {
        private readonly bool _value;

        public MiniJsonBoolValue(bool value)
        {
            _value = value;
        }

        public override void WriteTo(Utf8JsonWriter writer)
        {
            writer.WriteBooleanValue(_value);
        }
    }

    internal sealed class MiniJsonNullValue : MiniJsonNode
    {
        public static readonly MiniJsonNullValue Instance = new MiniJsonNullValue();

        public override void WriteTo(Utf8JsonWriter writer)
        {
            writer.WriteNullValue();
        }
    }

    internal static class MiniJson
    {
        public static MiniJsonNode Parse(JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                {
                    var node = new MiniJsonObject();

                    foreach (var property in element.EnumerateObject())
                    {
                        node.Members[property.Name] = Parse(property.Value);
                    }

                    return node;
                }
                case JsonValueKind.Array:
                {
                    var node = new MiniJsonArray();

                    foreach (var item in element.EnumerateArray())
                    {
                        node.Items.Add(Parse(item));
                    }

                    return node;
                }
                case JsonValueKind.Null:
                case JsonValueKind.Undefined:
                    return MiniJsonNullValue.Instance;
                default:
                    return new MiniJsonElementValue(element);
            }
        }

        public static byte[] Serialize(MiniJsonNode node)
        {
            using (var buffer = new MemoryStream())
            {
                using (var writer = new Utf8JsonWriter(buffer))
                {
                    node.WriteTo(writer);
                }

                return buffer.ToArray();
            }
        }

        public static string ReadString(MiniJsonObject node, string name)
        {
            if (node is null || !node.Members.TryGetValue(name, out var value)) return null;

            if (value is MiniJsonElementValue element &&
                element.Element.ValueKind == JsonValueKind.String)
            {
                return element.Element.GetString();
            }

            if (value is MiniJsonStringValue text) return text.Value;

            return null;
        }

        public static MiniJsonObject GetObject(MiniJsonObject node, string name)
        {
            if (node is null || !node.Members.TryGetValue(name, out var value)) return null;
            return value as MiniJsonObject;
        }

        public static MiniJsonArray GetArray(MiniJsonObject node, string name)
        {
            if (node is null || !node.Members.TryGetValue(name, out var value)) return null;
            return value as MiniJsonArray;
        }
    }
}
