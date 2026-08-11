using System;
using System.Collections.Generic;
using System.Linq;
using Cysharp.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Nox.CCK.Control;
using Nox.CCK.Utils;
using Nox.Control.Runtime.Handlers;
using Nox.Control.Runtime.Permissions;
using Nox.Control.Runtime.Registers;
using UnityEngine.Events;
using WebSocketSharp;
using WebSocketSharp.Server;
using Logger = Nox.CCK.Utils.Logger;

namespace Nox.Control.Runtime.Server {
	public class Service : WebSocketBehavior {
		public WebSocket                            Server;
		public UnityEvent<Client>                   OnOpenCallback    = new();
		public UnityEvent<Client>                   OnCloseCallback   = new();
		public UnityEvent<Client, string, object[]> OnMessageCallback = new();

		public Client Client;

		/// <summary>
		/// The client's unique permission identifier.
		/// </summary>
		public string ClientId { get; private set; }

		/// <summary>
		/// The current permission entry for this client (null if not yet established).
		/// </summary>
		public RegisteredEntry Entry { get; private set; }

		/// <summary>
		/// Whether this client has been authenticated with a valid token.
		/// </summary>
		public bool IsAuthorized => _identified && Entry != null;


		/// <summary>
		/// Whether the client has identified itself (sent a hello message with its id).
		/// Until then, only the hello handshake is processed.
		/// </summary>
		private bool _identified;

		/// <summary>
		/// Current protocol version. Server and client must agree on this version.
		/// </summary>
		public const int ProtocolVersion = 1;

		protected override void OnOpen() {
			if (Server.IsDisposing) {
				Context.WebSocket.Close();
				return;
			}

			Client = new Client(this, Server);
			OnOpenCallback?.Invoke(Client);
		}

