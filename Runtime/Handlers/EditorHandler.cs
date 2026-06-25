#if UNITY_EDITOR
using Cysharp.Threading.Tasks;
using Nox.CCK.Control;
using UnityEditor;

namespace Nox.Control.Runtime.Handlers {
    public class EditorGetPlayState : IOperator
    {
        public string Name => "editor_get_play_state";
        public string Description => "Check if Unity is currently in Play mode.";
        public ISchema Schema => new InputSchema();

        public async UniTask<IOutput> Execute(IInput _) {
            await UniTask.SwitchToMainThread();

            return OperatorOutput.Ok(new {
				playing = EditorApplication.isPlaying
			});
        }
    }

    public class EditorPlay : IOperator {
        public string Name => "editor_play";
        public string Description => "Enter Unity Play mode.";
        public ISchema Schema => new InputSchema();

        public async UniTask<IOutput> Execute(IInput _) {
            await UniTask.SwitchToMainThread();

            if (!EditorApplication.isPlaying)
				EditorApplication.isPlaying = true;

            return OperatorOutput.Ok(new {
				playing = EditorApplication.isPlaying
			});
        }
    }

    public class EditorStop : IOperator {
        public string Name => "editor_stop";
        public string Description => "Exit Unity Play mode.";
        public ISchema Schema => new InputSchema();

        public async UniTask<IOutput> Execute(IInput _) {
            await UniTask.SwitchToMainThread();
            if (EditorApplication.isPlaying)
                EditorApplication.isPlaying = false;

            return OperatorOutput.Ok(new {
                playing = EditorApplication.isPlaying
            });
        }
    }

}
#endif
