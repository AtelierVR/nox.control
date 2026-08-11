using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Cysharp.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Nox.CCK.Control;
using Nox.CCK.Utils;
using Nox.Control;
using Nox.Control.Runtime.Permissions;
using Nox.Control.Runtime.Registers;

namespace Nox.Control.Runtime.Handlers {
	/// <summary>
	/// Static manager for websocket permission entries.
	/// Persists each client as a JSON file in ConfigAPI.GetFolder()/known/.
	/// </summary>
	public static class RegistredManager {

		/// <summary>
		/// Fired when a permission entry is updated (granted, denied, revoked).
		/// The string parameter is the client ID that was updated.
		/// </summary>
		public static event Action<string> OnEntryUpdated;

		/// <summary>
		/// Notify listeners that an entry was updated.
		/// </summary>
		public static void NotifyEntryUpdated(string clientId)
			=> OnEntryUpdated?.Invoke(clientId);

		/// <summary>
		/// Persist a single RegisteredEntry to a file.
		/// </summary>
		public static void SaveEntryFile(RegisteredEntry entry) {
			if (string.IsNullOrEmpty(entry?.Id)) return;

			var folder = GetDefaultConfigFolder();
			if (folder == null) return;

			try {
				Directory.CreateDirectory(folder);
				var path = Path.Combine(folder, $"{SanitizeFileName(entry.Id)}.json");
				var json = JsonConvert.SerializeObject(entry, Formatting.Indented);
				File.WriteAllText(path, json);
			} catch (Exception ex) {
				Logger.LogWarning($"Failed to save entry file for {entry.Id}: {ex.Message}", tag: nameof(RegistredManager));
			}
		}

		/// <summary>
		/// Load a single RegisteredEntry from its file.
		/// </summary>
		public static RegisteredEntry LoadEntryFile(string clientId, string configFolder = null) {
			if (string.IsNullOrEmpty(clientId)) return null;

			var folder = configFolder ?? GetDefaultConfigFolder();
			if (folder == null) return null;

			try {
				var path = Path.Combine(folder, $"{SanitizeFileName(clientId)}.json");
				if (!File.Exists(path)) return null;
				var json = File.ReadAllText(path);
				return JsonConvert.DeserializeObject<RegisteredEntry>(json);
			} catch {
				return null;
			}
		}

		/// <summary>
		/// Delete the per-websocket file for a client.
		/// </summary>
		public static void DeleteEntryFile(string clientId, string configFolder = null) {
			if (string.IsNullOrEmpty(clientId)) return;

			var folder = configFolder ?? GetDefaultConfigFolder();
			if (folder == null) return;

			try {
				var path = Path.Combine(folder, $"{SanitizeFileName(clientId)}.json");
				if (File.Exists(path)) File.Delete(path);
			} catch (Exception ex) {
				Logger.LogWarning($"Failed to delete entry file for {clientId}: {ex.Message}", tag: nameof(RegistredManager));
			}
		}

		/// <summary>
		/// Load all entries from individual files.
		/// </summary>
		public static List<RegisteredEntry> LoadAll(string configFolder = null) {
			var folder = configFolder ?? GetDefaultConfigFolder();
			var result = new List<RegisteredEntry>();

			if (folder != null && Directory.Exists(folder)) {
				foreach (var file in Directory.GetFiles(folder, "*.json")) {
					try {
						var json = File.ReadAllText(file);
						var entry = JsonConvert.DeserializeObject<RegisteredEntry>(json);
						if (entry != null)
						result.Add(entry);
					} catch {
						// Skip corrupted files
					}
				}
			}

			return result;
		}

		/// <summary>
		/// Grant a single permission to a client.
		/// </summary>
		public static void GrantPermission(string clientId, string permission) {
			var entry = LoadEntryFile(clientId);
			if (entry == null) return;

			entry.SetPermission(permission, PermissionState.Granted);
			entry.LastConnectedAt = DateTime.UtcNow;
			SaveEntryFile(entry);

			NotifyEntryUpdated(clientId);

			Nox.Control.Runtime.Permissions.PermissionEvents.OnPermissionDecision.Invoke(new Nox.Control.Runtime.Permissions.PermissionDecisionEventArgs {
				ClientId = clientId,
				Decision = Nox.Control.Runtime.Permissions.PermissionDecision.Granted,
				Permissions = new[] { permission }
			});
		}

