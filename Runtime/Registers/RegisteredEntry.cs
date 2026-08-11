using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Nox.CCK.Convertors;
using Nox.CCK.Language;

namespace Nox.Control.Runtime.Registers {

	/// <summary>
	/// State of a single permission within a RegisteredEntry.
	/// </summary>
	public enum PermissionState {
		/// <summary>Declared at hello time, not yet granted.</summary>
		Declared,
		/// <summary>Explicitly granted (via permission_request or admin).</summary>
		Granted,
		/// <summary>Explicitly denied by admin.</summary>
		Denied
	}

	/// <summary>
	/// A single permission within a RegisteredEntry.
	/// </summary>
	[Serializable]
	public class Permission {
		[JsonProperty("type")]
		public PermissionState Type { get; set; }

		[JsonProperty("id")]
		public string Id { get; set; }
	}

	/// <summary>
	/// Represents a registered websocket client identified by its unique id.
	/// Stores authentication token, metadata, and a list of declared/granted/denied permissions.
	/// </summary>
	[Serializable]
	public class RegisteredEntry {
		/// <summary>
		/// Unique identifier for this websocket client (provided by the client itself).
		/// </summary>
		[JsonProperty("id")]
		public string Id { get; set; }

		/// <summary>
		/// Human-readable name or label for this connection, supporting multiple languages.
		/// </summary>
		[JsonProperty("name"), JsonConverter(typeof(TranslatedStringConverter))]
		public TranslatedString Name { get; set; } = new();

		/// <summary>
		/// Human-readable description for this connection, supporting multiple languages.
		/// </summary>
		[JsonProperty("description"), JsonConverter(typeof(TranslatedStringConverter))]
		public TranslatedString Description { get; set; } = new();

		/// <summary>
		/// Authentication token used to verify the identity of this websocket on reconnection.
		/// </summary>
		[JsonProperty("token")]
		public string Token { get; set; }

		/// <summary>
		/// The list of permissions for this client, each with a state (Declared, Granted, Denied).
		/// </summary>
		[JsonProperty("permissions")]
		public List<Permission> Permissions { get; set; } = new();

		/// <summary>
		/// UTC timestamp of the very first connection from this client.
		/// </summary>
		[JsonProperty("first_connected_at"), JsonConverter(typeof(UnixTimestampToDateTime))]
		public DateTime FirstConnectedAt { get; set; } = DateTime.UtcNow;

		/// <summary>
		/// UTC timestamp of the most recent connection from this client.
		/// </summary>
		[JsonProperty("last_connected_at"), JsonConverter(typeof(UnixTimestampToDateTime))]
		public DateTime LastConnectedAt { get; set; } = DateTime.UtcNow;

		/// <summary>
		/// IP address of the client at last connection.
		/// </summary>
		[JsonProperty("endpoint")]
		public string Endpoint { get; set; }

		/// <summary>
		/// Resolves a permission identifier to its localized description.
		/// </summary>
		public static string GetPermissionDescription(string permission)
			=> LanguageManager.Get(permission.Replace(':', '.').Insert(0, "permission.") + ".description");

		/// <summary>
		/// Returns a dictionary of permission → localized description for the given permissions.
		/// </summary>
		public static Dictionary<string, string> GetLocalizedPermissions(IEnumerable<string> permissions)
			=> permissions.ToDictionary(p => p, GetPermissionDescription);

		/// <summary>
		/// Returns true if this client has been granted a specific permission.
		/// </summary>
		public bool HasPermission(string permission)
			=> Permissions.Any(p => p.Id == permission && p.Type == PermissionState.Granted);

		/// <summary>
		/// Returns all permission ids for a given state.
		/// </summary>
		public string[] GetPermissionsByState(PermissionState state)
			=> Permissions.Where(p => p.Type == state).Select(p => p.Id).ToArray();

		/// <summary>
		/// Sets a permission to a specific state. Adds it if not present.
		/// </summary>
		public void SetPermission(string id, PermissionState state) {
			var existing = Permissions.FirstOrDefault(p => p.Id == id);
			if (existing != null)
				existing.Type = state;
			else
				Permissions.Add(new Permission { Id = id, Type = state });
		}

		/// <summary>
		/// Touch the last-connected timestamp (call on each new connection).
		/// </summary>
		public void Touch(string endpoint) {
			LastConnectedAt = DateTime.UtcNow;
			Endpoint = endpoint;
		}
	}
}
