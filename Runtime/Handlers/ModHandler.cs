using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using Newtonsoft.Json;
using Nox.CCK.Control;
using Nox.CCK.Mods;

namespace Nox.Control.Runtime.Handlers  {
		public class ModList : IOperator {
			public string Name => "mods_list";
			public string Description => "List all loaded mods with their metadata.";
			public ISchema Schema => new InputSchema();

			public async UniTask<IOutput> Execute(IInput _) {
				await UniTask.Yield();

				var api = Main.CoreAPI;
				if (api == null)
					return OperatorOutput.Error("CoreAPI not available");

				var mods = api.ModAPI.GetMods();
				var results = new ModMetadata[mods.Length];
				for(var i = 0; i < mods.Length; i++) 
					results[i] = new ModMetadata(mods[i]);

				return OperatorOutput.Ok(results);
			}
		}

		[Serializable]
		public class ModMetadata {
			public ModMetadata(IMod mod) {
				var meta = mod.GetMetadata();
				Id = meta.GetId();
				Provides = meta.GetProvides();
				Version = meta.GetVersion().ToString();
				Loaded = mod.IsLoaded();

				// Entries: map section name → array of FullName strings
				var entryPoints = meta.GetEntryPoints().All;
				Entries = new Dictionary<string, string[]>();
				foreach (var kv in entryPoints) 
					Entries[kv.Key] = Array.ConvertAll(kv.Value, e => e.FullName);

				// Instances: if loaded, all entry point class names are considered instantiate
				var allNames = new List<string>();
				foreach (var kv in entryPoints)
					foreach (var ep in kv.Value)
						allNames.Add(ep.FullName);
				Instances = allNames.ToArray();
			}

			[JsonProperty("id")]
			public string Id = string.Empty;

			[JsonProperty("provides")]
			public string[] Provides = Array.Empty<string>();

			[JsonProperty("version")]
			public string Version = new Version().ToString();

			[JsonProperty("loaded")]
			public bool Loaded = false;

			[JsonProperty("entries")]
			public Dictionary<string, string[]> Entries = new();

			[JsonProperty("instances")]
			public string[] Instances = Array.Empty<string>();
		}

		public class ModGet : IOperator {
			public string Name => "mods_get";
			public string Description => "Get details about a specific mod by its ID.";
			public ISchema Schema => new InputSchema()
				.Property<string>("id", "The mod ID to look up.", true);

			public async UniTask<IOutput> Execute(IInput args) {
				await UniTask.Yield();

				var api = Main.CoreAPI;
				if (api == null)
					return OperatorOutput.Error("CoreAPI not available");

				var id = args.Get<string>("id", true);

				var mod = api.ModAPI.GetMod(id);
				if (mod == null)
					return OperatorOutput.Error($"Mod '{id} not found.");

				return OperatorOutput.Ok(new ModMetadata(mod));
			}
		}
}
