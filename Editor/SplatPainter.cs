using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using Random = UnityEngine.Random;

namespace SplatPainter.Editor
{
	public sealed class SplatPainter : ScriptableWizard
	{
		private sealed class DummyObject : ScriptableObject
		{
			[SerializeField] private int prop;
		}

		public enum PaintChannel
		{
			R,
			G,
			B,
			A,
			N
		}

		private const string PainterShaderName = "Hidden/Painter";
		private const string BrushRendererShaderName = "Hidden/BrushRenderer";
		private const string HiddenBlitCopyIgnoreAlphaShaderName = "Hidden/BlitCopyIgnoreAlpha";
		private const string SplatMapPropName = "_SplatMap";
		private const string BrushColorPropName = "_BrushColor";
		private const string BrushSettingsPropName = "_BrushSettings";
		private const string Tex1PropName = "_Tex1";
		private const string Tex2PropName = "_Tex2";
		private const string Tex3PropName = "_Tex3";
		private const string Tex4PropName = "_Tex4";
		private const string Tex5PropName = "_Tex5";

		private static readonly int SplatMapPropID = Shader.PropertyToID(SplatMapPropName);
		private static readonly int BrushSettingsPropID = Shader.PropertyToID(BrushSettingsPropName);
		private static readonly int BrushColorPropID = Shader.PropertyToID(BrushColorPropName);
		private static readonly int Tex1PropID = Shader.PropertyToID(Tex1PropName);
		private static readonly int Tex2PropID = Shader.PropertyToID(Tex2PropName);
		private static readonly int Tex3PropID = Shader.PropertyToID(Tex3PropName);
		private static readonly int Tex4PropID = Shader.PropertyToID(Tex4PropName);
		private static readonly int Tex5PropID = Shader.PropertyToID(Tex5PropName);

		private readonly Stack<RenderTexture> _undoStack = new Stack<RenderTexture>();

		private readonly Dictionary<PaintChannel, Vector4> _colorTable = new Dictionary<PaintChannel, Vector4>
		{
			{ PaintChannel.R, new Vector4(1, 0, 0, 0) },
			{ PaintChannel.G, new Vector4(0, 1, 0, 0) },
			{ PaintChannel.B, new Vector4(0, 0, 1, 0) },
			{ PaintChannel.A, new Vector4(0, 0, 0, 1) },
			{ PaintChannel.N, new Vector4(0, 0, 0, 0) }
		};

		private event Action StrokeBegun;
		private event Action StrokeEnded;

		private bool _initialized;
		private int _brushSpacing = 3;
		private int _brushSize = 5;
		private float _brushHardness = 0.5f;
		private Material _material;
		private Material _painter;
		private Material _brushRenderer;
		private GameObject _currentObject;
		private MeshRenderer _currentRenderer;
		private Mesh _currentMesh;
		private RenderTexture _buffer;
		private RenderTexture _texture;
		private PaintChannel _currentChannel;
		private Vector2Int _textureSize;
		private string _originalTexturePath;
		private int _biggestDimension;
		private bool _paintButtonPressed;
		private bool _previousPaintState;
		private bool _destroyColliderAfterPaintingDone;
		private bool _textureSaved;
		private Collider _collider;
		private Vector2 _currentCursorPosition;
		private Vector2 _lastPaintPosition;
		private Texture[] _splatTextures;
		private DummyObject _undoDummy;
		private SerializedObject _dummySerializedObject;
		private SerializedProperty _dummyProp;
		private Vector2 _hitTextureCoord;
		private bool _needToPaint;


		[MenuItem("Tools/Splat Painter")]
		public static void Open()
		{
			SplatPainter splatPainter = DisplayWizard<SplatPainter>(ObjectNames.NicifyVariableName(nameof(SplatPainter)));
			splatPainter.ShowUtility();
		}

