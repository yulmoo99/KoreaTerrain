using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;

namespace KoreaTerrain;

internal static class DependencyResolver
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        AssemblyLoadContext.Default.Resolving += (context, name) =>
        {
            if (name.Name != "NetTopologySuite") return null;
            string path = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(typeof(DependencyResolver).Assembly.Location)!, "NetTopologySuite.dll");
            if (!System.IO.File.Exists(path)) return null;
            return context.LoadFromAssemblyPath(path);
        };
    }
}
