using Cysharp.Threading.Tasks;
using Nox.CCK.Utils;
using Nox.SDK.Control;

namespace Nox.Control.Handlers {
	public class ConfigHandler {
		public static void Handle(IClient client, string ev, object[] args) {
			switch (ev) {
				case "config:get": {
					var key = args.Length > 0 ? args[0]?.ToString() : null;
					var cfg = Config.Load();
					if (string.IsNullOrEmpty(key))
						client.Send("config:get", "*", cfg.Get()).Forget();
					else client.Send("config:get", key, cfg.Get(key)).Forget();
					break;
				}
				case "config:set": {
					if (args.Length < 2) {
						client.Send("error", "config:set requires a key and a value").Forget();
						return;
					}

					var key   = args[0]?.ToString();
					var value = args[1];

					var cfg = Config.Load();
					cfg.Set(key, value);
					cfg.Save();

					client.Send("config:get", key, cfg.Get(key)).Forget();
					break;
				}
				case "config:reload": {
					Config.Load(true);
					client.Send("config:reload").Forget();
					break;
				}
			}
		}
	}
}