using Newtonsoft.Json.Linq;
using Nox.Control;

namespace Nox.CCK.Control
{
	/// <summary>
	/// Typed wrapper for operator output. Replaces raw JToken in the API.
	/// </summary>
	public class OperatorOutput : IOutput
	{
		public OperatorOutput(JToken raw)
			=> Raw = raw;

		public JToken Raw { get; }

		public static OperatorOutput Ok(object value)
			=> new(JToken.FromObject(new { ok = true, value }));

		public static OperatorOutput Error(string message)
			=> new(JToken.FromObject(new { ok = false, error = message }));
	}
}
