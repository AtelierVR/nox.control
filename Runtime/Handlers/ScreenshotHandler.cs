using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Cysharp.Threading.Tasks;
using Nox.CCK.Control;
using Nox.CCK.Utils;
using UnityEngine;
using Logger = Nox.CCK.Utils.Logger;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Nox.Control.Runtime.Handlers {
	/// <summary>
	/// Captures an image from a camera. Defaults to the main camera
	/// (<c>Camera.main</c>); <c>camera</c> accepts a component/GameObject entity ID or
	/// a keyword (<c>main</c>, <c>current</c>, <c>editor</c>…).
	/// <para>
	/// The image is returned as an <b>MCP <c>image</c> content block</b> (the client
	/// displays it directly), together with a text block describing the camera. When
	/// <c>path</c> is provided, a <c>resource_link</c> points to the written file.
	/// </para>
	/// </summary>
	public class Screenshot : IOperator {
		public string Name
			=> "screenshot";

		public string Description
			=> "Capture a screenshot from a camera. Defaults to the main camera; 'camera' "
			   + "accepts a component/GameObject entity ID or a keyword (main, current, "
			   + "editor, scene, game). Returns the image as a viewable content block, "
			   + "optionally written to disk.";

		public string[] RequiredPermissions => new[] { "screenshot:read" };

		public ISchema Schema => new InputSchema()
			.Property<string>(
				"camera",
				"Camera to capture. Keywords: 'main' (default), 'current', 'editor'/'scene' "
				+ "(Scene view), 'game'. Otherwise a Camera/GameObject entity ID or a "
				+ "GameObject name."
			)
			.Property<int>("width", "Output width in pixels. Defaults to the camera's pixel width.")
			.Property<int>("height", "Output height in pixels. Defaults to the camera's pixel height.")
			.Property<string>("format", "Image format: 'png' (default) or 'jpg'/'jpeg'.")
			.Property<int>("quality", "JPEG quality (1-100). Defaults to 75. Ignored for PNG.")
			.Property<string>(
				"path",
				"Optional file path to write the image to. Relative paths resolve against the "
				+ "project folder in the Editor, or the persistent data folder in a build."
			);

		public async UniTask<IOutput> Execute(IInput args) {
			await UniTask.SwitchToMainThread();

			var selector = args.Get<string>("camera");

			// Validate the cheap arguments before resolving the camera, so that
			// an unknown format is not masked by a selection error.
			var format = NormalizeFormat(args.Get<string>("format"), out var formatError);
			if (formatError != null)
				return OperatorOutput.Error(formatError);

			if (!TryResolveCamera(selector, out var camera, out var resolveError))
				return OperatorOutput.Error(resolveError);

			var quality = Mathf.Clamp(args.Get<int?>("quality") ?? 75, 1, 100);

			var (width, height) = ResolveSize(camera, args.Get<int?>("width"), args.Get<int?>("height"));

			byte[] data;
			try {
				using var capture = Render(camera, width, height);
				data = format == "jpg"
					? capture.Texture.EncodeToJPG(quality)
					: capture.Texture.EncodeToPNG();
				capture.Texture.Destroy();
			} catch (Exception ex) {
				return OperatorOutput.Error($"Failed to render '{camera.name}': {ex.Message}");
			}

			var savedPath = TrySave(args.Get<string>("path"), data, out var saveError);
			if (saveError != null)
				return OperatorOutput.Error(saveError);

			var mimeType = format == "jpg" ? "image/jpeg" : "image/png";

			var metadata = new {
				camera = new {
					id   = camera.GetId(),
					name = camera.name,
					tag  = camera.tag,
					path = GetPath(camera.gameObject)
				},
				selector = string.IsNullOrWhiteSpace(selector) ? "main" : selector.Trim(),
				width,
				height,
				format,
				bytes      = data.Length,
				saved_path = savedPath
			};

			var output = OperatorOutput.Image(data, mimeType, metadata);

			// The file was written to disk: reference it without duplicating the bytes.
			if (savedPath != null)
				output.With(OutputContent.FromLink(
					new Uri(savedPath).AbsoluteUri,
					Path.GetFileName(savedPath),
					mimeType,
					"Screenshot written to disk"
				));

			return output;
		}

		#region Camera selection

		/// <summary>
		/// Resolves <paramref name="selector"/> (keyword, ID or name) to a <see cref="Camera"/>.
		/// </summary>
		private static bool TryResolveCamera(string selector, out Camera camera, out string error) {
			camera = null;
			error  = null;

			var wanted = string.IsNullOrWhiteSpace(selector) ? "main" : selector.Trim();

			switch (wanted.ToLowerInvariant()) {
				case "main":
				case "game":
				case "game_view":
					camera = MainCamera();
					if (camera == null)
						error = "No main camera found (no enabled camera tagged 'MainCamera').";
					return error == null;

				case "current":
					camera = Camera.current ?? FirstWatched() ?? MainCamera();
					if (camera == null)
						error = "No current camera found.";
					return error == null;

				case "editor":
				case "scene":
				case "scene_view":
				case "sceneview":
					#if UNITY_EDITOR
					camera = SceneView.lastActiveSceneView ? SceneView.lastActiveSceneView.camera : null;
					if (camera == null)
						error = "No active Scene view to capture.";
					return error == null;
					#else
					error = "The 'editor' selector is only available in the Unity Editor.";
					return false;
					#endif
			}

			// Numeric ID (component or GameObject) or a GameObject / component name.
			if (TryParseId(wanted, out var id)) {
				foreach (var candidate in AllCameras())
					if (candidate.GetId() == id || candidate.gameObject.GetId() == id) {
						camera = candidate;
						return true;
					}

				error = $"No camera found with ID {id}.";
				return false;
			}

			var byName = new List<Camera>();
			foreach (var candidate in AllCameras())
				if (string.Equals(candidate.name, wanted, StringComparison.OrdinalIgnoreCase)
				    || string.Equals(candidate.gameObject.name, wanted, StringComparison.OrdinalIgnoreCase))
					byName.Add(candidate);

			if (byName.Count == 0) {
				error = $"No camera matching '{wanted}'. Use a keyword (main, current, editor, game) "
				        + "or a Camera/GameObject entity ID.";
				return false;
			}

			camera = byName[0];
			return true;
		}

		private static Camera MainCamera()
			=> Camera.main;

		/// <summary>Local controller camera, registered by <see cref="CameraChop"/>.</summary>
		private static Camera FirstWatched() {
			foreach (var watched in CameraChop.WatchedCamera)
				if (watched != null)
					return watched;
			return null;
		}

		/// <summary>
		/// Every camera in the loaded scenes (including inactive ones), plus those in
		/// <c>DontDestroyOnLoad</c> in play mode (outside <c>SceneManager</c>).
		/// </summary>
		private static List<Camera> AllCameras() {
			var cameras = new List<Camera>(ComponentExtension.GetComponentsInChildren<Camera>(true));

			if (!Application.isPlaying)
				return cameras;

			try {
				foreach (var root in SceneExtensions.DontDestroyOnLoad.GetRootGameObjects()) {
					var found = root.GetComponentsInChildren<Camera>(true);
					foreach (var candidate in found)
						if (!cameras.Contains(candidate))
							cameras.Add(candidate);
				}
			} catch (Exception ex) {
				Logger.LogWarning($"Could not enumerate DontDestroyOnLoad cameras: {ex.Message}");
			}

			return cameras;
		}

		private static bool TryParseId(string value, out int id)
			=> int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out id);

		#endregion

		#region Rendu / encodage

		private static (int width, int height) ResolveSize(Camera camera, int? width, int? height) {
			var w = width  is > 0 ? width.Value  : camera.pixelWidth  > 0 ? camera.pixelWidth  : Screen.width;
			var h = height is > 0 ? height.Value : camera.pixelHeight > 0 ? camera.pixelHeight : Screen.height;

			return (w > 0 ? w : 1920, h > 0 ? h : 1080);
		}

		private static string NormalizeFormat(string format, out string error) {
			error = null;

			var wanted = string.IsNullOrWhiteSpace(format) ? "png" : format.Trim().ToLowerInvariant();
			switch (wanted) {
				case "png":
					return "png";
				case "jpg":
				case "jpeg":
					return "jpg";
				default:
					error = $"Unsupported format '{format}'. Use 'png' or 'jpg'.";
					return null;
			}
		}

		/// <summary>
		/// Renders the camera into a RenderTexture and reads the pixels.
		/// <para>
		/// <c>Camera.Render()</c> forces a render even outside play mode, which also covers the
		/// Scene view. <c>Handles.DrawCamera</c> (which would also add gizmos) is deliberately
		/// avoided: called from a websocket/MCP handler, outside a GUI context, it throws a
		/// NullReferenceException.
		/// </para>
		/// </summary>
		private static CaptureTarget Render(Camera camera, int width, int height) {
			var previousActive = RenderTexture.active;
			var previousTarget = camera.targetTexture;
			var renderTexture  = RenderTexture.GetTemporary(
				width, height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB
			);

			try {
				camera.targetTexture = renderTexture;

				camera.Render();
				RenderTexture.active = renderTexture;

				var texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
				texture.ReadPixels(new Rect(0, 0, width, height), 0, 0);
				texture.Apply();

				return new CaptureTarget(texture, renderTexture, camera, previousTarget, previousActive);
			} catch {
				camera.targetTexture = previousTarget;
				RenderTexture.active  = previousActive;
				RenderTexture.ReleaseTemporary(renderTexture);
				throw;
			}
		}

		/// <summary>Restores the camera's render state once the capture is done.</summary>
		private readonly struct CaptureTarget : IDisposable {
			public readonly Texture2D     Texture;
			private readonly RenderTexture _renderTexture;
			private readonly Camera        _camera;
			private readonly RenderTexture _previousTarget;
			private readonly RenderTexture _previousActive;

			internal CaptureTarget(
				Texture2D texture,
				RenderTexture renderTexture,
				Camera camera,
				RenderTexture previousTarget,
				RenderTexture previousActive
			) {
				Texture         = texture;
				_renderTexture  = renderTexture;
				_camera         = camera;
				_previousTarget = previousTarget;
				_previousActive = previousActive;
			}

			public void Dispose() {
				_camera.targetTexture  = _previousTarget;
				RenderTexture.active   = _previousActive;
				RenderTexture.ReleaseTemporary(_renderTexture);
			}
		}

		#endregion

		#region Disk output

		private static string TrySave(string path, byte[] data, out string error) {
			error = null;
			if (string.IsNullOrWhiteSpace(path))
				return null;

			try {
				var full = Path.IsPathRooted(path)
					? path
					: Path.Combine(
						Application.isEditor ? Path.GetFullPath(".") : Application.persistentDataPath,
						path
					);

				var directory = Path.GetDirectoryName(full);
				if (!string.IsNullOrEmpty(directory))
					Directory.CreateDirectory(directory);

				File.WriteAllBytes(full, data);
				return full;
			} catch (Exception ex) {
				error = $"Failed to write screenshot to '{path}': {ex.Message}";
				return null;
			}
		}

		/// <summary>Hierarchical path of the GameObject, reusable with <c>hierarchy_get</c>.</summary>
		private static string GetPath(GameObject gameObject) {
			var parts = new List<string>();
			for (var current = gameObject.transform; current != null; current = current.parent)
				parts.Add(current.name);

			// The Scene view camera (or a prefab's) belongs to no valid scene:
			// querying SceneExtensions.DontDestroyOnLoad there would create a GameObject, which
			// Unity refuses outside play mode. Use a neutral label instead.
			var scene = gameObject.scene;
			parts.Add(scene.IsValid() && !string.IsNullOrEmpty(scene.name) ? scene.name : "(no scene)");
			parts.Reverse();

			return string.Join("/", parts);
		}

		#endregion
	}
}
