using System;
using System.IO;
using System.Reflection;
using DlssgManager;
#if NEXT
[assembly: AssemblyVersion("2.0.1.0")]
#else
[assembly: AssemblyVersion("2.0.0.0")]
#endif
static class Fixture
{
    static int Main(string[] args)
    {
        try
        {
#if NEXT
            File.WriteAllText(Assembly.GetExecutingAssembly().Location + ".updated-marker", "2.0.1");
#else
            GuiUpdater.StartReplacement(args[0]).GetAwaiter().GetResult();
#endif
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }
}
