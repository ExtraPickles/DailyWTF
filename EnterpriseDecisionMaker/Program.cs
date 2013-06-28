//This code is in the Public Domain. However, I dont recommend you use this code.

using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.JScript;
using Convert = System.Convert;

namespace EnterpriseDecisionMaker
{
    internal class Program
    {
        public static Dictionary<string, string> rulePrograms = new Dictionary<string, string>();
        public static readonly Dictionary<string, string[]> ruleVariants = new Dictionary<string, string[]>();
        public static readonly Dictionary<string, string[]> ruleOptions = new Dictionary<string, string[]>();
        private static readonly TcpListener webListener = new TcpListener(IPAddress.Any, 80);
        public static cHTTPClient RequestProccessor = new cHTTPClient();

        private static void Main(string[] args)
        {
            RequestProccessor.fPath = @"c:\temp\log.log";

            foreach (string s in Assembly.GetExecutingAssembly().GetManifestResourceNames())
            {
                if (s.EndsWith(".rule"))
                {
                    using (var s2 = new StreamReader(Assembly.GetExecutingAssembly().GetManifestResourceStream(s)))
                    {
                        string progy = s2.ReadToEnd();

                        string[] hunklets = progy.Split('\n').Select(e => e.Trim().Trim('.', ';')).ToArray();
                        string name = hunklets[0].Remove(0, "Define Rule ".Length).Trim().Trim('{').Trim('}');
                        if (rulePrograms.ContainsKey(name)) continue;
                        IEnumerable<string> chunks =
                            hunklets[1].Remove(0, "Rule has Options ".Length).Split(',').Select(
                                s3 => s3.Trim().Trim('{').Trim('}'));
                        ruleVariants.Add(name,
                                         chunks.Select(
                                             c =>
                                             "\"" + (c.StartsWith("and") ? c.Remove(0, 3).Trim().Trim('{') : c) + "\"").
                                             ToArray());
                        string[] options = hunklets[2].Remove(0, "Rule has Options ".Length).Split(',').ToArray();

                        if (options.Length == 1)
                            options = hunklets[2].Remove(0, "Rule has Options ".Length).Split(new[] {"and"},
                                                                                              StringSplitOptions.None).
                                ToArray();
                        options = options.Select(o => o.Replace("and", "").Trim().Trim('{').Trim('}')).ToArray();
                        ruleOptions.Add(name, options);
                        rulePrograms.Add(name, progy);
                    }
                }
            }

            foreach (string s in Directory.EnumerateFiles(Assembly.GetExecutingAssembly().Location, "*.rule"))
            {
                if (s.EndsWith(".rule"))
                {
                    using (var s2 = new StreamReader(s))
                    {
                        string progy = s2.ReadToEnd();

                        string[] hunklets = progy.Split('\n').Select(e => e.Trim().Trim('.', ';')).ToArray();
                        string name = hunklets[0].Remove(0, "Define Rule ".Length).Trim().Trim('{').Trim('}');
                        if (rulePrograms.ContainsKey(name)) continue;

                        IEnumerable<string> chunks =
                            hunklets[1].Remove(0, "Rule has Options ".Length).Split(',').Select(
                                s3 => s3.Trim().Trim('{').Trim('}'));
                        ruleVariants.Add(name,
                                         chunks.Select(
                                             c =>
                                             "\"" + (c.StartsWith("and") ? c.Remove(0, 3).Trim().Trim('{') : c) + "\"").
                                             ToArray());
                        string[] options = hunklets[2].Remove(0, "Rule has Options ".Length).Split(',').ToArray();

                        if (options.Length == 1)
                            options = hunklets[2].Remove(0, "Rule has Options ".Length).Split(new[] {"and"},
                                                                                              StringSplitOptions.None).
                                ToArray();
                        options = options.Select(o => o.Replace("and", "").Trim().Trim('{').Trim('}')).ToArray();
                        ruleOptions.Add(name, options);
                        rulePrograms.Add(name, progy);
                    }
                }
            }

            webListener.Start();
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
            while (true)
            {
                if (webListener.Pending())
                {
                    TcpClient websock = webListener.AcceptTcpClient();
                    if (websock.Client.RemoteEndPoint.ToString().Contains("127."))
                        //Limit access to localhost as user level security is due in phase 2
                    {
                        var t = new Thread(handleClient);
                        t.Start(websock);
                    }
                }
                Thread.Sleep(1); //Dont hog all of the cpu
            }
        }