		/// <summary>
		/// Deny a single permission from a client.
		/// </summary>
		public static void DenyPermission(string clientId, string permission) {
			var entry = LoadEntryFile(clientId);
			if (entry == null) return;

			entry.SetPermission(permission, PermissionState.Denied);
			SaveEntryFile(entry);

			NotifyEntryUpdated(clientId);

			Nox.Control.Runtime.Permissions.PermissionEvents.OnPermissionDecision.Invoke(new Nox.Control.Runtime.Permissions.PermissionDecisionEventArgs {
				ClientId = clientId,
				Decision = Nox.Control.Runtime.Permissions.PermissionDecision.Denied,
				Permissions = new[] { permission }
			});
		}

		/// <summary>
		/// Generate a stable client identifier from connection metadata.
		/// </summary>
		public static string GenerateClientId(string endpoint, string userAgent = null) {
			var raw = $"{endpoint}|{userAgent ?? "unknown"}";
			using var sha = System.Security.Cryptography.SHA256.Create();
			var hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(raw));
			return Convert.ToBase64String(hash).Replace("/", "_").Replace("+", "-").Replace("=", "")[..16];
		}

		/// <summary>
		/// Generate a random authentication token.
		/// </summary>
		public static string GenerateToken() {
			var bytes = new byte[32];
			using var rng = System.Security.Cryptography.RandomNumberGenerator.Create();
			rng.GetBytes(bytes);
			return Convert.ToBase64String(bytes).Replace("/", "_").Replace("+", "-").Replace("=", "");
		}

		private static string GetDefaultConfigFolder()
			=> Path.Combine(Main.CoreAPI.ConfigAPI.GetFolder(), "known");

