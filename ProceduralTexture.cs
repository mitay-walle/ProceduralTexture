using System;
using Sirenix.OdinInspector;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using ObjectFieldAlignment = Sirenix.OdinInspector.ObjectFieldAlignment;
using Random = UnityEngine.Random;
using System.Linq;
using System.Reflection;

namespace GameJam.Plugins.Procedural
{
	[CreateAssetMenu]
	public sealed class ProceduralTexture : ScriptableObject, ISerializationCallbackReceiver
	{
		[SerializeField, ReadOnly, PreviewField(ObjectFieldAlignment.Center, Height = 150)] private Texture2D _texture;
		private const string E = nameof(Execute);
		[SerializeField] private bool _immediate = true;
		[SerializeField, OnValueChanged(E)] private bool _isSquare = true;
		[SerializeField, OnValueChanged(E)] private PoT _resolution = PoT._128;
		[SerializeField, OnValueChanged(E), HideIf(nameof(_isSquare))] private PoT _resolutionY = PoT._128;
		[SerializeField, OnValueChanged(E)] private Color _background = Color.clear;

		//[DrawWithUnity]
		[OnValueChanged(E, true, InvokeOnInitialize = true, InvokeOnUndoRedo = true)]
		[SerializeReference]
		private ILayer[] _layers = { new Gradient() };

		[Button, HideIf(nameof(_immediate))]
		private void Execute()
		{
			if (!_texture) return;
			if (_layers is not { Length: > 0 }) return;
			_context = new Context(_resolution, _isSquare, _resolutionY, _background, _texture);

			foreach (ILayer layer in _layers)
			{
				layer?.Process(_context);
			}

			try
			{
				_texture.SetPixels(_context.Colors);
			}
			catch { }
			_texture.Apply();
			#if UNITY_EDITOR
			EditorUtility.SetDirty(_texture);
			#endif
		}

		[Serializable]
		public class Blur : Layer
		{
			[SerializeField, Delayed] private int blurSize = 4;

			protected override Color ProcessPixel(Context c)
			{
				Color avgColor = Color.clear;
				int count = 0;

				for (int offsetY = -blurSize; offsetY <= blurSize; offsetY++)
				{
					for (int offsetX = -blurSize; offsetX <= blurSize; offsetX++)
					{
						int sampleX = Mathf.Clamp(c.x + offsetX, 0, c.width - 1);
						int sampleY = Mathf.Clamp(c.y + offsetY, 0, c.height - 1);
						avgColor += c.Colors[sampleY * c.width + sampleX];
						count++;
					}
				}

				avgColor /= count;
				return avgColor;
			}
		}

		[Serializable]
		public class Grain : Layer
		{
			[SerializeField, HorizontalGroup, HideLabel] private Color min = Color.clear;
			[SerializeField, HorizontalGroup, HideLabel] private Color max = Color.white;
			[SerializeField, PropertyRange(0, 1)] private float _amount = .5f;
			[SerializeField] private bool _reseed = true;
			[SerializeField, HideIf(nameof(_reseed))] private int _seed;

			protected override Color ProcessPixel(Context c)
			{
				Random.State old = default;
				if (!_reseed)
				{
					old = Random.state;
					Random.InitState(_seed);
				}

				Color result = Random.Range(0, c.index) > c.index * _amount ? min : max;
				if (!_reseed) Random.state = old;
				return result;
			}
		}

		[Serializable]
		public class Perlin : Layer
		{
			[SerializeField, HorizontalGroup, HideLabel] private Color min = Color.clear;
			[SerializeField, HorizontalGroup, HideLabel] private Color max = Color.white;
			[SerializeField] private Vector2 _uv = new(1, 1);
			[SerializeField] private AnimationCurve _gamma = AnimationCurve.Linear(0, 0, 1, 1);
			[SerializeField] private Vector2 _remap = new(0, 1);

			protected override Color ProcessPixel(Context c)
			{
				float t = Mathf.LerpUnclamped(_remap.x, _remap.y, Mathf.PerlinNoise((float)c.x / c.width * _uv.x, (float)c.y / c.height * _uv.y));

				return Color.LerpUnclamped(min, max, _gamma.Evaluate(t));
			}
		}

		[Serializable]
		public class Gradient : Layer
		{
			[SerializeField] private UV _uv;
			[SerializeField, ShowIf("@_uv==UV.GradientMap")]
			private GradientMapSource _source;
			[SerializeField, ShowIf("@_uv==UV.GradientMap && _source==GradientMapSource.Channel")]
			private RGBA _channel;
			//[SerializeField] private bool _isMirror;
			//[SerializeField, PropertyRange(1, 256)] private int _repeat = 1;
			[SerializeField] private UnityEngine.Gradient _gradient;
			[SerializeField] private AnimationCurve _gamma = AnimationCurve.Linear(0, 0, 1, 1);