        private static void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            e.DumpItToFile();
            MessageBox.Show(((Exception) e.ExceptionObject).Message, "Unhandled exception",
                            MessageBoxButtons.AbortRetryIgnore, MessageBoxIcon.Exclamation);
        }

        private static void handleClient(object pass)
        {
            var websock = (TcpClient) pass;
            while (websock.Connected)
            {
                NetworkStream w = websock.GetStream();
                var b = new Byte[256];
                var request = new StringBuilder();

                if (w.DataAvailable)
                {
                    int i;
                    while ((i = w.Read(b, 0, b.Length)) != 0)
                    {
                        request.Append(Encoding.ASCII.GetString(b, 0, i));
                        if (i < 256) break;
                    }
                }
                if (request.ToString().Length > 4 &&
                    request.ToString().Substring(request.ToString().Length - 4, 4) == "\x0D\x0A\x0D\x0A")
                {
                    RequestProccessor.Client = websock.Client.RemoteEndPoint;
                    string r = RequestProccessor.doIt(request.ToString(), w);
                    if (r != "@@ALREADY SENT@@")
                    {
                        byte[] rb = Encoding.ASCII.GetBytes(r);

                        w.Write(rb, 0, rb.Length);
                    }
                    request.Clear();
                }
                websock.Close();
            }
        }
    }


    public interface ILoggable
    {
        string fPath { get; set; }
        void Log(string s);
    }

    public sealed class cHTTPClient : ILoggable
    {
        public static readonly string[] s_rgsHeader = new[]
                                                          {
                                                              @"GET ",
                                                              @"POST ",
                                                              @"HOST: ",
                                                              @"CONNECTION: ",
                                                              @"ACCEPT: ",
                                                              @"USER-AGENT: ",
                                                              @"ACCEPT-ENCODING: ",
                                                              @"AGENDA:  "
                                                          };

        private static readonly char[] s_rgcNewLine = new[] {(char) 0xD, (char) 0xA};
        public static object logLock = new object();
        public int ImageNumber = -1;
        private Color _clientColor = Color.Red;
        public EndPoint Client { get; internal set; }

        public Color ClientColor
        {
            get { return _clientColor; }
            set { _clientColor = value; }
        }

        #region ILoggable Members

        public string fPath { get; set; }

        public void Log(string s)
        {
            try
            {
                lock (logLock)
                {
                    var file = new StringBuilder();
                    if (!File.Exists(fPath))
                        File.Create(fPath);
                    file.Append(File.ReadAllText(fPath));
                    file.AppendLine(string.Format("{0}\t{1}", DateTime.Now, s));
                    File.WriteAllText(fPath, file.ToString());
                }
            }
            catch (Exception ex)
            {
            }
        }

        #endregion

        public string doIt(string sRequest, NetworkStream w)
        {
            string[] rgsLines = sRequest.Split(s_rgcNewLine);
            var headerMap = new Dictionary<UInt64, string>();
            bool exe = false;
            string exeLine = "";
            foreach (string sLine in rgsLines)
            {
                UInt64 u64whichHeader = UInt64.MaxValue;
                // You never know how many they will add in the future. This should be future proof

                for (UInt64 index = 0x0; index < 0x6; index = index + 0x1)
                {
                    if (sLine.ToUpper().StartsWith(s_rgsHeader[index]))
                    {
                        u64whichHeader = (UInt64) s_rgsHeader[index].Length;
                        //Make it harder for hackers to hack this. All request headers are of a different length, so this works

                        string chunklet = sLine.Remove(0, s_rgsHeader[index].Length);
                        headerMap.Add(index + 5, chunklet);
                    }
                }

                switch (u64whichHeader)
                {
                    case 0xFFFFFFFFFFFFFFFF:
                        //KVP for request not a built in type. Reflect to see if there is a handler plugin
#if ENABLE_PLUGINS                       
                        var assemblies = AppDomain.CurrentDomain.GetAssemblies();
                        foreach (var asm in assemblies)
                        {
                            foreach (var t in asm.GetTypes())
                            {
                                var name = sLine.Split(new [] { ":" }, 1, StringSplitOptions.RemoveEmptyEntries)[0];
                                var toUse = t.GetMethod(name);
                                if (toUse != null)
                                {
                                    toUse.Invoke(null, new object[] { sLine.Remove(0, name.Length).Trim() });
                                }
                            }
                        }
#endif
                        break;
                    case 4:
                        exe = true;
                        exeLine = sLine;
                        break;
                    default:
                        Log("Unknown index found");
                        break;
                }
            }
            if (exe)
                return doIt3(exeLine.Remove(0, 4).Trim(), headerMap, w);
            throw new NotImplementedException();
        }

        private string doIt3(string sRV, Dictionary<ulong, string> rgisR, NetworkStream w)
        {
            var r = new StringBuilder(4096);
            var c = new StringBuilder(4096);
            string s = "200 OK";
            bool? bSuc = null;

            int si = sRV.IndexOf("HTTP/1.1", StringComparison.Ordinal);
            sRV = sRV.Remove(si, "HTTP/1.1".Length).Trim();
            string[] hackstack = sRV.Split('?');
            sRV = hackstack.First();
            string queries = "";
            if (hackstack.Length > 1)
                queries = hackstack[1];
            Console.WriteLine("Got Request " + sRV);
            switch (sRV)
            {
                case "/":
                    {
                        bSuc = true;

                        Stream gm =
                            Assembly.GetExecutingAssembly().GetManifestResourceStream(
                                "EnterpriseDecisionMaker.index.html");
                        var sr = new StreamReader(gm);
                        c.Append(sr.ReadToEnd());
                        sr.Dispose();
                    }
                    break;
                case "/ExecuteCalculation":
                    var bs = new JScriptCodeProvider();
                    ICodeCompiler comp = bs.CreateCompiler();
                    var p = new CompilerParameters {GenerateInMemory = true, IncludeDebugInformation = true};
                    Type t;
                    Assembly c2 =
                        comp.CompileAssemblyFromSource(p,
                                                       @"package jsPackage { class jsExecuteWrapper { public function Execute(code){return eval(code);}}}")
                            .CompiledAssembly;
                    object ins = Activator.CreateInstance(t = c2.GetType("jsPackage.jsExecuteWrapper"));
                    try
                    {
                        c.Append(t.InvokeMember("Execute", BindingFlags.InvokeMethod, null, ins,
                                                new object[] {queries.Replace("%2B", "+").Replace("%2D", "-")}));
                    }
                    catch (Exception ex)
                    {
                        c.Append("Error: " + ex.InnerException.Message);
                    }
                    bSuc = true;
                    break;
                case "/SuperPicture":
                    {
                        queries = Uri.UnescapeDataString(queries);
                        byte[] thingy = Convert.FromBase64String(queries.Remove(0, @"data:image/jpeg;base64,".Length));
                        string prefix = Client.ToString().Replace('.', '-').Split(':').First();
                        IEnumerable<string> files = Directory.EnumerateFiles(@"C:\Temp", prefix + "*.jpg");
                        string file = @"C:\Temp\" + prefix + (files.Count() + 1) + ".jpg";
                        thingy.DumpItToFile(file);
                    }
                    break;
                case "/UndoPicture":
                    {
                        string prefix = Client.ToString().Replace('.', '-').Split(':').First();
                        List<string> files = Directory.EnumerateFiles(@"C:\Temp\", prefix + "*.jpg").ToList();
                        if (files.Count() != 0)
                        {
                            File.Delete(files.Last());
                            files.Remove(files.Last());
                        }
                        if (files.Count() != 0)
                        {
                            c.AppendLine(@"data:image/jpeg;base64," +
                                         Convert.ToBase64String(File.ReadAllBytes(files.Last())));
                        }
                        bSuc = true;
                    }
                    break;
                case "/GetRules":
                    c.Append("[");
                    foreach (var query in Program.ruleVariants)
                    {
                        c.Append("{\"" + query.Key.Replace(" ", "_") + "\": [" +
                                 query.Value.Aggregate((work, next) => work + "," + next).TrimEnd(',') + "]},");
                    }
                    {
                        string b = c.ToString();
                        c.Clear();
                        c.Append(b.Trim().TrimEnd(','));
                    }
                    c.Append("]");
                    bSuc = true;
                    break;
                case "/GetDecision":
                    {
                        string rule = "";
                        string variant = "normal";
                        string agenda = "";
                        foreach (string query in queries.Split('&'))
                        {
                            string[] equ = query.Split('=');
                            if (equ[0] == "rule")
                            {
                                rule = equ[1];
                            }
                            else if (equ[0] == "variant")
                            {
                                variant = equ[1];
                            }
                            else if (equ[0] == "agenda")
                            {
                                agenda = equ[1];
                            }
                        }
                        string spain = rule.Replace("+", " ");
                        if (Program.rulePrograms.ContainsKey(spain))
                        {
                            string[] code = Program.rulePrograms[spain].Split(new[] {"\r\n"},
                                                                              StringSplitOptions.RemoveEmptyEntries);
                            c.AppendFormat(
                                "A Decision has been made according to {0}:{1}\nItem to be decided on: {2}\n\nRuling: {3}",
                                spain, variant, agenda, DoIt4(spain, code, variant));
                            c.DumpItToFile();
                            bSuc = true;
                        }
                    }
                    break;
                default:
                    try
                    {
                        Stream f =
                            Assembly.GetExecutingAssembly().GetManifestResourceStream("EnterpriseDecisionMaker." +
                                                                                      sRV.TrimStart('/').Replace('/',
                                                                                                                 '.'));
                        if (f == null)
                        {
                            bSuc = false;
                            s = "404 Not Found";
                        }
                        else
                        {
                            var sr = new StreamReader(f);
                            string toSend = sr.ReadToEnd();
                            bool isBinary = false;
                            foreach (char by in toSend)
                            {
                                if (by < 32 || by > 126)
                                    isBinary = true;
                                if (isBinary)
                                    break;
                            }
                            if (isBinary)
                            {
                                switch (sRV.Split('.').Last())
                                {
                                    case "gif":
                                        r.AppendLine("Content-Type: image/gif");
                                        break;
                                    case "png":
                                        r.AppendLine("Content-Type: image/png");
                                        break;
                                    case "jpg":
                                        r.AppendLine("Content-Type: image/jpg");
                                        break;
                                    case "ico":
                                        r.AppendLine("Content-Type: image/icon");
                                        break;
                                }
                                f.Seek(0, SeekOrigin.Begin);
                                var br = new BinaryReader(f);
                                int l = Convert.ToInt32(f.Length);
                                byte[] bts = br.ReadBytes(l);

                                byte[] head =
                                    Encoding.ASCII.GetBytes(string.Format(
                                        "HTTP/1.1 200 OK\n{0}content-length: {1}\n\n", r, bts.Length));
                                w.Write(head, 0, head.Length);
                                w.Write(bts, 0, bts.Length);
                                sr.Dispose();

                                return "@@ALREADY SENT@@";
                            }
                            c.Append(toSend);

                            sr.Dispose();
                            bSuc = true;
                        }
                    }
                    catch (Exception ex)
                    {
                        bSuc = null;
                        s = ex.Message;
                    }
                    break;
            }
            if (bSuc == true)
            {
                Console.Write("Success\n");
            }
            else
            {
                Log("Failed to get " + sRV);
            }

            if (bSuc == null)
                return string.Format("HTTP/1.1 500 Internal server error:\t{0}\n\n", s);
            return bSuc == true
                       ? string.Format("HTTP/1.1 {3}\n{0}content-length: {2}\n\n{1}\n\n", r, c, c.Length, s)
                       : string.Format("HTTP/1.1 {0}\n\n", s);
        }

        private static string DoIt4(string name, string[] code, string variant)
        {
            int count = Convert.ToInt32(code[3].Replace("Option is chosen by RandomChoice(", "").Replace("):", ""));
            int random = Convert.ToInt32(Math.Floor((Math.Sin(DateTime.Now.Ticks*426.55) + 1)/2*count)) + 1;

            bool variantMode = false;
            for (uint i = 4; i < code.Length; i++)
            {
                string line = code[i].Trim();
                if (line == "Map by Index.")
                    return Program.ruleOptions[name][random - 1];

                if (line.StartsWith("Any => "))
                {
                    return line.Remove(0, @"Any => ".Length).Replace("{", "").Replace("}", "").Replace(";", "");
                }

                if (line.StartsWith(random.ToString(CultureInfo.InvariantCulture) + " -> "))
                {
                    return
                        line.Remove(0, (random.ToString(CultureInfo.InvariantCulture) + " -> ").Length).Replace("{", "")
                            .Replace("}", "").Replace(";", "");
                }

                if (line.StartsWith("When variant is "))
                {
                    if (variant != line.Remove(0, "When variant is {".Length).Replace("}:", ""))
                    {
                        while (!code[++i].EndsWith(":"))
                        {
                        }
                    }
                }
            }
            return "No result. Please try again later";
        }
    }


    public static class StringSuperHelpers
    {
        public static void DumpItToFile(this object vic)
        {
            const string fPath = @"C:\temp\Dump.bin";
            var file = new StringBuilder();
            if (!File.Exists(fPath)) File.Create(fPath);
            file.Append(File.ReadAllText(fPath));
            file.AppendLine(string.Format("{1}", DateTime.Now, vic));
            File.WriteAllText(fPath, file.ToString());
        }

        public static void DumpItToFile(this object vic, string fpath)
        {
            FileStream file = File.Open(fpath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite);
                //FileShare.ReadWrite fixes Bug #48339
            try
            {
                file.Seek(0, SeekOrigin.Begin);
                if (vic is byte[])
                {
                    file.Write((byte[]) vic, 0, ((byte[]) vic).Length);
                }
                else
                {
                    byte[] bt = Encoding.ASCII.GetBytes(file.ToString());
                    file.Write(bt, 0, bt.Length);
                }
            }
            finally
            {
                file.Dispose()
                    ;
                file = null;
                GC.Collect(GC.MaxGeneration);
            }
        }

        public static T MakeItSo<T>(this string course)
        {
            Type ncc_1701;
            var ins =
                Activator.CreateInstance(
                    ncc_1701 = new JScriptCodeProvider().CreateCompiler().CompileAssemblyFromSource(
                        new CompilerParameters {GenerateInMemory = true},
                        @"class Picard { public function Helmsman(Data){return eval(Data);}}")
                                   .CompiledAssembly.GetType("Picard"));
            return (T) ncc_1701.InvokeMember("Helmsman", BindingFlags.InvokeMethod, null, ins, new object[] {course});
        }
    }
}