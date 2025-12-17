#if UNITY_EDITOR
using Cysharp.Threading.Tasks;
using Nox.SDK.Control;

namespace Nox.Control.Handlers {
	public class EditorHandler {
		public static void Handle(IClient client, string ev, object[] args) {
			switch (ev) {
				case "editor:playing": {
					var isPlaying = UnityEditor.EditorApplication.isPlaying;
					client.Send("editor:playing", isPlaying).Forget();
					break;
				}
				case "editor:play": {
					if (!UnityEditor.EditorApplication.isPlaying)
						UnityEditor.EditorApplication.isPlaying = true;
					client.Send("editor:playing", true).Forget();
					break;
				}
				case "editor:stop": {
					if (UnityEditor.EditorApplication.isPlaying)
						UnityEditor.EditorApplication.isPlaying = false;
					client.Send("editor:playing", false).Forget();
					break;
				}
			}
		}
	}
}
#endif