			protected override Color ProcessPixel(Context c)
			{
				switch (_uv)
				{
					case UV.Horizontal:
						{
							float t = (float)c.x / c.width;
							return _gradient.Evaluate(_gamma.Evaluate(t));
						}
					case UV.Vertical:
						{
							float t = (float)c.y / c.height;
							return _gradient.Evaluate(_gamma.Evaluate(t));
						}
					case UV.Circle:
						{
							float diameter = Mathf.Min(c.width, c.height);
							Vector2 center = new Vector2(diameter / 2, diameter / 2);
							float radius = diameter / 2f;
							Vector2 point = new Vector2(c.x, c.y);
							float distance = Vector2.Distance(point, center);
							float t = 1 - (distance / radius);
							return _gradient.Evaluate(_gamma.Evaluate(t));
						}
					case UV.GradientMap:
						{
							float t = 0;
							switch (_source)
							{
								case GradientMapSource.Grayscale:
									{
										t = c.Color.grayscale;
										break;
									}
								case GradientMapSource.Channel:
									{
										t = c.Color[(int)_channel];
										break;
									}
								default: throw new ArgumentOutOfRangeException();
							}
							return _gradient.Evaluate(_gamma.Evaluate(t));
						}
					default: throw new ArgumentOutOfRangeException();
				}
			}

			public enum UV
			{
				Horizontal,
				Vertical,
				Circle,
				GradientMap,
			}

			public enum GradientMapSource
			{
				Grayscale,
				Channel,
			}

			public enum RGBA
			{
				R,
				G,
				B,
				A
			}
		}

		[Serializable]
		public class TextureLayer : Layer
		{
			[SerializeField, Required] protected Texture2D _texture;
			[SerializeField, Required] protected Vector4 _tileAndOffset = new(1, 1, 0, 0);
			protected Texture2D _textureReadable;

			public override void Process(Context c)
			{
				if (!_texture) return;
				_textureReadable = _texture;
				if (_texture.isReadable)
				{
					_textureReadable = _texture;
				}
				else
				{
					byte[] tmp = _texture.GetRawTextureData();
					if (_textureReadable == null)
					{
						_textureReadable = new Texture2D(_texture.width, _texture.height);
					}
					else
					{
						_textureReadable.Reinitialize(_texture.width, _texture.height);
					}

					_textureReadable.LoadRawTextureData(tmp);
				}
				base.Process(c);
				if (_textureReadable != _texture)
				{
					DestroyImmediate(_textureReadable);
				}
			}

			protected override Color ProcessPixel(Context c)
			{
				if (!_texture) return c.Color;
				if (!_texture.isReadable) return c.Color;
				float x = ((float)c.x / c.width + _tileAndOffset[2]) * _tileAndOffset[0];
				float y = ((float)c.y / c.height + _tileAndOffset[3]) * _tileAndOffset[1];
				return _textureReadable.GetPixelBilinear(x, y);
			}
		}

		public interface ILayer
		{
			void Process(Context context);
		}

		[Serializable]
		public abstract class Layer : ILayer
		{
			[SerializeField] protected bool _skip;
			[SerializeField, PropertyRange(0, 1)] protected float _alpha = 1;
			[SerializeField] protected Blend _blend;
			[SerializeField] protected Vector2 _offset;

			public virtual void Process(Context c)
			{
				if (_skip) return;

				int index = 0;
				for (int y = 0; y < c.height; y++)
				{
					for (int x = 0; x < c.width; x++)
					{
						c.x = x + Mathf.RoundToInt(_offset[0] * c.size[0]);
						c.y = y + Mathf.RoundToInt(_offset[1] * c.size[1]);
						c.index = index;
						c.Color = c.Colors[index];
						Color before = c.Color;
						Color result = ProcessPixel(c);
						switch (_blend)
						{
							case Blend.Set:
								{
									c.Colors[index] = result;
									break;
								}
							case Blend.Alpha:
								{
									c.Colors[index] = Color.Lerp(before, result, result.a * _alpha);
									break;
								}
							case Blend.Additive:
								{
									c.Colors[index] += result * result.a * _alpha;
									break;
								}
							case Blend.Multiply:
								{
									Color alpha = new(1 - _alpha, 1 - _alpha, 1 - _alpha, 1 - _alpha);
									c.Colors[index] *= result + alpha;
									break;
								}
							default: throw new ArgumentOutOfRangeException();
						}
						index++;
					}
				}
			}

			protected abstract Color ProcessPixel(Context c);

			public enum Blend
			{
				Set,
				Alpha,
				Additive,
				Multiply,
			}
		}

