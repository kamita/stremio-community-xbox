using System;
using System.IO;
using System.Text;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

class P
{
    static void Main(string[] args)
    {
        string win = args[0];
        var sb = new StringBuilder();
        using (var fs = File.OpenRead(win))
        using (var pr = new PEReader(fs))
        {
            var md = pr.GetMetadataReader();
            string[] want = { "SubtitleTrack", "TimedMetadataTrack" };
            foreach (var w in want)
            {
                bool found = false;
                foreach (var th in md.TypeDefinitions)
                {
                    var td = md.GetTypeDefinition(th);
                    if (md.GetString(td.Name) != w) continue;
                    found = true;
                    sb.AppendLine("==== " + w + " ====");
                    foreach (var ph in td.Properties)
                    {
                        var pd = md.GetPropertyDefinition(ph);
                        sb.AppendLine("  P: " + md.GetString(pd.Name));
                    }
                    foreach (var mh in td.Methods)
                    {
                        var mdef = md.GetMethodDefinition(mh);
                        var n = md.GetString(mdef.Name);
                        if (n.StartsWith("get_") || n.StartsWith("set_"))
                            sb.AppendLine("  M: " + n);
                    }
                }
                if (!found) sb.AppendLine(w + ": NOT FOUND");
            }
        }
        File.WriteAllText(args[1], sb.ToString());
        Console.WriteLine("wrote " + args[1] + " (" + sb.Length + " chars)");
    }
}
