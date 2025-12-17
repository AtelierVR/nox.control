using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using Cysharp.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Nox.CCK.Mods.Events;
using Nox.CCK.Utils;

namespace Nox.Control.Handlers {
	public class EventHandler {
		public static void OnEvent(EventData context) {
			var clients = Main.Server.GetClients();

			var data = new List<JToken>();

			foreach (var d in context.Data) {
				switch (d) {
					case null:
						data.Add(JValue.CreateNull());
						break;

					case JToken jToken:
						data.Add(jToken);
						break;

					case ISerializable serializable:
						data.Add(JToken.FromObject(serializable));
						break;

					default:
						data.Add(JToken.FromObject(d.ToString()));
						break;
				}
			}

			var channels = (
			from EventEntryFlags flag
				in Enum.GetValues(typeof(EventEntryFlags))
			where context.SourceChannel.HasFlag(flag)
			select flag.ToString().ToSnakeCase()
			).ToArray();
			
			foreach (var client in clients)
				client.Send("event", context.EventName, context.Source.GetMetadata().GetId(), data.ToArray(), channels).Forget();
		}
	}
}