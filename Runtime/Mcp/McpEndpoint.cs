using System;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Nox.Control.Server;

namespace Nox.Control.Runtime.Mcp {
	/// <summary>
	/// Where the local control server is reachable and with which token: everything an external MCP
	/// client needs to be configured with.
	/// <para>
	/// The values the server is configured with (port, host, transport, token) live in
	/// <see cref="ControlConfigs"/>; this class only exposes the <b>live</b> endpoint.
	/// </para>
	/// </summary>
	public static class McpEndpoint {
		/// <summary>Whether the control server is currently listening (false outside play mode).</summary>
		public static bool IsRunning
			=> Main.Server != null && Main.Server.IsRunning();

		/// <summary>Effective port: the running server's, otherwise the configured one.</summary>
		public static int Port
			=> IsRunning
				? Main.Server.GetPort()
				: ControlConfigs.Port;

		/// <summary>
		/// Host advertised in <see cref="Url"/>.
		/// <para>
		/// The configured <c>listen_host</c> is used as-is when it designates a real host
		/// (<c>localhost</c>, an IP, a machine name), so the endpoint written in the client files can
		/// point to this machine from another machine. With a wildcard bind
		/// (<c>*</c>, <c>0.0.0.0</c>…) no host can be deduced from the configuration, so the machine
		/// name is used, then its first LAN IPv4, then <c>localhost</c>.
		/// </para>
		/// </summary>
		public static string Host {
			get {
				var configured = ControlConfigs.ListenHost;
				if (!IsWildcard(configured))
					return configured;

				return MachineHost ?? LocalAddress ?? ControlConfigs.DefaultListenHost;
			}
		}

		/// <summary>
		/// Access token (generated and persisted on first use). The server only accepts requests
		/// carrying it as <c>Authorization: Bearer &lt;token&gt;</c>.
		/// </summary>
		public static string Token
			=> McpDispatcher.GetOrCreateToken();

		/// <summary>Full MCP endpoint URL.</summary>
		public static string Url
			=> $"http://{Host}:{Port}/mcp";

		/// <summary>Ready-to-write HTTP authorization header value.</summary>
		public static string Authorization
			=> "Bearer " + Token;

		#region Host resolution

		/// <summary>True for the hosts that mean "every interface": they cannot be dialed as-is.</summary>
		private static bool IsWildcard(string host)
			=> string.IsNullOrWhiteSpace(host)
				|| host is "*" or "+" or "0.0.0.0" or "::" or "[::]";

		/// <summary>Machine name (NetBIOS/DNS/mDNS), preferred over a raw address because it is stable.</summary>
		private static string MachineHost {
			get {
				try {
					var name = Environment.MachineName;
					return string.IsNullOrWhiteSpace(name) ? null : name;
				} catch {
					return null;
				}
			}
		}

		/// <summary>
		/// First IPv4 of an active, non-tunnel interface — enough to reach this machine on the LAN.
		/// No name resolution and no traffic, so it cannot block.
		/// </summary>
		private static string LocalAddress {
			get {
				try {
					foreach (var network in NetworkInterface.GetAllNetworkInterfaces()) {
						if (network.OperationalStatus != OperationalStatus.Up)
							continue;
						if (network.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
							continue;

						foreach (var unicast in network.GetIPProperties().UnicastAddresses) {
							if (unicast.Address.AddressFamily != AddressFamily.InterNetwork)
								continue;
							if (IPAddress.IsLoopback(unicast.Address))
								continue;

							return unicast.Address.ToString();
						}
					}
				} catch {
					// No interface could be read: the caller falls back to localhost.
				}

				return null;
			}
		}

		#endregion
	}
}
