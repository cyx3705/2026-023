using System.Reflection;
using System.Runtime.Loader;

namespace HistoryVulcan.Services.Modules;

internal sealed partial class ModuleHost
{
    /// <summary>可回收的加载上下文：模块及其依赖从 nupkg 的字节装入，不锁包文件。</summary>
    private sealed class ModuleLoadContext : AssemblyLoadContext
    {
        private readonly IReadOnlyDictionary<string, byte[]> _assemblies;

        public ModuleLoadContext(IReadOnlyDictionary<string, byte[]> assemblies)
            : base("Modules-" + DateTime.Now.ToString("HHmmssfff"), isCollectible: true)
            => _assemblies = assemblies;

        protected override Assembly? Load(AssemblyName name)
            => TryLoadFromPackage(name, out var loaded) ? loaded : null;

        /// <summary>只从本包装载。找不到就返回 false，绝不回落到 Default。</summary>
        internal bool TryLoadFromPackage(AssemblyName name, out Assembly? assembly)
        {
            assembly = null;
            if (string.IsNullOrEmpty(name.Name))
                return false;

            foreach (var loaded in Assemblies)
            {
                if (string.Equals(loaded.GetName().Name, name.Name, StringComparison.OrdinalIgnoreCase))
                {
                    assembly = loaded;
                    return true;
                }
            }

            if (!_assemblies.TryGetValue(name.Name, out var bytes))
                return false;

            assembly = LoadFromStream(new MemoryStream(bytes));
            return true;
        }
    }
}
