using System.Collections.Generic;
using Nox.CCK.Language;
using UnityEngine;

namespace Nox.Control.Runtime.Settings {
	/// <summary>
	/// Creates and manages a dynamic LanguagePack for permission settings groups and toggles.
	/// The pack is created at runtime and registered with the LanguageManager so that
	/// group labels (client names) and toggle labels (permission descriptions) are
	/// resolved through the standard localization system.
	/// </summary>
	public static class PermissionSettingsLanguagePack {
		private static LanguagePack _pack;
		private static readonly Dictionary<string, string> EnUsEntries = new();
		private static readonly object Lock = new();

		/// <summary>
		/// Ensures the dynamic language pack exists and is registered,
		/// then adds or updates a translation key.
		/// Keys follow the convention:
		///   settings.group.permissions.{clientId}.label  → client name
		///   settings.group.permissions.{permissionId}.label → permission description
		/// All entries are registered under en-US (fallback language).
		/// </summary>
		public static void SetGroupLabel(string clientId, string clientName) {
			SetEntry($"settings.group.permissions.{clientId}.label", clientName);
		}

		public static void SetPermissionLabel(string clientId, string permissionId, string permissionDescription) {
			SetEntry($"settings.group.permissions.{clientId}.{permissionId}.label", permissionDescription);
		}

		private static void SetEntry(string key, string value) {
			lock (Lock) {
				EnUsEntries[key] = value;
				RebuildPack();
			}
		}

		/// <summary>
		/// Removes all dynamic entries and unregisters the pack.
		/// </summary>
		public static void Clear() {
			lock (Lock) {
				EnUsEntries.Clear();
				if (_pack != null) {
					LanguageManager.RemovePack(_pack);
					if (Application.isPlaying)
						Object.Destroy(_pack);
					else
						Object.DestroyImmediate(_pack);
					_pack = null;
				}
			}
		}

		private static void RebuildPack() {
			// Remove old pack
			if (_pack != null) {
				LanguageManager.RemovePack(_pack);
				if (Application.isPlaying)
					Object.Destroy(_pack);
				else
					Object.DestroyImmediate(_pack);
				_pack = null;
			}

			if (EnUsEntries.Count == 0)
				return;

			// Create new pack
			_pack = ScriptableObject.CreateInstance<LanguagePack>();
			_pack.name = "Dynamic_PermissionSettings";

			var entries = new List<LanguagePack.LanguageEntry>(EnUsEntries.Count);
			foreach (var kv in EnUsEntries)
				entries.Add(new LanguagePack.LanguageEntry { key = kv.Key, value = kv.Value });

			_pack.languages = new[] {
				new LanguagePack.LanguageData {
					IETF = LanguageManager.FallbackLanguage,
					entries = entries
				}
			};

			LanguageManager.AddPack(_pack);
		}
	}
}
