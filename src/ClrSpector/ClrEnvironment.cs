using System;
using System.Diagnostics;
using System.Linq;

namespace ClrSpector
{
    public class ClrEnvironment
    {
        public static bool IsDebug()
        {
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            var coreLibrary = assemblies.FirstOrDefault(a => a.Modules.Any(m => m.Name == "System.Private.CoreLib.dll"));
            return coreLibrary.CustomAttributes.OfType<DebuggableAttribute>() != null;
        }
    }
}
