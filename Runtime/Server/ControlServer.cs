using System;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;
using EmbedIO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Nox.Control.Runtime.Server.Modules;
using Nox.Control.Server;
using UnityEngine.Events;
using Logger = Nox.CCK.Utils.Logger;

namespace Nox.Control.Runtime.Server {
	/// <summary>
	/// Serveur de contrôle : <b>un seul port</b> pour tout, via EmbedIO.
	/// <list type="bullet">
	///   <item><description>WebSocket d'évènements sur <c>/</c> (voir <see cref="EventModule"/>)</description></item>
	///   <item><description>API REST sur <c>/api/*</c> (voir <see cref="ApiModule"/>)</description></item>
	///   <item><description>MCP JSON-RPC sur <c>POST /mcp</c> (voir <see cref="McpModule"/>)</description></item>
	/// </list>
	/// <para>
	/// Les routes sont disjointes, ce qui évite toute ambiguïté de résolution entre modules
	/// (contrairement à deux serveurs sur deux ports, le client n'a qu'une adresse à connaître).
	/// </para>
	/// <para>
	/// EmbedIO écoute le socket lui-même (<c>HttpListenerMode.EmbedIO</c>) : plus de
	/// <c>HttpListener</c>, donc plus besoin du <c>netsh http add urlacl</c> qu'exigeait
	/// l'ancienne API HTTP séparée.
	/// </para>
	/// </summary>
	public class ControlServer : IServer {
		private readonly string _address;
		private readonly int    _port;
		private readonly bool   _enableMdns;
		private readonly string _mdnsServiceName;

		private readonly WebServer    _server;
		private readonly EventModule  _events;

		private MdnsService _mdnsService;
		private bool        _isRunning;

		/// <summary>Vrai pendant la fermeture : les callbacks doivent alors ne plus rien émettre.</summary>
		public bool IsDisposing;

		public readonly UnityEvent<Client>                   OnClientConnected    = new();
		public readonly UnityEvent<Client>                   OnClientDisconnected = new();

		/// <summary>
		/// Levé pour chaque message reçu d'un client. Conservé pour l'API publique du mod ;
		/// comme dans l'implémentation précédente, le socket d'évènements exécute lui-même
		/// les opérateurs et ne lève pas cet évènement.
		/// </summary>
		public readonly UnityEvent<Client, string, object[]> OnEventReceived      = new();

		public bool EnableMcp { get; set; }

		public ControlServer(
			IPAddress address,
			int port,
			bool enableMdns = true,
			string mdnsServiceName = "Nox Control Server",
			bool enableMcp = false
		) {
			_address         = address.ToString();
			_port            = port;
			_enableMdns      = enableMdns;
			_mdnsServiceName = mdnsServiceName;
			EnableMcp        = enableMcp;

			_events = new EventModule("/", this);

			_server = new WebServer(o => o
				.WithUrlPrefix($"http://*:{_port}/")
				.WithMode(HttpListenerMode.EmbedIO)
			);

			// ⚠ L'ORDRE D'ENREGISTREMENT EST L'ORDRE DE RÉSOLUTION.
			// `WebModuleCollection.DispatchRequestAsync` itère les modules dans l'ordre
			// d'ajout, s'arrête au premier dont la route matche le chemin demandé, et sort
			// dès que la requête est traitée.
			// Or une route racine ("/") matche TOUTES les URLs : `EventModule` monté sur
			// "/" avale donc /api et /mcp. Comme il ne répond rien pour un chemin != "/"
			// (son OnRequestAsync retourne sans écrire) tout en étant IsFinalHandler
			// (sealed), EmbedIO envoie un 200 vide en text/html — ce que le client MCP
			// rejette avec « Unexpected 200 response for request:  » puis attend
			// indéfiniment une réponse à `initialize`.
			// ⇒ les modules à route spécifique DOIVENT être enregistrés avant lui.
			_server.WithModule(new ApiModule("/api"));   // REST  → "/api/*"
			if (EnableMcp)
				_server.WithModule(new McpModule("/mcp")); // MCP → "POST /mcp"
			_server.WithModule(_events);                 // WebSocket → "/" : catch-all, en DERNIER
		}

