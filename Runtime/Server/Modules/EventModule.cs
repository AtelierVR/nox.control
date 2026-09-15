using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;
using EmbedIO.WebSockets;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Nox.Control.Runtime.Handlers;
using Nox.Control.Runtime.Permissions;
using Nox.Control.Runtime.Registers;
using Logger = Nox.CCK.Utils.Logger;

namespace Nox.Control.Runtime.Server.Modules {
	/// <summary>
	/// Endpoint WebSocket d'évènements, monté sur le même port que l'API HTTP
	/// (voir <see cref="ControlServer"/>).
	/// <para>
	/// Le protocole est inchangé par rapport à l'implémentation websocket-sharp précédente :
	/// handshake « hello » (id, nom, version, jeton), gestion des permissions, puis exécution
	/// des opérateurs à la demande du client.
	/// </para>
	/// <para>
	/// Différence structurelle : EmbedIO n'instancie qu'<b>un seul</b> module pour toutes les
	/// connexions (là où websocket-sharp créait un <c>WebSocketBehavior</c> par socket), donc
	/// tout l'état par client vit dans <see cref="Connection"/>.
	/// </para>
	/// </summary>
	public class EventModule : WebSocketModule {
		public ControlServer Server;

		/// <summary>Version du protocole : serveur et client doivent s'accorder.</summary>
		public const int ProtocolVersion = 1;

		private readonly ConcurrentDictionary<string, Connection> _connections = new();

		public EventModule(string urlPath, ControlServer server) : base(urlPath, true)
			=> Server = server;

		#region Connexions

		/// <summary>État par connexion (ex-état d'instance du behavior websocket-sharp).</summary>
		private sealed class Connection {
			public readonly IWebSocketContext Context;

			public Client          Client;
			public string          ClientId;
			public RegisteredEntry Entry;
			public bool            Identified;

			public Action<string> OnEntryUpdated;

			public Connection(IWebSocketContext context)
				=> Context = context;

			public bool IsConnected
				=> Context?.WebSocket?.State == System.Net.WebSockets.WebSocketState.Open;

			public bool IsAuthorized
				=> Identified && Entry != null;
		}

		public IClient[] GetClients()
			=> _connections.Values
				.Where(c => c.Client != null)
				.Select(c => (IClient)c.Client)
				.ToArray();

		/// <summary>Clients identifiés (handshake validé) disposant de la permission demandée.</summary>
		public IClient[] GetAuthorizedClients(string permission)
			=> _connections.Values
				.Where(c => c.IsAuthorized && c.Entry.HasPermission(permission))
				.Select(c => c.Client as IClient)
				.Where(client => client != null)
				.ToArray();

		/// <summary>Envoi d'un message texte (interne au module).</summary>
		private Task SendToAsync(Connection connection, string payload)
			=> SendAsync(connection.Context, payload);

		/// <summary>Envoi d'un message texte à une connexion donnée.</summary>
		internal Task SendToAsync(IWebSocketContext context, string payload)
			=> SendAsync(context, payload);

		/// <summary>Fermeture d'une connexion (exposée à <see cref="Client"/>).</summary>
		internal Task CloseAsync(Client client)
			=> client != null ? CloseAsync(client.Context) : Task.CompletedTask;

		private Connection GetConnection(IWebSocketContext context)
			=> context != null && _connections.TryGetValue(context.Id, out var connection) ? connection : null;

		#endregion

		#region Callbacks EmbedIO

		protected override async Task OnClientConnectedAsync(IWebSocketContext context) {
			if (Server.IsDisposing) {
				await CloseAsync(context).ConfigureAwait(false);
				return;
			}

			var connection = new Connection(context) { Client = new Client(this, context) };
			_connections.TryAdd(context.Id, connection);
			Server.OnClientConnected.Invoke(connection.Client);
		}

		protected override async Task OnClientDisconnectedAsync(IWebSocketContext context) {
			if (!_connections.TryRemove(context.Id, out var connection))
				return;

			if (connection.OnEntryUpdated != null)
				RegistredManager.OnEntryUpdated -= connection.OnEntryUpdated;

			if (Server.IsDisposing || connection.Client == null)
				return;

			// Mémorise la date de dernière connexion
			if (connection.Entry != null) {
				connection.Entry.LastConnectedAt = DateTime.UtcNow;
				RegistredManager.SaveEntryFile(connection.Entry);
			}

			Server.OnClientDisconnected.Invoke(connection.Client);
			await Task.CompletedTask;
		}

