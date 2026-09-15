using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json.Linq;
using Nox.Control;

namespace Nox.CCK.Control
{
    /// <summary>
    /// Typed wrapper for operator input arguments. Replaces raw JToken in the API.
    /// <para>
    /// Values are coerced defensively so a call never fails on a mere type mismatch:
    /// MCP clients frequently send arrays/objects as strings (e.g. <c>path: "[-1,-8910]"</c>
    /// instead of <c>path: [-1,-8910]</c>), and nested JSON arrives as <see cref="JToken"/> or
    /// <c>object[]</c>. <see cref="Convert.ChangeType"/> handles none of those, so we
    /// round-trip through JSON instead.
    /// </para>
    /// </summary>
    public class OperatorInput : IInput
    {
        public OperatorInput(JToken raw)
        	=> All = raw?.ToObject<Dictionary<string, object>>() ?? new Dictionary<string, object>();

        public Dictionary<string, object> All { get; } = new Dictionary<string, object>();

        IReadOnlyDictionary<string, object> IInput.All
            => All;

        public T Get<T>(string key, bool required = false)
        {
            if (All.TryGetValue(key, out var v) && v != null)
                return Coerce<T>(v, key);

            if (required)
                throw new KeyNotFoundException($"Required key '{key}' not found in input.");
            return default;
        }

        public bool Has<T>(string key)
        {
            if (!All.TryGetValue(key, out var v) || v == null)
                return false;

            try {
                Coerce<T>(v, key);
                return true;
            } catch {
                return false;
            }
        }

        /// <summary>
        /// Converts a raw argument value to <typeparamref name="T"/>, accepting both the native
        /// JSON type and its stringified form.
        /// </summary>
        private static T Coerce<T>(object v, string key)
        {
            // Fast path: already the requested type (also covers T == object).
            if (v is T same)
                return same;

            var target = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);

            try {
                if (v is string s)
                    return (T)FromString(s, target);

                // JToken (nested arrays/objects) and plain collections both round-trip
                // correctly through JSON, unlike Convert.ChangeType.
                if (v is JToken token)
                    return token.ToObject<T>();

                if (target.IsArray || v is IEnumerable and not string)
                    return JToken.FromObject(v).ToObject<T>();

                if (target.IsEnum)
                    return (T)Enum.ToObject(target, Convert.ChangeType(v, Enum.GetUnderlyingType(target)));

                return (T)Convert.ChangeType(v, target, CultureInfo.InvariantCulture);
            } catch (Exception ex) {
                throw new ArgumentException(
                    $"Invalid value for '{key}': cannot convert {Describe(v)} to {typeof(T).Name}. ({ex.Message})",
                    key
                );
            }
        }

        private static object FromString(string s, Type target)
        {
            if (target == typeof(string))
                return s;

            if (target.IsEnum)
                return Enum.Parse(target, s, ignoreCase: true);

            var trimmed = s.Trim();

            // Comma-separated shorthand for arrays: "-1,-8910" → "[-1,-8910]".
            if (target.IsArray && trimmed.Length > 0 && trimmed[0] != '[' && trimmed.IndexOf(',') >= 0)
                trimmed = "[" + trimmed + "]";

            // A string is a valid JSON payload for arrays/objects ("[-1,2]", "{\"a\":1}"),
            // and bare scalars ("42", "true", "1.5") parse as JSON too.
            return JToken.Parse(trimmed).ToObject(target);
        }

        private static string Describe(object v)
            => v == null ? "null" : $"'{v}' ({v.GetType().Name})";

        public static implicit operator OperatorInput(JToken token)
            => token != null ? new OperatorInput(token) : null;
    }

    /// <summary>
    /// Optional <c>offset</c>/<c>limit</c> arguments shared by the listing operators, so a
    /// caller can walk a large result instead of pulling everything at once (a single
    /// <c>hierarchy_get</c> on a player rig can otherwise span hundreds of KB).
    /// <para>
    /// Semantics: <c>offset</c> = index of the first item, <c>limit</c> = maximum number of
    /// items. A missing/short <c>limit</c> (or <c>0</c>) means "no limit", which keeps the
    /// behaviour of callers that don't use pagination.
    /// </para>
    /// </summary>
    public readonly struct Pagination
    {
        /// <summary>Index of the first item to return (never negative).</summary>
        public readonly int Offset;

        /// <summary>Maximum number of items to return; <c>0</c> means no limit.</summary>
        public readonly int Limit;

        public bool IsUnbounded
            => Limit <= 0;

        private Pagination(int offset, int limit) {
            Offset = offset;
            Limit  = limit;
        }

        /// <summary>Reads the pagination arguments from an operator input.</summary>
        public static Pagination Read(IInput args)
            => new(Math.Max(0, args.Get<int>("offset")), Math.Max(0, args.Get<int>("limit")));

        /// <summary>
        /// Slices <paramref name="source"/> to the requested window and reports
        /// <paramref name="total"/>, the count before slicing, so the caller can tell whether
        /// more items are available.
        /// </summary>
        public T[] Apply<T>(T[] source, out int total) {
            total = source?.Length ?? 0;
            if (source == null || source.Length == 0)
                return Array.Empty<T>();

            // Fast path: no pagination requested — return the original array untouched.
            if (Offset == 0 && IsUnbounded)
                return source;

            var skip = Math.Min(Offset, source.Length);
            var take = IsUnbounded ? source.Length - skip : Math.Min(Limit, source.Length - skip);
            if (take <= 0)
                return Array.Empty<T>();

            var slice = new T[take];
            Array.Copy(source, skip, slice, 0, take);
            return slice;
        }
    }
}