		public void Listen() {
			if (_isRunning) return;

			try {
				_server.Start();
				_isRunning = true;
				Logger.Log($"Started on {_address}:{_port}", tag: nameof(ControlServer));

				// Start mDNS advertising if enabled
				if (_enableMdns) {
					try {
						_mdnsService = new MdnsService(
							_mdnsServiceName, "_nctrl._tcp", (ushort)_port,
							$"address={_address}",
							$"protocol=websocket",
							$"version=1.0"
						);
						_mdnsService.Start();
					} catch (Exception mdnsEx) {
						Logger.LogError(new Exception("Failed to start mDNS advertising (server will continue without it)", mdnsEx), tag: nameof(ControlServer));
					}
				}
			} catch (Exception ex) {
				Logger.LogError(new Exception($"Failed to start server on {_address}:{_port}", ex), tag: nameof(ControlServer));
				throw;
			}
		}

		public void Dispose() {
			if (!_isRunning && !IsDisposing) return;
			IsDisposing = true;
			_isRunning  = false;

			try {
				// Stop mDNS advertising first
				try {
					_mdnsService?.Stop();
					_mdnsService?.Dispose();
					_mdnsService = null;
				} catch (Exception ex) {
					Logger.LogError(new Exception("Error stopping mDNS service", ex), tag: nameof(ControlServer));
				}

				// Disconnect all clients
				foreach (var client in GetClients())
					try {
						client.Close().Forget();
					} catch (Exception ex) {
						Logger.LogError(new Exception("Error disconnecting client", ex), tag: nameof(ControlServer));
					}

				// Stop the server: EmbedIO dispose libère le socket d'écoute (le port est
				// rendu immédiatement au système, ce qui évite les conflits au reload suivant).
				try {
					_server?.Dispose();
				} catch (Exception ex) {
					Logger.LogError(new Exception("Error stopping server", ex), tag: nameof(ControlServer));
				}

				Logger.Log($"Server stopped on {_address}:{_port}", tag: nameof(ControlServer));
			} catch (Exception ex) {
				Logger.LogError(new Exception("Error stopping server", ex), tag: nameof(ControlServer));
			} finally {
				IsDisposing = false;
			}
		}

		public UniTask Broadcast(string ev, params object[] args)
			=> UniTask.WhenAll(GetClients().Select(client => client.Send(ev, args)));

		public int GetPort()
			=> _port;

		public bool IsRunning()
			=> _isRunning;

		public IClient[] GetClients()
			=> _events.GetClients();

		/// <summary>
		/// Clients identifiés (handshake « hello » validé) disposant de la permission demandée.
		/// </summary>
		public IClient[] GetAuthorizedClients(string permission)
			=> _events.GetAuthorizedClients(permission);

		// ── Helpers partagés par les modules HTTP ───────────────────────────────

		/// <summary>
		/// Chemin demandé, relatif à la route du module. EmbedIO peut fournir le chemin complet
		/// ou relatif selon la stratégie de routage : on gère les deux.
		/// </summary>
		internal static string RelativePath(IHttpContext context, string baseRoute) {
			var path = context.RequestedPath ?? "/";
			if (path.StartsWith(baseRoute, StringComparison.OrdinalIgnoreCase))
				path = path.Substring(baseRoute.Length);
			return path.Trim('/');
		}

		/// <summary>Écrit une réponse JSON (CORS ouvert, comme l'ancienne API HTTP).</summary>
		internal static async Task SendJsonAsync(IHttpContext context, int statusCode, JToken data) {
			context.Response.StatusCode = statusCode;
			await context.SendStringAsync(data.ToString(Formatting.None), "application/json", Encoding.UTF8);
		}

		/// <summary>Vrai si la requête porte le jeton d'API attendu (Bearer).</summary>
		internal static bool IsAuthorized(IHttpContext context) {
			var configuredToken = McpDispatcher.GetOrCreateToken();
			if (string.IsNullOrEmpty(configuredToken))
				return true;

			var authHeader = context.Request.Headers["Authorization"];
			return !string.IsNullOrEmpty(authHeader)
				&& string.Equals(authHeader, "Bearer " + configuredToken, StringComparison.Ordinal);
		}
	}
}