		public struct Context
		{
			public Color[] Colors;
			public Texture2D Texture;
			public int index;
			public int x;
			public int y;
			public Color Background;
			public Color Color;
			public int width;
			public int height;
			public int length => width * height;
			public Vector2Int size => new(width, height);

			public Context(PoT width, bool isSquare, PoT height, Color background, Texture2D texture)
			{
				this.width = (int)width;
				this.height = isSquare ? (int)width : (int)height;
				index = 0;
				x = 0;
				y = 0;
				Background = background;
				Color = Color.clear;
				Colors = default;
				Texture = texture;
				Colors = new Color[length];
				for (int i = 0; i < length; i++)
				{
					Colors[i] = background;
				}
			}
		}

		public enum PoT
		{
			_1 = 1,
			_2 = 2,
			_4 = 4,
			_8 = 8,
			_16 = 16,
			_32 = 32,
			_64 = 64,
			_128 = 128,
			_256 = 256,
			_512 = 512,
			_1024 = 1024,
		}

		private void OnValidate()
		{
			string assetPath = AssetDatabase.GetAssetPath(this);

			if (!_texture && this != null)
			{
				AssetDatabase.ImportAsset(assetPath);
				_texture = AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
			}

			if (!_texture)
			{
				_context = new Context(_resolution, _isSquare, _resolutionY, _background, _texture);

#if UNITY_2018
                _texture = new Texture2D(_context.width, _context.height);
#else
				_texture = new Texture2D(_context.width, _context.height, DefaultFormat.LDR, TextureCreationFlags.None);
#endif
				_context.Texture = _texture;
				if (_texture.name != name) _texture.name = name;
			}

			if (!_texture) return;

			if (_texture.name != name)
			{
				_texture.name = name;
			}
			else
			{
				_context = new Context(_resolution, _isSquare, _resolutionY, _background, _texture);

				if (_texture.width != _context.width || _texture.height != _context.height)
				{
					_texture.Reinitialize(_context.width, _context.height);
				}
				_texture.alphaIsTransparency = true;
				Execute();
			}

#if UNITY_EDITOR
			if (!EditorUtility.IsPersistent(this)) return;
			if (AssetDatabase.IsSubAsset(_texture)) return;
			if (AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath)) return;

            #if UNITY_2020_1_OR_NEWER
			if (AssetDatabase.IsAssetImportWorkerProcess()) return;
            #endif
			AssetDatabase.AddObjectToAsset(_texture, this);
			AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);
#endif
		}
		#if UNITY_EDITOR
		[NonSerialized] private Context _context;

		#endif

		public void OnAfterDeserialize() { }

		public void OnBeforeSerialize()
		{
#if UNITY_EDITOR
			if (!_texture || _texture.name == name) return;
			_texture.name = name;
  #endif
		}
	}

#if !ODIN_INSPECTOR
	[InitializeOnLoad]
	public class ExtensionContextMenu
	{
		static ExtensionContextMenu()
		{
			EditorApplication.contextualPropertyMenu -= OnContextualPropertyMenu;
			EditorApplication.contextualPropertyMenu += OnContextualPropertyMenu;
		}

		private static void OnContextualPropertyMenu(GenericMenu menu, SerializedProperty property)
		{
			Debug.Log("context menu");
			if (property.isArray) return;
			if (property.propertyType != SerializedPropertyType.ManagedReference) return;
			if (GetRealTypeFromTypename(property.managedReferenceFieldTypename) != typeof(ProceduralTexture.ILayer)) { return; }

			var propertyCopy = property.Copy();
			var types = TypeCache.GetTypesDerivedFrom<ProceduralTexture.ILayer>().Where(t => (t.Attributes & TypeAttributes.Serializable) != 0);

			foreach (Type type in types)
			{
				menu.AddItem(new GUIContent($"set to {type.Name}"), false, () =>
				{
					propertyCopy.serializedObject.Update();

					foreach (var target in property.serializedObject.targetObjects)
					{
						Undo.RegisterCompleteObjectUndo(target, $"change type to {type.Name}");
					}
					propertyCopy.managedReferenceValue = Activator.CreateInstance(type);
					propertyCopy.serializedObject.ApplyModifiedProperties();
				});
			}
		}

		private static (string AssemblyName, string ClassName) GetSplitNamesFromTypename(string typename)
		{
			if (string.IsNullOrEmpty(typename))
				return ("", "");

			var typeSplitString = typename.Split(char.Parse(" "));
			var typeClassName = typeSplitString[1];
			var typeAssemblyName = typeSplitString[0];
			return (typeAssemblyName, typeClassName);
		}

		// Gets real type of managed reference's field typeName
		private static Type GetRealTypeFromTypename(string stringType)
		{
			var names = GetSplitNamesFromTypename(stringType);
			var realType = Type.GetType($"{names.ClassName}, {names.AssemblyName}");
			return realType;
		}
	}
#endif
}
