using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using Nox.Control;

namespace Nox.CCK.Control
{
    /// <summary>
    /// Typed wrapper for operator input arguments. Replaces raw JToken in the API.
    /// </summary>
    public class OperatorInput : IInput
    {
        public OperatorInput(JToken raw)
        	=> All = raw.ToObject<Dictionary<string, object>>() ?? new Dictionary<string, object>();

        public Dictionary<string, object> All { get; } = new Dictionary<string, object>();

        IReadOnlyDictionary<string, object> IInput.All
            => All;

        public T Get<T>(string key, bool required = false)
        {
            if (All.TryGetValue(key, out var v))
            {
                if (v is T t)
                    return t;
                if (v is JToken jt)
                    return jt.ToObject<T>();
                return (T)Convert.ChangeType(v, typeof(T));
            }
            if (required)
                throw new KeyNotFoundException($"Required key '{key}' not found in input.");
            return default;
        }

        public bool Has<T>(string key)
            => All.ContainsKey(key) && All[key] is T;

        public static implicit operator OperatorInput(JToken token)
            => token != null ? new OperatorInput(token) : null;
    }
}