		protected override async Task OnMessageReceivedAsync(IWebSocketContext context, byte[] buffer, IWebSocketReceiveResult result) {
			if (Server.IsDisposing || !Server.IsRunning())
				return;

			var connection = GetConnection(context);
			if (connection == null)
				return;

			try {
				var json = JObject.Parse(Encoding.UTF8.GetString(buffer));
				var ev   = json["event"]?.ToString();
				var data = json["args"] as JArray ?? new JArray();

				// Handshake « hello » : doit être le tout premier message
				if (ev == "hello") {
					await UniTask.SwitchToMainThread();
					HandleHello(connection, data.First as JObject ?? new JObject());
					return;
				}

				// Tout le reste exige l'identification
				if (!connection.Identified) {
					Logger.LogWarning("Client sent message before hello handshake, closing.", tag: nameof(EventModule));
					await CloseAsync(context).ConfigureAwait(false);
					return;
				}

				await UniTask.SwitchToMainThread();

				// Évènements internes au service
				switch (ev) {
					case "permission:request":
						HandlePermissionRequest(connection, data.First as JArray ?? new JArray());
						return;
					case "permission:list":
						HandlePermissionList(connection);
						return;
				}

				if (ev == null)
					return;

				// Porte de permission : l'opération peut exiger une permission précise
				var op       = Main.Instance?.GetRegistered()?.FirstOrDefault(o => o.Name == ev);
				var required = op?.RequiredPermissions;
				if (required != null && required.Length > 0) {
					if (connection.Entry == null || !required.Any(r => connection.Entry.HasPermission(r))) {
						await SendPermissionRequired(connection, ev, required);
						return;
					}
				}

				// Exécute l'opérateur et renvoie le résultat
				var input  = data is JArray arr && arr.Count > 0 ? arr[0] : new JObject();
				var output = await Main.Instance.ExecuteAsync(ev, input);

				if (connection.IsConnected) {
					try {
						await SendToAsync(
							connection,
							new JObject {
								["event"] = ev,
								["args"]  = new JArray { output }
							}.ToString(Formatting.None)
						);
					} catch (Exception ex) {
						Logger.LogWarning($"Failed to send response for {ev}: {ex.Message}", tag: nameof(EventModule));
					}
				}
			} catch (Exception ex) {
				if (!Server.IsDisposing)
					Logger.LogError($"Error parsing message: {ex.Message}");
			}
		}

		#endregion

		#region Protocole

		/// <summary>
		/// Handshake d'identification. Le client envoie :
		/// { event: "hello", args: [{ id, name, description, version, token?, permissions }] }
		/// Un client inconnu reçoit un jeton généré, un client connu doit fournir le sien.
		/// </summary>
		private void HandleHello(Connection connection, JObject helloArgs) {
			var endpoint      = connection.Context.RemoteEndPoint?.ToString() ?? "unknown";
			var clientId      = helloArgs["id"]?.ToString();
			var clientName    = helloArgs["name"];
			var clientDesc    = helloArgs["description"];
			var token         = helloArgs["token"]?.ToString();
			var version       = helloArgs["version"]?.Value<int>() ?? 0;
			var declaredPerms = helloArgs["permissions"] is JArray arr
				? arr.Select(p => p.ToString()).ToArray()
				: Array.Empty<string>();

			if (string.IsNullOrEmpty(clientId)) {
				SendHelloReject(connection, "Missing required field 'id'.");
				return;
			}

			if (version != ProtocolVersion) {
				SendHelloReject(connection, $"Protocol version mismatch: server={ProtocolVersion}, client={version}");
				return;
			}

			connection.ClientId = clientId;

			// Entrée existante ?
			connection.Entry = RegistredManager.LoadEntryFile(clientId);
			var isReturning = connection.Entry != null;

			if (isReturning) {
				// Un client connu doit présenter le bon jeton
				if (string.IsNullOrEmpty(token) || connection.Entry.Token != token) {
					SendHelloReject(connection, "Invalid or missing token for existing client.");
					return;
				}

				// Met à jour les permissions déclarées et les métadonnées (sans écraser les états)
				foreach (var p in declaredPerms) {
					var existing = connection.Entry.Permissions?.FirstOrDefault(perm => perm.Id == p);
					if (existing == null)
						connection.Entry.SetPermission(p, PermissionState.Declared);
				}
				if (clientName != null)
					connection.Entry.Name = ParseTranslatedString(clientName, $"WebSocket Client - {endpoint}");
				if (clientDesc != null)
					connection.Entry.Description = ParseTranslatedString(clientDesc, $"External control client connected from {endpoint}");
			} else {
				// Nouveau client : entrée créée avec un jeton généré
				var generatedToken = RegistredManager.GenerateToken();
				connection.Entry = new RegisteredEntry {
					Id               = clientId,
					Name             = ParseTranslatedString(clientName, $"WebSocket Client - {endpoint}"),
					Description      = ParseTranslatedString(clientDesc, $"External control client connected from {endpoint}"),
					Token            = generatedToken,
					FirstConnectedAt = DateTime.UtcNow,
				};
				foreach (var p in declaredPerms)
					connection.Entry.SetPermission(p, PermissionState.Declared);

				connection.Entry.Touch(endpoint);
				RegistredManager.SaveEntryFile(connection.Entry);

				SubscribeToEntryUpdates(connection);
				connection.Identified = true;

				SendHelloOk(connection, generatedToken);
				return;
			}

			connection.Entry.Touch(endpoint);
			RegistredManager.SaveEntryFile(connection.Entry);

			SubscribeToEntryUpdates(connection);
			connection.Identified = true;

			// Client connu : confirmation sans renvoyer le jeton
			SendHelloOk(connection, null);
		}