		private void OnEnable()
		{
			_painter = new Material(Shader.Find(PainterShaderName));
			_brushRenderer = new Material(Shader.Find(BrushRendererShaderName));

			TrySetActiveObject(Selection.activeGameObject);

			if (_currentObject == null)
			{
				_initialized = false;
				_textureSaved = true;

				return;
			}

			_undoDummy = CreateInstance<DummyObject>();
			_dummySerializedObject = new SerializedObject(_undoDummy);
			_dummyProp = _dummySerializedObject.FindProperty("prop");

			Undo.ClearAll();
			SceneView.duringSceneGui += OnSceneGui;
			Undo.undoRedoPerformed += UndoRedoPerformed;

			if (GraphicsSettings.renderPipelineAsset != null)
			{
				RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;
			}
			else
			{
				Camera.onPreCull -= DrawBrushRenderer;
				Camera.onPreCull += DrawBrushRenderer;
			}

			StrokeBegun += OnBrushStrokeBegun;
			StrokeEnded += OnBrushStrokeEnded;
			_initialized = true;
		}

		private void Update()
		{
			if (_needToPaint)
			{
				Paint(_hitTextureCoord, _brushSize);
				_needToPaint = false;
			}
		}

		private void OnDestroy()
		{
			SceneView.duringSceneGui -= OnSceneGui;
			Undo.undoRedoPerformed -= UndoRedoPerformed;

			if (GraphicsSettings.renderPipelineAsset != null)
			{
				RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
			}
			else
			{
				Camera.onPreCull -= DrawBrushRenderer;
			}

			StrokeBegun -= OnBrushStrokeBegun;
			StrokeEnded -= OnBrushStrokeEnded;

			if (_buffer != null)
			{
				RenderTexture.ReleaseTemporary(_buffer);
			}

			if (_textureSaved == false)
			{
				if (EditorUtility.DisplayDialog("Warning!", "Splat Map is not saved!", "Save and quit", "Quit without saving"))
				{
					SaveSplatMap();
				}
			}

			if (_undoDummy != null)
			{
				DestroyImmediate(_undoDummy);
			}

			SetMaterialSplatMapToSavedTexture();
			DestroyColliderIfNeeded();
			_initialized = false;
		}

		private void OnGUI()
		{
			if (_initialized)
			{
				Tools.current = Tool.None;
			}
			else
			{
				GUILayout.Label("Selected object not suitable for splat map painting :(");

				return;
			}

			SceneView.RepaintAll();
			_currentChannel = (PaintChannel)GUILayout.Toolbar((int)_currentChannel, _splatTextures, GUILayout.MaxHeight(64));
			_brushSpacing = EditorGUILayout.IntSlider("Spacing", _brushSpacing, 1, 10);
			_brushSize = EditorGUILayout.IntSlider("Size", _brushSize, 1, _biggestDimension / 6);
			_brushHardness = EditorGUILayout.Slider("Hardness", _brushHardness, 0f, 1f);

			if (GUILayout.Button("Save Splat Map"))
			{
				SaveSplatMap();
			}

			GUILayout.FlexibleSpace();

			if (GUILayout.Button("Fill All"))
			{
				if (EditorUtility.DisplayDialog("Are you sure?", "Fill texture with one color?", "Yes", "No"))
				{
					if (_currentObject != null)
					{
						Paint(Vector2.zero, float.MaxValue);
					}
				}
			}
		}


		private void UndoRedoPerformed()
		{
			UndoPaint();
		}

		private void OnBeginCameraRendering(ScriptableRenderContext context, Camera camera)
		{
			if (_initialized == false)
			{
				return;
			}

			DrawBrushRenderer(camera);
		}

		private void OnSceneGui(SceneView sceneView)
		{
			if (_initialized == false || _currentObject == null)
			{
				return;
			}

			HandleInputEvents();
			HandleUtility.AddDefaultControl(GUIUtility.GetControlID(FocusType.Passive));
			HandlePainting();
			HandleUtility.Repaint();
		}


		private void OnBrushStrokeBegun()
		{
			int randomValue;

			do
			{
				randomValue = Random.Range(int.MinValue, int.MaxValue);
			} while (randomValue == _dummyProp.intValue);

			_dummyProp.intValue = randomValue;
			_dummySerializedObject.ApplyModifiedProperties();

			Undo.RecordObject(_undoDummy, "Texture painted");
			Undo.IncrementCurrentGroup();
			SaveStateForUndo(_texture);
		}

