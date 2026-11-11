using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace VRMod
{
    /// <summary>
    /// What sits around the game's VR session on this PC: the OpenXR layers other programs have
    /// installed, the runtime's own log files, and a report file that survives the next start.
    ///
    /// Layers: an OpenXR API layer is a library another program registers with Windows; the
    /// OpenXR loader puts it between every game and the headset's runtime. Each layer names an
    /// environment variable that switches it off, and setting that variable inside this process
    /// before OpenXR starts runs the game without the layer, without changing the PC.
    ///
    /// Everything here runs on the main thread.
    /// </summary>
    internal static class OpenXRSurroundings
    {
        private static readonly IntPtr LocalMachine = new IntPtr(unchecked((int)0x80000002));
        private static readonly IntPtr CurrentUser = new IntPtr(unchecked((int)0x80000001));
        private const uint Read64 = 0x20019 | 0x0100; // KEY_READ | KEY_WOW64_64KEY

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
        private static extern int RegOpenKeyExW(IntPtr key, string subKey, uint options, uint access, out IntPtr result);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
        private static extern int RegEnumValueW(IntPtr key, uint index, StringBuilder name, ref uint nameLength, IntPtr reserved, out uint type, byte[] data, ref uint dataLength);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
        private static extern int RegQueryValueExW(IntPtr key, string name, IntPtr reserved, out uint type, byte[] data, ref uint dataLength);
        [DllImport("advapi32.dll")]
        private static extern int RegCloseKey(IntPtr key);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern bool SetEnvironmentVariableW(string name, string value);

        internal sealed class Layer
        {
            public string where, manifest, name = "", library = "", offSwitch = "";
            public bool on, readable;
        }

        private static string folder = "", version = "", layersNow = "kept", runtimeNow = "", noHelpNow = "";

        /// <summary>The manifest of the PC's default OpenXR runtime, from the registry.</summary>
        internal static string activeRuntime = "";

        /// <summary>Where the list of installed layers comes from: the Windows registry, unless a test replaces it.</summary>
        internal static Action<List<Layer>> LayerSource = ReadRegistry;

        private static void ReadRegistry(List<Layer> layers)
        {
            activeRuntime = ReadString(LocalMachine, @"SOFTWARE\Khronos\OpenXR\1", "ActiveRuntime");
            ReadLayers(LocalMachine, "all users", layers);
            ReadLayers(CurrentUser, "this user", layers);
        }
        private static bool headerWritten;

        /// <summary>True when this start runs without the other programs' layers.</summary>
        internal static bool LayersSkipped { get { return layersNow == "skipped"; } }

        internal static void Configure(string reportFolder, string modVersion)
        {
            folder = reportFolder ?? "";
            version = modVersion ?? "";
            try
            {
                if (folder.Length == 0) return;
                Directory.CreateDirectory(folder);
                // Keep the report from growing without end: the last few starts are what matter.
                FileInfo report = new FileInfo(ReportPath);
                if (report.Exists && report.Length > 400 * 1024)
                {
                    string text = File.ReadAllText(ReportPath);
                    File.WriteAllText(ReportPath, "(older starts removed)\n" + text.Substring(text.Length - 150 * 1024));
                }
            }
            catch { }
        }

        internal static string ReportPath { get { return Path.Combine(folder, "report.txt"); } }
        private static string StatePath { get { return Path.Combine(folder, "state.txt"); } }

        /// <summary>Adds lines to the report file, with a heading for the first lines of this start.</summary>
        internal static void AppendReport(IEnumerable<string> lines)
        {
            if (folder.Length == 0) return;
            try
            {
                StringBuilder text = new StringBuilder();
                if (!headerWritten)
                {
                    headerWritten = true;
                    text.Append($"\n==== Game started {DateTime.Now:yyyy-MM-dd HH:mm:ss}, Resurrected VRMod {version} ====\n");
                }
                foreach (string line in lines) text.Append(line).Append('\n');
                File.AppendAllText(ReportPath, text.ToString());
            }
            catch { }
        }

        // What the last start left behind: whether it ran with the layers, how it went, which
        // runtime it used, and for which runtime a start without the layers was refused as well.
        private static void ReadState(out string layers, out string outcome, out string runtime, out string noHelp)
        {
            layers = "kept"; outcome = "none"; runtime = ""; noHelp = "";
            try
            {
                if (folder.Length == 0 || !File.Exists(StatePath)) return;
                foreach (string line in File.ReadAllLines(StatePath))
                {
                    if (line.StartsWith("layers=")) layers = line.Substring(7).Trim();
                    else if (line.StartsWith("outcome=")) outcome = line.Substring(8).Trim();
                    else if (line.StartsWith("runtime=")) runtime = line.Substring(8).Trim();
                    else if (line.StartsWith("withoutLayersNoBetter=")) noHelp = line.Substring(22).Trim();
                }
            }
            catch { }
        }

        /// <summary>Remembers for the next start how this one went: "accepted" or "refused".</summary>
        internal static void RecordOutcome(string outcome)
        {
            try
            {
                if (folder.Length > 0)
                    File.WriteAllText(StatePath, "layers=" + layersNow + "\noutcome=" + outcome + "\nruntime=" + runtimeNow + "\nwithoutLayersNoBetter=" + noHelpNow + "\n");
            }
            catch { }
        }

        /// <summary>
        /// Lists the default runtime and the installed layers, and switches the layers off for
        /// this start when the setting says so. Call before the OpenXR loader starts.
        /// mode: "Keep", "Skip", or "Auto". Auto skips when the last start with the layers on was
        /// refused every eye texture, and keeps skipping while that works. When a start without
        /// the layers is refused as well, the layers are not the cause: Auto leaves them on from
        /// then on for that runtime and mod version. What one runtime did says nothing about another.
        /// </summary>
        internal static void BeforeLoader(string mode, Action<string, bool> say)
        {
            string lastLayers, lastOutcome, lastRuntime, noHelp;
            ReadState(out lastLayers, out lastOutcome, out lastRuntime, out noHelp);
            List<Layer> layers = new List<Layer>();
            string problem = "";
            try
            {
                LayerSource(layers);
            }
            catch (Exception e)
            {
                problem = e.GetType().Name + ": " + e.Message;
            }

            string overridden = Environment.GetEnvironmentVariable("XR_RUNTIME_JSON");
            say("Default OpenXR runtime on this PC: " + (activeRuntime.Length > 0 ? activeRuntime : "(not found)") +
                (string.IsNullOrEmpty(overridden) ? "" : "; overridden for this process by XR_RUNTIME_JSON=" + overridden) +
                (problem.Length > 0 ? " (registry could not be read: " + problem + ")" : ""), false);

            runtimeNow = (string.IsNullOrEmpty(overridden) ? activeRuntime : overridden).Replace('\n', ' ').Trim();
            string thisCase = (version + " | " + runtimeNow).Trim();
            bool sameRuntime = string.Equals(lastRuntime, runtimeNow, StringComparison.OrdinalIgnoreCase);
            string learned = "";
            if (sameRuntime && lastLayers == "skipped" && lastOutcome == "refused" && noHelp != thisCase)
            {
                noHelp = thisCase;
                learned = "On the last start the runtime refused every eye texture without the other programs' layers as well, so those layers are not the cause. They stay on from now on.";
            }
            noHelpNow = noHelp;

            bool skip = false; string why = "";
            if (string.Equals(mode, "Skip", StringComparison.OrdinalIgnoreCase)) { skip = true; why = "the setting 'OpenXR layers' is Skip"; }
            else if (!string.Equals(mode, "Keep", StringComparison.OrdinalIgnoreCase) && sameRuntime)
            {
                if (lastLayers == "kept" && lastOutcome == "refused" && noHelp != thisCase) { skip = true; why = "on the last start the runtime refused every eye texture with the layers on, and 'OpenXR layers' is Auto"; }
                else if (lastLayers == "skipped" && lastOutcome == "accepted") { skip = true; why = "the last start worked without them, and 'OpenXR layers' is Auto (set it to Keep to have them back)"; }
            }

            int on = 0;
            StringBuilder list = new StringBuilder();
            foreach (Layer layer in layers)
            {
                if (layer.on) ++on;
                bool switched = false;
                if (skip && layer.on && layer.offSwitch.Length > 0)
                {
                    // Set both ways: through the runtime library and directly in Windows, which is
                    // where the OpenXR loader reads it.
                    try { Environment.SetEnvironmentVariable(layer.offSwitch, "1"); switched = true; } catch { }
                    try { if (SetEnvironmentVariableW(layer.offSwitch, "1")) switched = true; } catch { }
                }
                list.Append($"\n    {(layer.name.Length > 0 ? layer.name : "(name not readable)")}: {(layer.on ? "on" : "off")}" +
                            (switched ? ", SWITCHED OFF FOR THIS START" : skip && layer.on ? ", could not be switched off (no off switch found)" : "") +
                            $"; {layer.manifest}" + (layer.library.Length > 0 ? $"; library {layer.library}" : "") +
                            (layer.offSwitch.Length > 0 ? $"; off switch {layer.offSwitch}" : "") + $"; registered for {layer.where}");
            }
            say($"OpenXR layers other programs have installed ({layers.Count}, {on} on):" + (layers.Count == 0 ? " none" : list.ToString()), skip && on > 0);
            if (skip && on > 0) say("This start runs without those layers, because " + why + ". Nothing on the PC is changed; it applies to this game process only.", true);
            else if (learned.Length > 0) say(learned, true);
            layersNow = skip && on > 0 ? "skipped" : "kept";
            RecordOutcome("unknown");
        }

        private static string ReadString(IntPtr root, string subKey, string name)
        {
            IntPtr key;
            if (RegOpenKeyExW(root, subKey, 0, Read64, out key) != 0) return "";
            try
            {
                byte[] data = new byte[2048]; uint length = (uint)data.Length, type;
                if (RegQueryValueExW(key, name, IntPtr.Zero, out type, data, ref length) != 0 || (type != 1 && type != 2)) return "";
                return Encoding.Unicode.GetString(data, 0, (int)Math.Min(length, (uint)data.Length)).TrimEnd('\0');
            }
            finally
            {
                RegCloseKey(key);
            }
        }

        // Each value under ...\ApiLayers\Implicit is the path of a layer's manifest; 0 means on.
        private static void ReadLayers(IntPtr root, string where, List<Layer> layers)
        {
            IntPtr key;
            if (RegOpenKeyExW(root, @"SOFTWARE\Khronos\OpenXR\1\ApiLayers\Implicit", 0, Read64, out key) != 0) return;
            try
            {
                for (uint index = 0; index < 64; index++)
                {
                    StringBuilder name = new StringBuilder(1024);
                    uint nameLength = 1024, type, dataLength = 16;
                    byte[] data = new byte[16];
                    int result = RegEnumValueW(key, index, name, ref nameLength, IntPtr.Zero, out type, data, ref dataLength);
                    if (result == 259) break;      // ERROR_NO_MORE_ITEMS
                    if (result != 0) continue;
                    Layer layer = new Layer { where = where, manifest = name.ToString(), on = type == 4 && BitConverter.ToInt32(data, 0) == 0 };
                    ReadManifest(layer);
                    layers.Add(layer);
                }
            }
            finally
            {
                RegCloseKey(key);
            }
        }

        internal static void ReadManifest(Layer layer)
        {
            try
            {
                if (!File.Exists(layer.manifest)) return;
                string json = File.ReadAllText(layer.manifest);
                layer.readable = true;
                layer.name = Field(json, "name");
                layer.library = Field(json, "library_path").Replace("\\\\", "\\");
                layer.offSwitch = Field(json, "disable_environment");
            }
            catch { }
        }

        private static string Field(string json, string name)
        {
            Match match = Regex.Match(json, "\"" + name + "\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
            return match.Success ? match.Groups[1].Value : "";
        }

        /// <summary>
        /// The last lines of the log files the headset's runtime wrote recently. The runtime knows
        /// why it refused; its own log is the most direct place to read that.
        /// </summary>
        internal static string RuntimeLogExcerpts(string runtimeName)
        {
            StringBuilder text = new StringBuilder();
            try
            {
                List<string> words = new List<string> { "openxr" };
                foreach (string word in Regex.Split((runtimeName ?? "").ToLowerInvariant(), "[^a-z0-9]+"))
                    if (word.Length >= 4 && word != "openxr" && word != "runtime" && !words.Contains(word)) words.Add(word);

                List<string> roots = new List<string>(), programRoots = new List<string>();
                string runtimeFolder = "";
                try
                {
                    if (activeRuntime.Length > 0)
                    {
                        runtimeFolder = Path.GetDirectoryName(activeRuntime) ?? "";
                        string file = Regex.Replace(Path.GetFileNameWithoutExtension(activeRuntime).ToLowerInvariant(), "[^a-z]+.*$", "");
                        if (file.Length >= 4 && !words.Contains(file)) words.Add(file);
                        if (runtimeFolder.Length > 0) programRoots.Add(runtimeFolder);
                        string vendor = Path.GetDirectoryName(runtimeFolder) ?? "";
                        // "C:\Program Files\Pimax\Runtime" also gives "C:\Program Files\Pimax", but never a drive or Program Files itself.
                        if (vendor.Length > 0 && Path.GetDirectoryName(vendor) != null && !Regex.IsMatch(Path.GetFileName(vendor) ?? "", "^(program files.*|programdata|windows|steamapps|common)$", RegexOptions.IgnoreCase))
                        {
                            programRoots.Add(vendor);
                            string vendorWord = (Path.GetFileName(vendor) ?? "").ToLowerInvariant();
                            if (vendorWord.Length >= 4 && !words.Contains(vendorWord)) words.Add(vendorWord);
                        }
                    }
                }
                catch { }

                string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                string[] dataFolders =
                {
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    profile.Length > 0 ? Path.Combine(Path.Combine(profile, "AppData"), "LocalLow") : "",
                };
                foreach (string data in dataFolders)
                {
                    try
                    {
                        if (data.Length == 0 || !Directory.Exists(data)) continue;
                        foreach (string directory in Directory.GetDirectories(data))
                        {
                            string name = Path.GetFileName(directory).ToLowerInvariant();
                            foreach (string word in words)
                                if (name.Contains(word)) { roots.Add(directory); break; }
                        }
                    }
                    catch { }
                }

                // The data folders first: that is where logs usually are. The program folders last.
                roots.AddRange(programRoots);
                List<FileInfo> found = new List<FileInfo>();
                DateTime oldest = DateTime.Now.AddDays(-2);
                foreach (string root in roots)
                {
                    int visited = 0;
                    Collect(root, 0, oldest, found, ref visited);
                }
                found.Sort((a, b) => b.LastWriteTime.CompareTo(a.LastWriteTime));
                // A headset's software writes many logs. The ones that talk about eye textures,
                // OpenXR or this game come first; among equals, the newest.
                if (found.Count > 40) found.RemoveRange(40, found.Count - 40);
                string game = "";
                try { game = System.Diagnostics.Process.GetCurrentProcess().ProcessName.ToLowerInvariant(); } catch { }
                Dictionary<string, int> scores = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (FileInfo file in found) scores[file.FullName] = Relevance(file, game);
                List<FileInfo> ranked = new List<FileInfo>(found);
                found.Sort((a, b) =>
                {
                    int byScore = scores[b.FullName].CompareTo(scores[a.FullName]);
                    return byScore != 0 ? byScore : ranked.IndexOf(a).CompareTo(ranked.IndexOf(b));
                });

                text.Append("The runtime's own log files (searched " + roots.Count + " folder(s) for names containing: " + string.Join(", ", words.ToArray()) + "):");
                if (roots.Count == 0 || found.Count == 0) { text.Append(" none written in the last two days were found."); return text.ToString(); }
                int shown = 0;
                HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (FileInfo file in found)
                {
                    if (!seen.Add(file.FullName)) continue;
                    if (shown >= 6) { text.Append($"\n    (not shown) {file.FullName}, {file.Length} bytes, written {file.LastWriteTime:yyyy-MM-dd HH:mm:ss}"); if (++shown > 14) break; continue; }
                    ++shown;
                    text.Append($"\n    ---- {file.FullName}, {file.Length} bytes, written {file.LastWriteTime:yyyy-MM-dd HH:mm:ss}");
                    text.Append(Tail(file.FullName));
                }
            }
            catch (Exception e)
            {
                text.Append(" could not be collected: " + e.GetType().Name + ": " + e.Message);
            }
            return text.ToString();
        }

        private static int Relevance(FileInfo file, string game)
        {
            int score = 0;
            try
            {
                string name = file.Name.ToLowerInvariant(), content = ReadEnd(file.FullName).ToLowerInvariant();
                if (content.Contains("swapchain") || content.Contains("swap chain")) score += 8;
                if (game.Length >= 4 && content.Contains(game)) score += 4;
                if (name.Contains("openxr") || name.Contains("xr")) score += 2;
                if (content.Contains("openxr")) score += 2;
                if (content.Contains("error") || content.Contains("fail")) score += 1;
            }
            catch { }
            return score;
        }

        private static void Collect(string directory, int depth, DateTime oldest, List<FileInfo> found, ref int visited)
        {
            if (depth > 4 || ++visited > 300 || found.Count > 200) return;
            try
            {
                foreach (string path in Directory.GetFiles(directory))
                {
                    string name = Path.GetFileName(path).ToLowerInvariant();
                    if (!name.EndsWith(".log") && !name.EndsWith(".txt") && !name.Contains("log")) continue;
                    if (name.EndsWith(".dll") || name.EndsWith(".exe") || name.EndsWith(".json") || name.EndsWith(".png")) continue;
                    FileInfo file = new FileInfo(path);
                    if (file.Length > 0 && file.LastWriteTime >= oldest) found.Add(file);
                }
                foreach (string child in Directory.GetDirectories(directory)) Collect(child, depth + 1, oldest, found, ref visited);
            }
            catch { }
        }

        private static readonly Regex Problem = new Regex("error|fail|unsupport|not support|refus|denied|invalid|mismatch|exception|swap ?chain|cannot|can't|unable", RegexOptions.IgnoreCase);

        // The last 256 KB of a file as text.
        private static string ReadEnd(string path)
        {
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                long start = Math.Max(0, stream.Length - 256 * 1024);
                stream.Seek(start, SeekOrigin.Begin);
                byte[] bytes = new byte[stream.Length - start];
                int read = 0, got;
                while (read < bytes.Length && (got = stream.Read(bytes, read, bytes.Length - read)) > 0) read += got;
                // UTF-16 logs have a zero in every other byte.
                int zeros = 0;
                for (int i = 1; i < Math.Min(read, 400); i += 2) if (bytes[i] == 0) ++zeros;
                return zeros > 100 ? Encoding.Unicode.GetString(bytes, 0, read) : Encoding.UTF8.GetString(bytes, 0, read);
            }
        }

        // The end of one log file. Runs of lines that differ only in their numbers (the same
        // error once per frame) are shown once with a count, so the lines that explain something
        // are not pushed out by thousands of repeats.
        internal static string Tail(string path)
        {
            StringBuilder text = new StringBuilder();
            try
            {
                string content = ReadEnd(path);
                string[] lines = content.Replace("\r", "").Split('\n');
                List<string> kept = new List<string>();
                Dictionary<string, int> counts = new Dictionary<string, int>();
                Dictionary<string, int> position = new Dictionary<string, int>();
                for (int i = Math.Max(0, lines.Length - 4000); i < lines.Length; i++)
                {
                    string line = lines[i].TrimEnd();
                    if (line.Length == 0) continue;
                    if (line.Length > 320) line = line.Substring(0, 320) + "...";
                    string shape = Regex.Replace(line, "[0-9]+", "#");
                    int count;
                    if (counts.TryGetValue(shape, out count)) { counts[shape] = count + 1; continue; }
                    counts[shape] = 1; position[shape] = kept.Count; kept.Add(line);
                }
                Dictionary<int, int> repeats = new Dictionary<int, int>();
                foreach (KeyValuePair<string, int> pair in counts) if (pair.Value > 1) repeats[position[pair.Key]] = pair.Value;
                int first = Math.Max(0, kept.Count - 70);
                // Of the lines before those, the ones that read like a problem are kept too (the last 25 of them).
                List<int> earlier = new List<int>();
                for (int i = 0; i < first; i++) if (Problem.IsMatch(kept[i])) earlier.Add(i);
                if (earlier.Count > 25) earlier.RemoveRange(0, earlier.Count - 25);
                if (first > 0) text.Append($"\n        ... ({first} earlier different line(s) left out{(earlier.Count > 0 ? ", except these that read like a problem" : "")})");
                foreach (int i in earlier)
                {
                    int count;
                    text.Append("\n        " + kept[i] + (repeats.TryGetValue(i, out count) ? $"   (x{count}, numbers aside)" : ""));
                }
                if (earlier.Count > 0) text.Append("\n        ... (the end of the file follows)");
                for (int i = first; i < kept.Count; i++)
                {
                    int count;
                    text.Append("\n        " + kept[i] + (repeats.TryGetValue(i, out count) ? $"   (x{count}, numbers aside)" : ""));
                }
                if (kept.Count == 0) text.Append("\n        (empty)");
            }
            catch (Exception e)
            {
                text.Append("\n        (could not be read: " + e.GetType().Name + ": " + e.Message + ")");
            }
            return text.ToString();
        }
    }
}