		private static void SubscribeToEntryUpdates(Connection connection) {
			connection.OnEntryUpdated = id => OnEntryUpdated(connection, id);
			RegistredManager.OnEntryUpdated += connection.OnEntryUpdated;
		}

		private static Nox.CCK.Convertors.TranslatedString ParseTranslatedString(JToken token, string fallback) {
			if (token == null) return new Nox.CCK.Convertors.TranslatedString { ["en-US"] = fallback };
			if (token.Type == JTokenType.String)
				return new Nox.CCK.Convertors.TranslatedString { ["en-US"] = token.ToString() };
			try {
				return token.ToObject<Nox.CCK.Convertors.TranslatedString>();
			} catch {
				return new Nox.CCK.Convertors.TranslatedString { ["en-US"] = fallback };
			}
		}

		/// <summary>Accusé de réception du hello (avec le jeton pour un nouveau client).</summary>
		private void SendHelloOk(Connection connection, string token) {
			if (!connection.IsConnected) return;
			try {
				var obj = new JObject {
					["ok"]               = true,
					["protocol_version"] = ProtocolVersion
				};
				if (token != null)
					obj["token"] = token;

				SendToAsync(connection, new JObject {
					["event"] = "hello",
					["args"]  = new JArray { obj }
				}.ToString(Formatting.None)).AsUniTask().Forget();
			} catch (Exception ex) {
				Logger.LogWarning($"Failed to send hello ok: {ex.Message}", tag: nameof(EventModule));
			}
		}

		/// <summary>
		/// Demande de permissions. Le client envoie :
		/// { event: "permission:request", args: [["config:read", "hierarchy:read"]] }
		/// </summary>
		private void HandlePermissionRequest(Connection connection, JArray requestedList) {
			if (connection.Entry == null || !connection.IsConnected) return;

			var requested      = requestedList?.Select(p => p.ToString()).ToArray() ?? Array.Empty<string>();
			var allowed        = new JArray();
			var pending        = new JArray();
			var newlyRequested = new List<string>();
			var rejected       = new JArray();

			foreach (var perm in requested) {
				var declaredIds = connection.Entry.GetPermissionsByState(PermissionState.Declared);
				var deniedIds   = connection.Entry.GetPermissionsByState(PermissionState.Denied);

				// Déjà accordée → autorisé immédiatement
				if (connection.Entry.HasPermission(perm)) {
					allowed.Add(perm);
					continue;
				}

				// Refusée → rejet
				if (deniedIds.Contains(perm)) {
					rejected.Add(new JObject { ["id"] = perm, ["reason"] = "Permission denied by admin." });
					continue;
				}

				// Déclarée mais pas encore accordée → en attente d'un admin
				if (declaredIds.Contains(perm)) {
					pending.Add(perm);
					newlyRequested.Add(perm);
					continue;
				}

				// Jamais déclarée → rejet
				rejected.Add(new JObject { ["id"] = perm, ["reason"] = "Not declared at hello time." });
			}

			if (newlyRequested.Count > 0) {
				PermissionEvents.OnPermissionRequest.Invoke(new Nox.Control.Runtime.Permissions.PermissionRequestEventArgs {
					ClientId             = connection.ClientId,
					ClientName           = connection.Entry.Name,
					Endpoint             = connection.Context.RemoteEndPoint,
					RequestedPermissions = newlyRequested.ToArray()
				});
			}

			try {
				SendToAsync(connection, new JObject {
					["event"] = "permission:response",
					["args"] = new JArray { new JObject {
						["allowed"]  = allowed,
						["pending"]  = pending,
						["rejected"] = rejected
					}}
				}.ToString(Formatting.None)).AsUniTask().Forget();
			} catch (Exception ex) {
				Logger.LogWarning($"Failed to send permission:response: {ex.Message}", tag: nameof(EventModule));
			}
		}

