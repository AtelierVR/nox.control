using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace Nox.Control.Runtime {
    public static class SchemaConverter {
        /// <summary>
        /// Converts an ISchema to a JSON Schema-compatible JObject for MCP/HTTP transport.
        /// </summary>
        public static JObject ToJObject(this ISchema s) {
            var props = new JObject();
            var required = new JArray();

            foreach (var p in s.Properties) {
                var propObj = ToJsonSchema(p.Type);

                if (!string.IsNullOrEmpty(p.Description))
                    propObj["description"] = p.Description;

                props[p.Name] = propObj;
                if (p.Required)
                    required.Add(p.Name);
            }

            var schema = new JObject {
                ["type"] = s.Type,
                ["properties"] = props
            };

            if (required.Count > 0)
                schema["required"] = required;

            return schema;
        }

        /// <summary>
        /// Builds the JSON Schema fragment describing a CLR type.
        /// <para>
        /// Arrays and generic collections are emitted as real <c>type: "array"</c> with an
        /// <c>items</c> schema. Previously every unknown type (including <c>int[]</c>) fell
        /// back to <c>"string"</c>, which made MCP clients send the argument as a JSON string
        /// (e.g. <c>"path": "[-1,-8910]"</c>) instead of an array, and the operator then failed
        /// to bind it.
        /// </para>
        /// </summary>
        private static JObject ToJsonSchema(Type t) {
            var nullable = Nullable.GetUnderlyingType(t);
            if (nullable != null)
                return ToJsonSchema(nullable);

            if (t.IsEnum)
                return new JObject {
                    ["type"] = "string",
                    ["enum"] = new JArray(Enum.GetNames(t))
                };

            if (t == typeof(bool))
                return new JObject { ["type"] = "boolean" };

            if (t == typeof(int) || t == typeof(long) || t == typeof(short) || t == typeof(byte)
                || t == typeof(uint) || t == typeof(ulong) || t == typeof(ushort) || t == typeof(sbyte))
                return new JObject { ["type"] = "integer" };

            if (t == typeof(float) || t == typeof(double) || t == typeof(decimal))
                return new JObject { ["type"] = "number" };

            if (t == typeof(string) || t == typeof(char) || t == typeof(Guid) || t == typeof(DateTime))
                return new JObject { ["type"] = "string" };

            // Any JSON value.
            if (t == typeof(object) || typeof(JToken).IsAssignableFrom(t))
                return new JObject();

            if (t.IsArray)
                return new JObject {
                    ["type"] = "array",
                    ["items"] = ToJsonSchema(t.GetElementType())
                };

            if (TryGetElementType(t, out var element))
                return new JObject {
                    ["type"] = "array",
                    ["items"] = ToJsonSchema(element)
                };

            return new JObject { ["type"] = "object" };
        }

        private static bool TryGetElementType(Type t, out Type element) {
            element = null;
            if (t == typeof(string))
                return false;

            if (t.IsGenericType) {
                var definition = t.GetGenericTypeDefinition();
                if (definition == typeof(List<>)
                    || definition == typeof(IList<>)
                    || definition == typeof(IReadOnlyList<>)
                    || definition == typeof(IEnumerable<>)
                    || definition == typeof(ICollection<>)
                    || definition == typeof(IReadOnlyCollection<>)) {
                    element = t.GetGenericArguments()[0];
                    return true;
                }
            }

            foreach (var implemented in t.GetInterfaces())
                if (implemented.IsGenericType && implemented.GetGenericTypeDefinition() == typeof(IEnumerable<>)) {
                    element = implemented.GetGenericArguments()[0];
                    return true;
                }

            return false;
        }
    }
}