using System;
using System.Buffers;
using System.Text;
using System.Text.Json;

namespace Unify.Cli.Helpers
{
    public static class JsonHelpers
    {
        // https://stackoverflow.com/a/59574030
        public static string MergeJson(string originalJson, string newContent)
        {
            var outputBuffer = new ArrayBufferWriter<byte>();

            using (var jDoc1 = JsonDocument.Parse(originalJson, new JsonDocumentOptions { AllowTrailingCommas = true }))
            using (var jDoc2 = JsonDocument.Parse(newContent))
            using (var jsonWriter = new Utf8JsonWriter(outputBuffer, new JsonWriterOptions { Indented = true }))
            {
                var root1 = jDoc1.RootElement;
                var root2 = jDoc2.RootElement;

                if (root1.ValueKind != JsonValueKind.Array && root1.ValueKind != JsonValueKind.Object)
                {
                    throw new InvalidOperationException($"The original JSON document to merge new content into must be a container type. Instead it is {root1.ValueKind}.");
                }

                if (root1.ValueKind != root2.ValueKind)
                {
                    return originalJson;
                }

                if (root1.ValueKind == JsonValueKind.Array)
                {
                    MergeArrays(jsonWriter, root1, root2);
                }
                else
                {
                    MergeObjects(jsonWriter, root1, root2);
                }
            }

            return Encoding.UTF8.GetString(outputBuffer.WrittenSpan);
        }

        private static void MergeObjects(Utf8JsonWriter jsonWriter, JsonElement root1, JsonElement root2)
        {

            jsonWriter.WriteStartObject();

            foreach (var property in root1.EnumerateObject())
            {
                var propertyName = property.Name;

                JsonValueKind newValueKind;

                if (root2.TryGetProperty(propertyName, out var newValue) && (newValueKind = newValue.ValueKind) != JsonValueKind.Null)
                {
                    jsonWriter.WritePropertyName(propertyName);

                    var originalValue = property.Value;
                    var originalValueKind = originalValue.ValueKind;

                    if (newValueKind == JsonValueKind.Object && originalValueKind == JsonValueKind.Object)
                    {
                        MergeObjects(jsonWriter, originalValue, newValue); // Recursive call
                    }
                    else if (newValueKind == JsonValueKind.Array && originalValueKind == JsonValueKind.Array)
                    {
                        MergeArrays(jsonWriter, originalValue, newValue);
                    }
                    else
                    {
                        newValue.WriteTo(jsonWriter);
                    }
                }
                else
                {
                    property.WriteTo(jsonWriter);
                }
            }

            // Write all the properties of the second document that are unique to it.
            foreach (var property in root2.EnumerateObject())
            {
                if (!root1.TryGetProperty(property.Name, out _))
                {
                    property.WriteTo(jsonWriter);
                }
            }

            jsonWriter.WriteEndObject();
        }

        private static void MergeArrays(Utf8JsonWriter jsonWriter, JsonElement root1, JsonElement root2)
        {

            jsonWriter.WriteStartArray();

            // Write all the elements from both JSON arrays
            foreach (var element in root1.EnumerateArray())
            {
                element.WriteTo(jsonWriter);
            }
            foreach (var element in root2.EnumerateArray())
            {
                element.WriteTo(jsonWriter);
            }

            jsonWriter.WriteEndArray();
        }
    }
}
