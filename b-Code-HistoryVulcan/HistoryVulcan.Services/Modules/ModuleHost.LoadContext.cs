using System.Reflection;
using System.Runtime.Loader;

namespace HistoryVulcan.Services.Modules;

public sealed partial class ModuleHost
{
    /// <summary>文件可能正在拷贝中,重试读取。</summary>
    internal static byte[] ReadFileWithRetry(string path)
    {
        for (var i = 0; ; i++)
        {
            try
            {
                return File.ReadAllBytes(path);
            }
            catch (IOException) when (i < 5)
            {
                Thread.Sleep(300);
            }
        }
    }

    /// <summary>可回收的加载上下文:模块及其依赖全部从内存流加载,不锁磁盘文件。</summary>
    private sealed class ModuleLoadContext : AssemblyLoadContext
    {
        private readonly string _dir;

        public ModuleLoadContext(string dir)
            : base("Modules-" + DateTime.Now.ToString("HHmmssfff"), isCollectible: true)
            => _dir = dir;

        protected override Assembly? Load(AssemblyName name)
        {
            // 模块目录里有同名 DLL 就从内存加载;否则返回 null 回落到默认上下文(框架程序集)
            return TryLoadFromPackage(name, out var loaded) ? loaded : null;
        }

        /// <summary>
        /// 只从本包目录装载。找不到就返回 false，绝不回落到 Default。
        /// </summary>
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

            var path = Path.Combine(_dir, name.Name + ".dll");
            if (!File.Exists(path))
                return false;

            assembly = LoadFromStream(new MemoryStream(ReadFileWithRetry(path)));
            return true;
        }
    }
}