		private void OnBrushStrokeEnded() { }

		private void HandleInputEvents()
		{
			if (Event.current.button == 0 && Event.current.alt == false)
			{
				_paintButtonPressed = Event.current.type switch
				{
					EventType.MouseDown => true,
					EventType.MouseUp   => false,
					_                   => _paintButtonPressed
				};

				if (_paintButtonPressed == false)
				{
					_lastPaintPosition = Vector2.one * 1000;
				}
			}
		}

		private void HandlePainting()
		{
			if (Event.current == null)
			{
				return;
			}

			Ray ray = HandleUtility.GUIPointToWorldRay(Event.current.mousePosition);

			if (_collider.Raycast(ray, out RaycastHit hit, float.MaxValue) == false)
			{
				return;
			}

			Handles.CircleHandleCap(-1, hit.point, Quaternion.LookRotation(hit.normal), 1f, EventType.Repaint);

			_currentCursorPosition = hit.textureCoord;
			float distanceFromPreviousPainPoint = Vector2.Distance(_lastPaintPosition, hit.textureCoord * _biggestDimension);
			bool canPaint = _paintButtonPressed && distanceFromPreviousPainPoint > _brushSpacing;

			if (_paintButtonPressed != _previousPaintState)
			{
				if (_paintButtonPressed)
				{
					StrokeBegun?.Invoke();
				}
				else
				{
					StrokeEnded?.Invoke();
				}
			}

			_previousPaintState = _paintButtonPressed;

			if (canPaint == false)
			{
				return;
			}

			_lastPaintPosition = hit.textureCoord * _biggestDimension;
			_hitTextureCoord = hit.textureCoord;
			_needToPaint = true;
		}

		private void TrySetActiveObject(GameObject gameObject)
		{
			if (gameObject == null)
			{
				return;
			}

			MeshFilter meshFilter = gameObject.GetComponent<MeshFilter>();

			if (meshFilter == null)
			{
				return;
			}

			MeshRenderer meshRenderer = gameObject.GetComponent<MeshRenderer>();

			if (meshRenderer == null)
			{
				return;
			}

			if (meshRenderer.sharedMaterials == null)
			{
				return;
			}

			Material material = meshRenderer.sharedMaterials.FirstOrDefault
				(rendererMaterial => rendererMaterial != null && rendererMaterial.HasProperty(SplatMapPropName));

			if (material == null)
			{
				return;
			}

			Texture texture = material.GetTexture(SplatMapPropID);

			if (texture == null)
			{
				return;
			}

			if (gameObject.TryGetComponent(out Collider existingCollider))
			{
				_collider = existingCollider;
				_destroyColliderAfterPaintingDone = false;
			}
			else
			{
				_collider = gameObject.AddComponent<MeshCollider>();
				_destroyColliderAfterPaintingDone = true;
			}

			_originalTexturePath = AssetDatabase.GetAssetPath(texture);
			_currentObject = gameObject;
			_currentRenderer = meshRenderer;
			_currentMesh = gameObject.GetComponent<MeshFilter>().sharedMesh;
			_textureSize = new Vector2Int(texture.width, texture.height);
			_biggestDimension = _textureSize.x > _textureSize.y ? _textureSize.x : _textureSize.y;
			_buffer = RenderTexture.GetTemporary(_textureSize.x, _textureSize.y);
			_texture = new RenderTexture(_textureSize.x, _textureSize.y, 0, GetTextureFormat()) { filterMode = FilterMode.Bilinear };

			Graphics.Blit(texture, _buffer);
			Graphics.Blit(_buffer, _texture);

			_splatTextures = new Texture[5];
			_splatTextures[0] = material.GetTexture(Tex1PropID);
			_splatTextures[1] = material.GetTexture(Tex2PropID);
			_splatTextures[2] = material.GetTexture(Tex3PropID);
			_splatTextures[3] = material.GetTexture(Tex4PropID);
			_splatTextures[4] = material.GetTexture(Tex5PropID);

			Material blitMaterial = new Material(Shader.Find(HiddenBlitCopyIgnoreAlphaShaderName));

			for (int i = 0; i < _splatTextures.Length; i++)
			{
				RenderTexture newTexture = new RenderTexture(256, 256, 0);
				Graphics.Blit(_splatTextures[i], newTexture, blitMaterial);
				_splatTextures[i] = newTexture;
			}

			_material = material;
			material.SetTexture(SplatMapPropID, _texture);
			_textureSaved = false;
		}

