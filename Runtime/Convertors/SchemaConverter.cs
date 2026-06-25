using System;
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
                var propObj = new JObject {
                    ["type"] = ToJsonType(p.Type),
                    ["description"] = p.Description ?? ""
                };

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

        private static string ToJsonType(Type t) {
            if (t == typeof(string))
                return "string";
            if (t == typeof(int) || t == typeof(long) || t == typeof(float) || t == typeof(double) || t == typeof(decimal))
                return "number";
            if (t == typeof(bool))
                return "boolean";
            if (t == typeof(object) || t == typeof(JToken) || t == typeof(JObject) || t == typeof(JArray))
                return "object";
            return "string";
        }
    }
}