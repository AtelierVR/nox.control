using System;
using System.Linq;
using Cysharp.Threading.Tasks;
using Logger = Nox.CCK.Utils.Logger;

namespace Nox.Control.Runtime {
	/// <summary>
	/// Sends events to the clients connected to the control server — the streaming side of the API.
	/// <para>
	/// A tool call answers once, so anything progressive (log lines, progress, partial results) has
	/// to be pushed while the operation runs. This is that channel: WebSocket clients receive
	/// <c>{ event, args }</c> messages and can follow a long operation live, while HTTP/MCP clients
	/// get the final result (and, for MCP, the usual single response).
	/// </para>
	/// <para>
	/// Never throws: with no server (startup, shutdown, editor outside play mode) it does nothing,
	/// and a client that fails to receive cannot break the code that is emitting.
	/// </para>
	/// </summary>
	public static class ControlEvents {
		/// <summary>
		/// Broadcasts <paramref name="name"/> to the connected clients, optionally restricted to the
		/// ones allowed to <paramref name="permission"/>.
		/// </summary>
		/// <returns>How many clients the event was sent to.</returns>
		public static int Broadcast(string name, object payload = null, string permission = null) {
			try {
				var server = Main.Server;
				if (server == null || !server.IsRunning() || string.IsNullOrEmpty(name))
					return 0;

				var clients = string.IsNullOrEmpty(permission)
					? server.GetClients()
					: server.GetAuthorizedClients(permission);

				if (clients == null || clients.Length == 0)
					return 0;

				var sent = 0;
				foreach (var client in clients) {
					if (client == null)
						continue;

					client.Send(name, payload).Forget();
					sent++;
				}

				return sent;
			} catch (Exception e) {
				Logger.LogWarning($"Broadcast '{name}' failed: {e.Message}", tag: nameof(ControlEvents));
				return 0;
			}
		}

		/// <summary>Broadcasts to the clients allowed to read the given permission (alias for readability).</summary>
		public static int BroadcastTo(string permission, string name, object payload = null)
			=> Broadcast(name, payload, permission);

		/// <summary>Clients currently connected (empty when no server is running).</summary>
		public static int ClientCount {
			get {
				try {
					return Main.Server?.GetClients()?.Length ?? 0;
				} catch {
					return 0;
				}
			}
		}

		/// <summary>Clients allowed to use a permission.</summary>
		public static string[] AuthorizedClients(string permission) {
			try {
				return Main.Server?.GetAuthorizedClients(permission)?
					.Select(client => client.ToString())
					.ToArray() ?? Array.Empty<string>();
			} catch {
				return Array.Empty<string>();
			}
		}
	}
}
