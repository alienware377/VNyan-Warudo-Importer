using System.Reflection;

// The bundle's MonoScripts reference these assemblies at Version=1.0.0.0 (confirmed by
// decompressing a real VRChat-authored .warudo). Unity's player-side script binding is by
// assembly simple-name, but matching the version too removes any ambiguity and lets the
// in-editor (AssetDatabase) binding used by the offline converter resolve as well.
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]
