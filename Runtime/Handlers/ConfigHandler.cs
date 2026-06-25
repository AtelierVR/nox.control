using Cysharp.Threading.Tasks;
using Nox.CCK.Control;
using Nox.CCK.Utils;

namespace Nox.Control.Runtime.Handlers {
    public class ConfigGet : IOperator
    {
        public string Name => "config_get";
        public string Description => "Get a configuration value by key, or all values if key is '*' or omitted.";
        public ISchema Schema => new InputSchema()
            .Property<string>("key", "The configuration key (e.g. 'settings.control.port'), or '*' for all.");

        public async UniTask<IOutput> Execute(IInput args) {
			await UniTask.Yield();

            var key = args.Get<string>("key");
            var cfg = Config.Load();

            if (string.IsNullOrEmpty(key) || key == "*") {
                var all = cfg.Get();
                return OperatorOutput.Ok(all);
            }

            return OperatorOutput.Ok(cfg.Get(key));
        }
    }

    public class ConfigSet : IOperator
    {
        public string Name => "config_set";
        public string Description => "Set a configuration value.";
        public ISchema Schema => new InputSchema()
            .Property<string>("key", "The configuration key.", true)
            .Property<string>("value", "The value to set (any JSON type).", true);

        public async UniTask<IOutput> Execute(IInput args) {
			await UniTask.Yield();

            var key = args.Get<string>("key", true);
            var rVal = args.Get<object>("value", true);
            var val = rVal is Newtonsoft.Json.Linq.JValue jv
				? jv.Value
				: rVal?.ToString();

            var cfg = Config.Load();
            cfg.Set(key, val);
            cfg.Save();

            return OperatorOutput.Ok(new {
                key,
				value = cfg.Get(key)
			});
        }
    }

    public class ConfigReload : IOperator
    {
        public string Name => "config_reload";
        public string Description => "Reload configuration from disk.";
        public ISchema Schema => new InputSchema();

        public async UniTask<IOutput> Execute(IInput _) {
			await UniTask.Yield();

            Config.Load(true);
            return OperatorOutput.Ok(new { reloaded = true });
        }
    }
}
