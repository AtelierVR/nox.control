using System;
using System.Linq;
using System.Net;
using Nox.CCK.Utils;

namespace Nox.Control.Runtime {
	/// <summary>
	/// Every configuration value of the control server — the <c>settings.control.*</c> keys of the
	/// Nox config file — with its default, in a single place.
	///
	/// <para>
	/// The values are only read and written through this class (server start-up, MCP installer,
	/// editor panel), so a key is never spelled twice and a default can never drift. Getters never
	/// throw: a missing or unreadable entry falls back to the default, and anything written is
	/// clamped/sanitized so a typo cannot stop the server from starting.
	/// </para>
	///
	/// <para>
	/// Each setter persists the config file immediately (the server reads it from disk on restart).
	/// </para>
	/// </summary>
	public static class ControlConfigs {
		/// <summary>Prefix shared by every key of the control server.</summary>
		public const string Prefix = "settings.control.";

		#region Port

		/// <summary>Config key of the listening port.</summary>
		public const string PortKey = Prefix + "port";

		/// <summary>Port used when nothing is configured.</summary>
		public const int DefaultPort = 50000;

		/// <summary>
		/// Port the server tries to bind. Out of range values are clamped: the server falls back to
		/// a free port anyway when the configured one is taken.
		/// <para>A <c>--control-endpoint</c> command-line override wins over the config file.</para>
		/// </summary>
		public static int Port {
			get => OverridePort ?? ConfiguredPort;
			set => Set(PortKey, Math.Min(65535, Math.Max(1, value)));
		}

		/// <summary>Port stored in the config file, ignoring any <c>--control-endpoint</c> override.</summary>
		public static int ConfiguredPort
			=> Math.Min(65535, Math.Max(1, Config.Load().Get(PortKey, DefaultPort)));

		#endregion

		#region MCP transport

		/// <summary>Config key enabling the MCP transport (<c>POST /mcp</c>).</summary>
		public const string McpEnabledKey = Prefix + "mcp";

		/// <summary>The transport is off by default: the server only exposes REST and WebSocket.</summary>
		public const bool DefaultMcpEnabled = false;

		/// <summary>
		/// Whether the <c>/mcp</c> module is mounted. It is read when the server starts, so a change
		/// only applies after <c>Main.RestartIfRunning()</c>.
		/// </summary>
		public static bool McpEnabled {
			get => Config.Load().Get(McpEnabledKey, DefaultMcpEnabled);
			set => Set(McpEnabledKey, value);
		}

		#endregion

		#region Listen host

		/// <summary>Config key of the host used in the listen URL.</summary>
		public const string ListenHostKey = Prefix + "listen_host";

		/// <summary>Host used when nothing is configured (local machine only).</summary>
		public const string DefaultListenHost = "localhost";

		/// <summary>
		/// Host of the listen URL: <c>localhost</c>, <c>*</c> to accept the network, an IP or a
		/// hostname. Sanitized on write, because an invalid host would make the listen URL invalid.
		/// <para>A <c>--control-endpoint</c> command-line override wins over the config file.</para>
		/// </summary>
		public static string ListenHost {
			get => OverrideHost ?? ConfiguredListenHost;
			set => Set(ListenHostKey, SanitizeHost(value));
		}

		/// <summary>Host stored in the config file, ignoring any <c>--control-endpoint</c> override.</summary>
		public static string ConfiguredListenHost
			=> Config.Load().Get(ListenHostKey, DefaultListenHost);

