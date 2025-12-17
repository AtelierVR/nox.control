using System.Linq;
using Cysharp.Threading.Tasks;
using Nox.CCK.Mods.Initializers;
using Nox.SDK.Control;

namespace Nox.Control.Handlers {
	public class ModHandler {
		public static void Handle(IClient client, string ev, object[] args) {
			switch (ev) {
				case "mods:list": {
					var mods     = Main.CoreAPI.ModAPI.GetMods();
					var modInfos = new object[mods.Length];
					for (var i = 0; i < mods.Length; i++) {
						var mod  = mods[i];
						var meta = mod.GetMetadata();
						modInfos[i] = new {
							id       = meta.GetId(),
							provides = meta.GetProvides(),
							version  = meta.GetVersion()?.ToString() ?? "0.0.0",
							loaded   = mod.IsLoaded(),
							entries  = meta.GetEntryPoints().GetAll(),
							instances = mod.GetInstances<IModInitializer>()
								.Select(inst => inst.GetType().FullName)
								.ToArray(),
						};
					}

					client.Send("mods:list", modInfos).Forget();
					break;
				}
				case "mods:get": {
					var key = args.Length > 0 ? args[0]?.ToString() : null;
					if (string.IsNullOrEmpty(key)) {
						client.Send("mods:get", null).Forget();
						break;
					}

					var mod  = Main.CoreAPI.ModAPI.GetMod(key);
					var meta = mod?.GetMetadata();
					if (mod == null) {
						client.Send("mods:get", null).Forget();
						break;
					}

					var modInfo = new {
						id       = meta.GetId(),
						provides = meta.GetProvides(),
						version  = meta.GetVersion()?.ToString() ?? "0.0.0",
						loaded   = mod.IsLoaded(),
						entries  = meta.GetEntryPoints().GetAll(),
						instances = mod.GetInstances<IModInitializer>()
							.Select(inst => inst.GetType().FullName)
							.ToArray(),
					};

					client.Send("mods:get", modInfo).Forget();
					break;
				}
			}
		}
	}
}