		/// <summary>
		/// Handles the initial identification handshake.
		/// The client sends: { event: "hello", args: [{ id, name, description, version, token?, permissions }] }
		/// If the client has no token and is unknown, a new entry is created and the token is returned.
		/// If the client has an existing entry, the token must match. Invalid tokens close the socket.
		/// </summary>
		private void HandleHello(JObject helloArgs) {
			var endpoint = Context.UserEndPoint?.ToString() ?? "unknown";
			var clientId = helloArgs["id"]?.ToString();
			var clientName = helloArgs["name"];
			var clientDesc = helloArgs["description"];
			var token = helloArgs["token"]?.ToString();
			var version = helloArgs["version"]?.Value<int>() ?? 0;
			var declaredPerms = helloArgs["permissions"] is JArray arr
				? arr.Select(p => p.ToString()).ToArray()
				: Array.Empty<string>();

			if (string.IsNullOrEmpty(clientId)) {
				SendHelloReject("Missing required field 'id'.");
				return;
			}

			if (version != ProtocolVersion) {
				SendHelloReject($"Protocol version mismatch: server={ProtocolVersion}, client={version}");
				return;
			}

			ClientId = clientId;

			// Load existing entry
			Entry = RegistredManager.LoadEntryFile(ClientId);
			var isReturning = Entry != null;

			if (isReturning) {
				// Returning client must provide the correct token
				if (string.IsNullOrEmpty(token) || Entry.Token != token) {
					SendHelloReject("Invalid or missing token for existing client.");
					return;
				}
				// Update declared permissions and metadata (don't overwrite existing states)
				foreach (var p in declaredPerms) {
					var existing = Entry.Permissions?.FirstOrDefault(perm => perm.Id == p);
					if (existing == null)
						Entry.SetPermission(p, PermissionState.Declared);
				}
				if (clientName != null)
					Entry.Name = ParseTranslatedString(clientName, $"WebSocket Client - {endpoint}");
				if (clientDesc != null)
					Entry.Description = ParseTranslatedString(clientDesc, $"External control client connected from {endpoint}");
			} else {
				// New client: create entry with a generated token
				var generatedToken = RegistredManager.GenerateToken();
				Entry = new RegisteredEntry {
					Id = ClientId,
					Name = ParseTranslatedString(clientName, $"WebSocket Client - {endpoint}"),
					Description = ParseTranslatedString(clientDesc, $"External control client connected from {endpoint}"),
					Token = generatedToken,
					FirstConnectedAt = DateTime.UtcNow,
				};
				foreach (var p in declaredPerms)
					Entry.SetPermission(p, PermissionState.Declared);

				Entry.Touch(endpoint);
				RegistredManager.SaveEntryFile(Entry);

				RegistredManager.OnEntryUpdated += OnEntryUpdated;
				_identified = true;

				// Send token to the new client
				SendHelloOk(generatedToken);
				return;
			}

			Entry.Touch(endpoint);
			RegistredManager.SaveEntryFile(Entry);

			RegistredManager.OnEntryUpdated += OnEntryUpdated;
			_identified = true;

			// Returning client: confirm without re-sending token
			SendHelloOk(null);
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

		/// <summary>
		/// Sends a hello acknowledgment. For new clients, includes the generated token.
		/// </summary>
		private void SendHelloOk(string token) {
			if (!IsConnected()) return;
			try {
				var obj = new JObject {
					["ok"] = true,
					["protocol_version"] = ProtocolVersion
				};
				if (token != null)
					obj["token"] = token;

				var msg = new JObject {
					["event"] = "hello",
					["args"] = new JArray { obj }
				}.ToString(Formatting.None);

				Context.WebSocket.Send(msg);
			} catch (Exception ex) {
				Logger.LogWarning($"Failed to send hello ok: {ex.Message}", tag: nameof(Service));
			}
		}

		/// <summary>
		/// Handles a permission request from the client.
		/// Client sends: { event: "permission:request", args: [["config:read", "hierarchy:read"]] }
		/// Server responds: { event: "permission:response", args: [{ allowed: [...], rejected: [...] }] }
		/// </summary>
		private void HandlePermissionRequest(JArray requestedList) {
			if (Entry == null || !IsConnected()) return;

			var requested = requestedList?.Select(p => p.ToString()).ToArray() ?? Array.Empty<string>();
			var allowed = new JArray();
			var pending  = new JArray();
			var newlyRequested = new List<string>();
			var rejected = new JArray();

			foreach (var perm in requested) {
				var declaredIds = Entry.GetPermissionsByState(PermissionState.Declared);
				var grantedIds = Entry.GetPermissionsByState(PermissionState.Granted);
				var deniedIds = Entry.GetPermissionsByState(PermissionState.Denied);

				// Already granted → allow immediately
				if (Entry.HasPermission(perm)) {
					allowed.Add(perm);
					continue;
				}

				// Denied → reject
				if (deniedIds.Contains(perm)) {
					rejected.Add(new JObject { ["id"] = perm, ["reason"] = "Permission denied by admin." });
					continue;
				}

				// Declared but not yet granted → pending admin approval
				if (declaredIds.Contains(perm)) {
					pending.Add(perm);
					newlyRequested.Add(perm);
					continue;
				}

				// Not declared at all → reject
				rejected.Add(new JObject { ["id"] = perm, ["reason"] = "Not declared at hello time." });
			}

			// Fire event for admin to see pending requests
			if (newlyRequested.Count > 0) {
				Nox.Control.Runtime.Permissions.PermissionEvents.OnPermissionRequest.Invoke(new Nox.Control.Runtime.Permissions.PermissionRequestEventArgs {
					ClientId = ClientId,
					ClientName = Entry.Name,
					Endpoint = Context.UserEndPoint as System.Net.IPEndPoint,
					RequestedPermissions = newlyRequested.ToArray()
				});
			}

			try {
				var msg = new JObject {
					["event"] = "permission:response",
					["args"] = new JArray { new JObject {
						["allowed"]  = allowed,
						["pending"]  = pending,
						["rejected"] = rejected
					}}
				}.ToString(Formatting.None);

				Context.WebSocket.Send(msg);
			} catch (Exception ex) {
				Logger.LogWarning($"Failed to send permission:response: {ex.Message}", tag: nameof(Service));
			}
		}

		/// <summary>
		/// Returns the current permission state for this client.
		/// Client sends: { event: "permission:list", args: [] }
		/// Server responds: { event: "permission:list", args: [{ allowed: [...], pending: [...], rejected: [...] }] }
		/// </summary>
		private void HandlePermissionList() {
			if (Entry == null || !IsConnected()) return;

			var allowed = new JArray(Entry.GetPermissionsByState(PermissionState.Granted));
			var pending = new JArray(Entry.GetPermissionsByState(PermissionState.Declared));
			var rejected = new JArray();
			foreach (var perm in Entry.GetPermissionsByState(PermissionState.Denied))
				rejected.Add(new JObject { ["id"] = perm, ["reason"] = "Denied." });

			try {
				var msg = new JObject {
					["event"] = "permission:list",
					["args"] = new JArray { new JObject {
						["allowed"] = allowed,
						["pending"] = pending,
						["rejected"] = rejected
					}}
				}.ToString(Formatting.None);

				Context.WebSocket.Send(msg);
			} catch (Exception ex) {
				Logger.LogWarning($"Failed to send permission:list: {ex.Message}", tag: nameof(Service));
			}
		}

		/// <summary>
		/// Called when a RegisteredEntry is updated externally (via permission_grant/deny/revoke operators).
		/// Reloads the entry from disk.
		/// </summary>
		private void OnEntryUpdated(string clientId) {
			if (clientId != ClientId) return;
			var updated = RegistredManager.LoadEntryFile(ClientId);
			if (updated != null) {
				Entry = updated;
				SendPermissionUpdated();
			}
		}


		/// <summary>
		/// Sends the current permission state to the connected client.
		/// </summary>
		private void SendPermissionUpdated() {
			if (Entry == null || !IsConnected()) return;
			try {
				var granted  = new JArray(Entry.GetPermissionsByState(PermissionState.Granted));
				var declared = new JArray(Entry.GetPermissionsByState(PermissionState.Declared));
				var denied   = new JArray(Entry.GetPermissionsByState(PermissionState.Denied));
				var msg = new JObject {
					["event"] = "permission:updated",
					["args"] = new JArray { new JObject {
						["granted"]  = granted,
						["declared"] = declared,
						["denied"]   = denied
					}}
				}.ToString(Formatting.None);
				Context.WebSocket.Send(msg);
			} catch (Exception ex) {
				Logger.LogWarning($"Failed to send permission:updated: {ex.Message}", tag: nameof(Service));
			}
		}
		/// <summary>
		/// Sends a hello rejection and closes the connection.
		/// </summary>
		private void SendHelloReject(string reason) {
			Logger.LogWarning($"Hello rejected: {reason}", tag: nameof(Service));
			try {
				var msg = new JObject {
					["event"] = "hello:reject",
					["args"] = new JArray { new JObject {
						["reason"] = reason,
						["protocol_version"] = ProtocolVersion
					}}
				}.ToString(Formatting.None);
				Context.WebSocket.Send(msg);
			} catch {
				// best-effort
			}
			Context.WebSocket.Close();
		}

		private bool IsConnected() {
			try {
				return Context?.WebSocket?.ReadyState == WebSocketState.Open;
			} catch {
				return false;
			}
		}

		protected override void OnClose(CloseEventArgs e) {
			if (Client == null) return;
			if (Server.IsDisposing) return;

			// Unsubscribe from entry updates
			RegistredManager.OnEntryUpdated -= OnEntryUpdated;

			// Update last connection info on close
			if (Entry != null) {
				Entry.LastConnectedAt = DateTime.UtcNow;
				RegistredManager.SaveEntryFile(Entry);
			}

			OnCloseCallback.Invoke(Client);
		}

		protected override void OnMessage(MessageEventArgs e)
			=> OnMessageSync(e).Forget();

		private async UniTask OnMessageSync(MessageEventArgs e) {
			if (Server.IsDisposing || !Server.IsRunning()) return;

			try {
				var json = JObject.Parse(e.Data);
				var ev   = json["event"]?.ToString();
				var data = json["args"] as JArray ?? new JArray();

				// Hello handshake: must be the very first message
				if (ev == "hello") {
					await UniTask.SwitchToMainThread();
					HandleHello(data.First as JObject ?? new JObject());
					return;
				}

				// Everything else requires identification
				if (!_identified) {
					Logger.LogWarning("Client sent message before hello handshake, closing.", tag: nameof(Service));
					Context.WebSocket.Close();
					return;
				}

				await UniTask.SwitchToMainThread();

				// Internal events handled by the service itself
				switch (ev) {
				case "permission:request":
					HandlePermissionRequest(data.First as JArray ?? new JArray());
					return;
				case "permission:list":
						HandlePermissionList();
						return;
				}

				// Permission gate: check if this operation requires specific permissions
				if (ev != null) {
					var op = Main.Instance?.GetRegistered()?.FirstOrDefault(o => o.Name == ev);
					var required = op?.RequiredPermissions;
					if (required != null && required.Length > 0) {
						if (Entry == null || !required.Any(r => Entry.HasPermission(r))) {
							await SendPermissionRequired(ev, required);
							return;
						}
					}
					// Execute the operator and send the result back
					var input = data is JArray arr && arr.Count > 0 ? arr[0] : new JObject();
					var result = await Main.Instance.ExecuteAsync(ev, input);
					if (IsConnected()) {
						try {
							var response = new JObject {
								["event"] = ev,
								["args"] = new JArray { result }
							}.ToString(Formatting.None);
							Context.WebSocket.Send(response);
						} catch (Exception ex) {
							Logger.LogWarning($"Failed to send response for {ev}: {ex.Message}", tag: nameof(Service));
						}
					}
				}
			} catch (Exception ex) {
				if (!Server.IsDisposing)
					Logger.LogError($"Error parsing message: {ex.Message}");
			}
		}

		private async UniTask SendPermissionRequired(string eventName, string[] required) {
			if (!IsConnected()) return;

			try {
				var info = new JObject {
					["event"] = eventName,
					["require"] = JArray.FromObject(required)
				};

				var msg = new JObject {
					["event"] = "permission:required",
					["args"] = new JArray { info }
				}.ToString(Formatting.None);

				Context.WebSocket.Send(msg);
			} catch {
				// best-effort
			}

			await UniTask.CompletedTask;
		}

		protected override void OnError(ErrorEventArgs e) {
			if (!Server.IsDisposing) return;
			Logger.LogError(e.Exception);
		}
	}
}