		/// <summary>État courant des permissions du client.</summary>
		private void HandlePermissionList(Connection connection) {
			if (connection.Entry == null || !connection.IsConnected) return;

			var allowed  = new JArray(connection.Entry.GetPermissionsByState(PermissionState.Granted));
			var pending  = new JArray(connection.Entry.GetPermissionsByState(PermissionState.Declared));
			var rejected = new JArray();
			foreach (var perm in connection.Entry.GetPermissionsByState(PermissionState.Denied))
				rejected.Add(new JObject { ["id"] = perm, ["reason"] = "Denied." });

			try {
				SendToAsync(connection, new JObject {
					["event"] = "permission:list",
					["args"] = new JArray { new JObject {
						["allowed"]  = allowed,
						["pending"]  = pending,
						["rejected"] = rejected
					}}
				}.ToString(Formatting.None)).AsUniTask().Forget();
			} catch (Exception ex) {
				Logger.LogWarning($"Failed to send permission:list: {ex.Message}", tag: nameof(EventModule));
			}
		}

		/// <summary>Appelé quand une entrée est modifiée de l'extérieur (grant/deny/revoke).</summary>
		private static void OnEntryUpdated(Connection connection, string clientId) {
			if (clientId != connection.ClientId) return;
			var updated = RegistredManager.LoadEntryFile(connection.ClientId);
			if (updated == null) return;

			connection.Entry = updated;
			SendPermissionUpdated(connection);
		}

		/// <summary>Envoie l'état des permissions au client connecté.</summary>
		private static void SendPermissionUpdated(Connection connection) {
			if (connection.Entry == null || !connection.IsConnected) return;
			try {
				connection.Client?.Send("permission:updated", new JObject {
					["granted"]  = new JArray(connection.Entry.GetPermissionsByState(PermissionState.Granted)),
					["declared"] = new JArray(connection.Entry.GetPermissionsByState(PermissionState.Declared)),
					["denied"]   = new JArray(connection.Entry.GetPermissionsByState(PermissionState.Denied))
				}).Forget();
			} catch (Exception ex) {
				Logger.LogWarning($"Failed to send permission:updated: {ex.Message}", tag: nameof(EventModule));
			}
		}

		/// <summary>Rejette le handshake et ferme la connexion.</summary>
		private void SendHelloReject(Connection connection, string reason) {
			Logger.LogWarning($"Hello rejected: {reason}", tag: nameof(EventModule));
			try {
				SendToAsync(connection, new JObject {
					["event"] = "hello:reject",
					["args"] = new JArray { new JObject {
						["reason"]           = reason,
						["protocol_version"] = ProtocolVersion
					}}
				}.ToString(Formatting.None)).AsUniTask().Forget();
			} catch {
				// best-effort
			}

			CloseAsync(connection.Context).AsUniTask().Forget();
		}

		private async UniTask SendPermissionRequired(Connection connection, string eventName, string[] required) {
			if (!connection.IsConnected) return;

			try {
				var info = new JObject {
					["event"]   = eventName,
					["require"] = JArray.FromObject(required)
				};

				await SendToAsync(connection, new JObject {
					["event"] = "permission:required",
					["args"]  = new JArray { info }
				}.ToString(Formatting.None));
			} catch {
				// best-effort
			}
		}

		#endregion
	}
}