		private static string SanitizeFileName(string name) {
			foreach (var c in Path.GetInvalidFileNameChars())
				name = name.Replace(c, '_');
			return name;
		}
	}

	#region Operators

	/// <summary>
	/// Operator: permission_admin_list — list all known permission entries (admin).
	/// </summary>
	public class PermissionList : IOperator {
		public string Name => "permission_admin_list";
		public string Description => "List all permission entries for websocket clients (admin).";
		public string[] RequiredPermissions => new[] { "control:admin" };
		public ISchema Schema => new InputSchema()
			.Property<string>("configFolder", "Optional custom config folder path", false);

		public async UniTask<IOutput> Execute(IInput input) {
			var folder = input.Get<string>("configFolder", false);
			var entries = RegistredManager.LoadAll(folder);
			return OperatorOutput.Ok(entries);
		}
	}

	/// <summary>
	/// Operator: permission_grant — grant one or more permissions to a client.
	/// </summary>
	public class PermissionGrant : IOperator {
		public string Name => "permission_grant";
		public string Description => "Grant permissions to a websocket client.";
		public string[] RequiredPermissions => new[] { "control:admin" };
		public ISchema Schema => new InputSchema()
			.Property<string>("clientId", "The client identifier", true)
			.Property<string[]>("permissions", "Specific permissions to grant (empty = all declared)", false)
			.Property<string>("configFolder", "Optional custom config folder path", false);

		public async UniTask<IOutput> Execute(IInput input) {
			var clientId = input.Get<string>("clientId", true);
			var permissions = input.Get<string[]>("permissions", false) ?? Array.Empty<string>();
			var configFolder = input.Get<string>("configFolder", false);

			var entry = RegistredManager.LoadEntryFile(clientId, configFolder);
			if (entry == null)
				return OperatorOutput.Error($"Client '{clientId}' not found.");

			if (permissions.Length == 0) {
				foreach (var p in entry.GetPermissionsByState(PermissionState.Declared))
					entry.SetPermission(p, PermissionState.Granted);
			} else {
				foreach (var p in permissions)
					entry.SetPermission(p, PermissionState.Granted);
			}

			entry.LastConnectedAt = DateTime.UtcNow;
			RegistredManager.SaveEntryFile(entry);

			RegistredManager.NotifyEntryUpdated(clientId);

			Permissions.PermissionEvents.OnPermissionDecision.Invoke(new Nox.Control.Runtime.Permissions.PermissionDecisionEventArgs {
				ClientId = clientId,
				Decision = Nox.Control.Runtime.Permissions.PermissionDecision.Granted,
				Permissions = permissions.Length == 0
					? entry.GetPermissionsByState(PermissionState.Declared)
					: permissions
			});

			return OperatorOutput.Ok(entry);
		}
	}

	/// <summary>
	/// Operator: permission_deny — deny a permission request.
	/// </summary>
	public class PermissionDeny : IOperator {
		public string Name => "permission_deny";
		public string Description => "Deny a permission request from a websocket client.";
		public string[] RequiredPermissions => new[] { "control:admin" };
		public ISchema Schema => new InputSchema()
			.Property<string>("clientId", "The client identifier to deny", true)
			.Property<string>("configFolder", "Optional custom config folder path", false);

		public async UniTask<IOutput> Execute(IInput input) {
			var clientId = input.Get<string>("clientId", true);
			var configFolder = input.Get<string>("configFolder", false);

			var entry = RegistredManager.LoadEntryFile(clientId, configFolder);
			if (entry == null)
				return OperatorOutput.Error($"Client '{clientId}' not found.");

			foreach (var p in entry.GetPermissionsByState(PermissionState.Granted))
				entry.SetPermission(p, PermissionState.Denied);
			RegistredManager.SaveEntryFile(entry);

			RegistredManager.NotifyEntryUpdated(clientId);

			Permissions.PermissionEvents.OnPermissionDecision.Invoke(new Nox.Control.Runtime.Permissions.PermissionDecisionEventArgs {
				ClientId = clientId,
				Decision = Nox.Control.Runtime.Permissions.PermissionDecision.Denied
			});

			return OperatorOutput.Ok(entry);
		}
	}

	/// <summary>
	/// Operator: permission_revoke — revoke all permissions and delete the entry.
	/// </summary>
	public class PermissionRevoke : IOperator {
		public string Name => "permission_revoke";
		public string Description => "Revoke all permissions and remove a websocket client entry.";
		public string[] RequiredPermissions => new[] { "control:admin" };
		public ISchema Schema => new InputSchema()
			.Property<string>("clientId", "The client identifier to revoke", true)
			.Property<string>("configFolder", "Optional custom config folder path", false);

		public async UniTask<IOutput> Execute(IInput input) {
			var clientId = input.Get<string>("clientId", true);
			var configFolder = input.Get<string>("configFolder", false);

			RegistredManager.DeleteEntryFile(clientId, configFolder);

			RegistredManager.NotifyEntryUpdated(clientId);

			Permissions.PermissionEvents.OnPermissionDecision.Invoke(new Nox.Control.Runtime.Permissions.PermissionDecisionEventArgs {
				ClientId = clientId,
				Decision = Nox.Control.Runtime.Permissions.PermissionDecision.Revoked
			});

			return OperatorOutput.Ok(new { revoked = clientId });
		}
	}

	/// <summary>
	/// Operator: permission_get — get a single permission entry by client ID.
	/// </summary>
	public class PermissionGet : IOperator {
		public string Name => "permission_get";
		public string Description => "Get a single permission entry by client ID.";
		public string[] RequiredPermissions => new[] { "control:admin" };
		public ISchema Schema => new InputSchema()
			.Property<string>("clientId", "The client identifier", true)
			.Property<string>("configFolder", "Optional custom config folder path", false);

		public async UniTask<IOutput> Execute(IInput input) {
			var clientId = input.Get<string>("clientId", true);
			var configFolder = input.Get<string>("configFolder", false);

			var entry = RegistredManager.LoadEntryFile(clientId, configFolder);
			if (entry == null)
				return OperatorOutput.Error($"Client '{clientId}' not found.");

			return OperatorOutput.Ok(entry);
		}
	}

	#endregion
}
