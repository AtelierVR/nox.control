using System;
using System.Linq;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;
using EmbedIO;
using Newtonsoft.Json.Linq;

namespace Nox.Control.Runtime.Server.Modules {
	/// <summary>
	/// Module HTTP de l'API REST du serveur de contrôle, monté sur <c>/api</c>.
	/// <list type="bullet">
	///   <item><description><c>GET  /api</c> — index (version + liste des endpoints)</description></item>
	///   <item><description><c>GET  /api/tools</c> — opérateurs au format MCP (name/description/inputSchema)</description></item>
	///   <item><description><c>GET  /api/operations</c> — noms des opérateurs</description></item>
	///   <item><description><c>POST /api/call/{name}</c> — exécute un opérateur (jeton requis)</description></item>
	/// </list>
	/// </summary>
	internal sealed class ApiModule : WebModuleBase {
		public ApiModule(string baseRoute) : base(baseRoute) { }

		/// <inheritdoc />
		public override bool IsFinalHandler => true;

		protected override async Task OnRequestAsync(IHttpContext context) {
			var method = context.Request.HttpMethod?.ToUpperInvariant();
			var path   = ControlServer.RelativePath(context, BaseRoute);

			if (method == "OPTIONS") {
				context.Response.SetEmptyResponse(204);
				return;
			}

			switch (method) {
				case "GET" when path.Length == 0:
					await ControlServer.SendJsonAsync(context, 200, JObject.FromObject(new {
						service    = "Nox.Control",
						version    = "1.0",
						transport  = "embedio",
						operations = Main.Instance?.GetRegistered().Length ?? 0,
						endpoints  = new[] {
							"GET  /api/tools",
							"GET  /api/operations",
							"POST /api/call/{name}",
							"POST /mcp",
							"WS   /"
						}
					}));
					return;

				case "GET" when path == "tools": {
					var tools = new JArray();
					if (Main.Instance != null)
						foreach (var op in Main.Instance.GetRegistered())
							tools.Add(new JObject {
								["name"]        = op.Name,
								["description"] = op.Description,
								["inputSchema"] = op.Schema.ToJObject()
							});
					await ControlServer.SendJsonAsync(context, 200, JObject.FromObject(new { tools }));
					return;
				}

				case "GET" when path == "operations": {
					var names = Main.Instance != null
						? new JArray(Main.Instance.GetRegistered().Select(op => op.Name))
						: new JArray();
					await ControlServer.SendJsonAsync(context, 200, names);
					return;
				}

				case "POST" when path.StartsWith("call/", StringComparison.OrdinalIgnoreCase): {
					if (!ControlServer.IsAuthorized(context)) {
						await ControlServer.SendJsonAsync(context, 401, JObject.FromObject(new { error = "Unauthorized" }));
						return;
					}

					var operationName = path.Substring("call/".Length);
					var body          = await context.GetRequestBodyAsStringAsync();
					JToken args       = null;
					if (!string.IsNullOrEmpty(body)) {
						try { args = JToken.Parse(body); }
						catch { args = new JValue(body); }
					}

					// Les opérateurs peuvent toucher des API Unity : bascule sur le thread principal.
					await UniTask.SwitchToMainThread();

					var result = Main.Instance != null
						? await Main.Instance.ExecuteAsync(operationName, args)
						: JObject.FromObject(new { error = "Control API not available" });
					await ControlServer.SendJsonAsync(context, 200, result);
					return;
				}
			}

			await ControlServer.SendJsonAsync(context, 404, JObject.FromObject(new { error = "Not found" }));
		}
	}
}