		/// <summary>
		/// Keeps only the characters a URL host accepts: a stray value would prevent the server from
		/// starting, which is worse than silently falling back to the default host.
		/// </summary>
		private static string SanitizeHost(string value) {
			if (string.IsNullOrWhiteSpace(value))
				return DefaultListenHost;

			var clean = new string(value.Trim()
				.Where(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' or '*' or '[' or ']' or ':')
				.ToArray());

			return string.IsNullOrEmpty(clean) ? DefaultListenHost : clean;
		}

		#endregion

		#region Bind address

		/// <summary>Config key of the address passed to the server (informational / mDNS).</summary>
		public const string AddressKey = Prefix + "address";

		/// <summary>Address used when nothing is configured (all interfaces).</summary>
		public static string DefaultAddress
			=> IPAddress.Any.ToString();

		/// <summary>Raw address value, as text.</summary>
		public static string Address {
			get => Config.Load().Get(AddressKey, DefaultAddress);
			set => Set(AddressKey, string.IsNullOrWhiteSpace(value) ? DefaultAddress : value.Trim());
		}

		/// <summary>
		/// Parsed bind address. An unparsable value is ignored (falls back to all interfaces) so a
		/// bad config can never prevent the server from starting.
		/// </summary>
		public static IPAddress BindAddress {
			get {
				var value = Address;
				return IPAddress.TryParse(value, out var address) ? address : IPAddress.Any;
			}
		}

		#endregion

		#region Token

		/// <summary>Config key of the API token (see <c>McpDispatcher.GetOrCreateToken</c>).</summary>
		public const string TokenKey = Prefix + "_token";

		/// <summary>Stored token, or an empty string when none was generated yet.</summary>
		public static string Token
			=> Config.Load().Get(TokenKey, string.Empty);

		/// <summary>Stores the token (internal: only the token generator writes it).</summary>
		internal static void SetToken(string token)
			=> Set(TokenKey, token);

		#endregion

		#region Command-line override

		/// <summary>
		/// Command-line flag overriding the endpoint <b>without touching the config file</b>, for a
		/// single run: <c>--control-endpoint ":50000"</c> (port only) or
		/// <c>--control-endpoint "0.0.0.0:50000"</c> (host and port).
		/// <para>
		/// Accepted forms: <c>:PORT</c>, <c>HOST:PORT</c>, <c>[IPv6]:PORT</c>, <c>PORT</c>
		/// (port only) or <c>HOST</c> (host only, e.g. <c>*</c>). A missing part keeps the value
		/// from the config file. Both <c>--control-endpoint :50000</c> and
		/// <c>--control-endpoint=:50000</c> work.
		/// </para>
		/// </summary>
		public const string EndpointArg = "control-endpoint";

		private static bool   _overrideParsed;
		private static string _overrideHost;
		private static int?   _overridePort;

		/// <summary>Host forced by <c>--control-endpoint</c>, or <c>null</c> when the config applies.</summary>
		public static string OverrideHost {
			get {
				ParseEndpointOverride();
				return _overrideHost;
			}
		}

		/// <summary>Port forced by <c>--control-endpoint</c>, or <c>null</c> when the config applies.</summary>
		public static int? OverridePort {
			get {
				ParseEndpointOverride();
				return _overridePort;
			}
		}

		/// <summary>True when <c>--control-endpoint</c> overrides at least one value.</summary>
		public static bool IsEndpointOverridden {
			get {
				ParseEndpointOverride();
				return _overridePort.HasValue || _overrideHost != null;
			}
		}

		/// <summary>
		/// Reads <c>--control-endpoint</c> once and stores the host/port it forces. Getters never
		/// throw: an invalid value is logged and ignored, leaving the config file in charge.
		/// </summary>
		private static void ParseEndpointOverride() {
			// Parse once, even on failure: a bad value must not be re-warned on every read.
			if (_overrideParsed)
				return;
			_overrideParsed = true;

			string raw;
			try {
				raw = ArgsParser.Parse().Get(EndpointArg);
			} catch (Exception ex) {
				Logger.LogWarning($"[control] Could not read --{EndpointArg}: {ex.Message}");
				return;
			}

			if (string.IsNullOrWhiteSpace(raw))
				return;

			var value = raw.Trim();

			// Split "host:port". A bare IPv6 literal ("::1", several colons) is treated as a host:
			// only a *bracketed* "[::1]:5000" carries a port.
			string hostPart = null;
			string portPart = null;

			var lastColon   = value.LastIndexOf(':');
			var bracketsEnd = value.LastIndexOf(']');

			if (value.StartsWith("[")) {
				if (bracketsEnd < 0) {
					Logger.LogWarning($"[control] Invalid --{EndpointArg} \"{raw}\" (unclosed IPv6 bracket); ignored.");
					return;
				}
				hostPart = value[..(bracketsEnd + 1)];
				if (lastColon > bracketsEnd)
					portPart = value[(lastColon + 1)..];
			} else if (lastColon >= 0 && value.IndexOf(':') == lastColon) {
				// Exactly one colon: "host:port" or ":port".
				hostPart = value[..lastColon];
				portPart = value[(lastColon + 1)..];
			} else if (lastColon < 0 && value.All(char.IsDigit)) {
				// No colon, all digits: a bare port.
				portPart = value;
			} else {
				// No colon (or an unbracketed IPv6 literal): a host.
				hostPart = value;
			}

			if (!string.IsNullOrWhiteSpace(portPart)) {
				if (int.TryParse(portPart.Trim(), out var port))
					_overridePort = Math.Min(65535, Math.Max(1, port));
				else {
					Logger.LogWarning($"[control] Invalid port \"{portPart.Trim()}\" in --{EndpointArg}; ignored.");
					return;
				}
			}

			if (!string.IsNullOrWhiteSpace(hostPart))
				_overrideHost = SanitizeHost(hostPart);

			if (!_overridePort.HasValue && _overrideHost == null) {
				Logger.LogWarning($"[control] --{EndpointArg} \"{raw}\" overrides nothing; ignored.");
				return;
			}

			Logger.Log(
				$"[control] Endpoint overridden by --{EndpointArg}: "
				+ $"host={_overrideHost ?? "(config)"}, port={_overridePort?.ToString() ?? "(config)"}."
			);
		}

		#endregion

		#region Helpers

		/// <summary>Writes a value and persists the config immediately.</summary>
		private static void Set(string key, object value) {
			var config = Config.Load();
			config.Set(key, value);
			config.Save();
		}

		#endregion
	}
}