		private void Paint(Vector2 uvPosition, float size)
		{
			float uvBrushSize = ConvertSizeToUvSpace(size);
			_painter.SetVector(BrushSettingsPropID, new Vector4(uvPosition.x, uvPosition.y, uvBrushSize, _brushHardness));
			_painter.SetVector(BrushColorPropID, _colorTable[_currentChannel]);
			Graphics.Blit(_texture, _buffer);
			Graphics.Blit(_buffer, _texture, _painter);
		}

		private void SaveSplatMap()
		{
			if (_texture == null || _originalTexturePath == null)
			{
				return;
			}

			try
			{
				Texture2D texture = new Texture2D
					(_texture.width, _texture.height, GraphicsFormat.R32G32B32A32_SFloat, 0, TextureCreationFlags.None);

				RenderTexture.active = _texture;
				texture.ReadPixels(new Rect(0, 0, _texture.width, _texture.height), 0, 0, false);
				RenderTexture.active = null;
				string path = Path.Combine(Application.dataPath.Replace("Assets", string.Empty), _originalTexturePath);
				byte[] encodedPNG = texture.EncodeToPNG();
				File.WriteAllBytes(path, encodedPNG);
				EditorUtility.SetDirty(_texture);
				EditorUtility.SetDirty(_currentObject);
				_textureSaved = true;
			}
			catch (Exception e)
			{
				Debug.LogError(e);
				EditorUtility.DisplayDialog("Texture saving error", e.Message, "Ok");
			}
		}

		private float ConvertSizeToUvSpace(float size)
		{
			return size / _biggestDimension;
		}

		private void SetMaterialSplatMapToSavedTexture()
		{
			if (_material == null || string.IsNullOrEmpty(_originalTexturePath))
			{
				return;
			}

			_material.SetTexture(SplatMapPropID, AssetDatabase.LoadAssetAtPath<Texture>(_originalTexturePath));
			EditorUtility.SetDirty(_material);
			AssetDatabase.Refresh();
		}

		private void DestroyColliderIfNeeded()
		{
			if (_destroyColliderAfterPaintingDone && _collider != null)
			{
				DestroyImmediate(_collider);
			}
		}

		private void DrawBrushRenderer(Camera camera)
		{
			if (_paintButtonPressed || camera == null)
			{
				return;
			}

			float uvBrushSize = ConvertSizeToUvSpace(_brushSize);

			_brushRenderer.SetVector
				(BrushSettingsPropID, new Vector4(_currentCursorPosition.x, _currentCursorPosition.y, uvBrushSize, _brushHardness));

			for (int i = 0; i < _currentRenderer.sharedMaterials.Length; i++)
			{
				Matrix4x4 matrix = Matrix4x4.TRS
					(_currentObject.transform.position, _currentObject.transform.rotation, _currentObject.transform.lossyScale);

				Graphics.DrawMesh(_currentMesh, matrix, _brushRenderer, 0, camera, i);
			}
		}

		private void SaveStateForUndo(RenderTexture texture)
		{
			RenderTexture temp = new RenderTexture(texture.width, texture.height, 0);
			Graphics.Blit(texture, temp);
			_undoStack.Push(temp);
		}

		private void UndoPaint()
		{
			if (_undoStack.Count <= 0)
			{
				return;
			}

			RenderTexture prevState = _undoStack.Pop();
			Graphics.Blit(prevState, _texture);
			prevState.Release();
		}

		private static GraphicsFormat GetTextureFormat()
		{
			#if UNITY_2022_1_OR_NEWER
			return SystemInfo.GetCompatibleFormat(GraphicsFormat.R32G32B32A32_SFloat, GraphicsFormatUsage.Blend);
			#else
			return SystemInfo.GetCompatibleFormat(GraphicsFormat.R32G32B32A32_SFloat, FormatUsage.Blend);
			#endif
		}
	}
}
