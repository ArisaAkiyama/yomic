using System;
using System.Reflection;
using System.Runtime.Loader;

namespace MyMangaApp.Core.Services
{
    public class ExtensionLoadContext : AssemblyLoadContext
    {
        private readonly AssemblyDependencyResolver _resolver;

        public ExtensionLoadContext(string pluginPath) : base(isCollectible: true)
        {
            _resolver = new AssemblyDependencyResolver(pluginPath);
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            // IMPORTANT: Force sharing of Core libraries.
            // If the extension tries to load "MyMangaApp.Shared", we MUST return null
            // so that it falls back to the Default context (which has the app's version).
            // This ensures (IMangaSource from Extension) == (IMangaSource from App).
            
            if (assemblyName.Name == "MyMangaApp.Shared" || 
                assemblyName.Name == "Avalonia.Base" ||
                assemblyName.Name == "Avalonia.Controls" ||
                assemblyName.Name == "ReactiveUI" ||
                assemblyName.Name == "HtmlAgilityPack" ||
                assemblyName.Name == "Newtonsoft.Json" || 
                assemblyName.Name == "PuppeteerSharp") 
            {
                return null; 
            }

            string? assemblyPath = _resolver.ResolveAssemblyToPath(assemblyName);
            if (assemblyPath != null)
            {
                return LoadFromAssemblyPath(assemblyPath);
            }

            return null;
        }

        protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
        {
            string? libraryPath = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            if (libraryPath != null)
            {
                return LoadUnmanagedDllFromPath(libraryPath);
            }

            return IntPtr.Zero;
        }
    }
}
