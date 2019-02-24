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
            var coreLibrary = assemblies.First(a => a.Modules.Any(m => m.Name == "System.Private.CoreLib.dll"));

            foreach (var attribute in coreLibrary.GetCustomAttributes(false))
            {
                if (attribute is DebuggableAttribute debuggableAttribute)
                {
                    return debuggableAttribute.IsJITTrackingEnabled;
                }
            }

            return false;
        }
    }
}
