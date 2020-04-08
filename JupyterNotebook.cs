using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using OsLib;
using System.Linq;
using System.Threading.Tasks;
using System;
using System.Diagnostics;
using RunProcessAsTask; // https://github.com/jamesmanning/RunProcessAsTask
using System.IO;
using ZmodFiles;

// TODO move this source file into sln OsLib.Test
// TODO replace nyokacli by umoya.ai

/// <summary>
/// see bottom of file for developer notes
/// </summary>
namespace JNB
{
	public class NotebookInfo
	{
		//public string Key() => $"{this.name}.{this.pid}";
		public NotebookInfo(dynamic obj)
		: this((JObject)JObject.FromObject(obj))
		{
		}
		public NotebookInfo(JObject jo)
		{
			Name = (string)jo["name"];
			BaseUrl = (string)jo["base_url"];
			Hostname = jo.ContainsKey("hostname") ? (string)jo["hostname"] : Hostname;
			NotebookDir = jo.ContainsKey("notebook_dir") ? (string)jo["notebook_dir"] : NotebookDir;
			Password = jo.ContainsKey("password") ? (bool)jo["password"] : Password;
			Pid = jo.ContainsKey("pid") ? (int)jo["pid"] : Pid;
			Port = jo.ContainsKey("port") ? (int)jo["port"] : Port;
			Secure = jo.ContainsKey("secure") ? (bool)jo["secure"] : Secure;
			Token = jo.ContainsKey("token") ? (string)jo["token"] : Token;
			Url = jo.ContainsKey("url") ? (string)jo["url"] : Url;
			Size = 0;
			LastModified = DateTime.MinValue;
			Uploaded = DateTime.MinValue;
			Running = false;
		}
		public NotebookInfo(string name, string base_url = "/", string hostname = "localhost", string notebook_dir = null,
					 bool password = false, int pid = 0, int port = 8888, bool secure = false, string token = null,
				  string url = "http://localhost:8888/")
		{
			Name = name;
			Hostname = hostname;
			BaseUrl = base_url;
			if (string.IsNullOrEmpty(notebook_dir))
			{
				var x = new RaiFile(Name);
				x.Path = x.Name;
				notebook_dir = x.FullName;
			}
			NotebookDir = notebook_dir;
			Password = password;
			Pid = pid;
			Port = port;
			Secure = secure;
			Token = token;
			Url = url;
			Size = 0;
			LastModified = DateTime.MinValue;
			Uploaded = DateTime.MinValue;
			Running = false;
		}
		/// <summary>
		/// Convenience constructor from IpynbFileInfo
		/// </summary>
		/// <param name="info">info with updated information from the OS, i.e.static from a ZmodDirectory instance</param>
		public NotebookInfo(IpynbFileInfo info)
		{
			Name = info.fileInfo.Name;
			Hostname = "localhost";
			BaseUrl = "/";
			NotebookDir = info.fileInfo.DirectoryName;
			Password = false;
			Pid = 0;       // initial value; will be updated by refresh once started
			Port = 8888;   // initial value; will be updated by refresh once started
			Secure = false;
			Token = null;  // initial value; will be updated by refresh once started
			Url = "http://localhost:8888/";  // initial value; setting the Port, changes it
			Size = 0;
			LastModified = info.fileInfo.LastWriteTimeUtc;  // TODO: check which is right here
			Uploaded = info.fileInfo.CreationTimeUtc;       // TODO: check which is right here
			Running = false; // will be set by Start()
		}
		public string Name { get; set; }
		public string Hostname { get; set; }
		public string BaseUrl { get; set; }
		public string NotebookDir { get; set; }
		public bool Password { get; set; }
		public int Pid { get; set; }
		public Process Process { get; set; }
		public int Port
		{
			get
			{
				return port;
			}
			set
			{
				var portOld = port;
				port = Math.Max(8888, Math.Min(value, 8890));
				if (!string.IsNullOrEmpty(Url))
					Url = Url.Replace($":{portOld.ToString()}", $":{port.ToString()}");
			}
		}
		private int port;
		public bool Secure { get; set; }
		public string Token { get; set; }
		public string Url { get; set; }
		public long Size { get; set; }
		public DateTimeOffset LastModified { get; set; }
		public DateTimeOffset Uploaded { get; set; }
		public bool Running { get; set; }
	}
	/// <summary>
	/// For JupyterNotebooks, we use the following convention:
	/// each .ipynb file is in a directory by the same name without extension
	/// </summary>
	/// <typeparam name="string">key</typeparam>
	/// <typeparam name="NotebookInfo">info</typeparam>
	public class JupyterNotebook : IEnumerable<KeyValuePair<string, NotebookInfo>>
	{
		private static ConcurrentDictionary<string, NotebookInfo> notebooks =
			 new ConcurrentDictionary<string, NotebookInfo>();
		public NotebookInfo this[string key]
		{
			get
			{
				return notebooks.ContainsKey(key) ? notebooks[key] : null;
			}
			set
			{
				if (string.IsNullOrEmpty(value.NotebookDir))
					throw new Exception("NotebookDir empty");
				notebooks[key] = value;
			}
		}
		public IEnumerator<KeyValuePair<string, NotebookInfo>> GetEnumerator() => notebooks.GetEnumerator();
		IEnumerator IEnumerable.GetEnumerator() => notebooks.GetEnumerator();
		protected static string NotebookCmd { get; set; } = "jupyter-notebook";
		protected static string NotebookParam { get; set; } = "--no-browser --notebook-dir {NotebookDir} {NotebookDir}{Name} &> /dev/null &";
		protected static string ListCmd { get; set; } = "jupyter-notebook list --json";
		protected static string KillCmd { get; set; } = "kill -9 {Pid}";
		protected static string StopCmd { get; set; } = "jupyter-notebook stop {Port} &> /dev/null &";
		public static string NotebookRootDir { get; set; } = $"~/ZMOD/Code/";
		public static List<int> PortRange = Enumerable.Range(8888, 8890).ToList();    // not used for JNB
		public string Name { get; set; }
		public NotebookInfo Info
		{
			get { return notebooks[Name]; }
			set { notebooks[Name] = value; }
		}
		public static NotebookInfo Status(int port)
		{
			var q = from _ in notebooks
					  where _.Value.Port == port && _.Value.Running == true
					  select _.Value;
			return q.Count() == 0 ? null : q.First();
		}
		public static bool NextPortAvailable(out int port)
		{
			port = 0;
			foreach (var portNumber in PortRange)
			{
				if (Status(portNumber) == null)
				{
					port = portNumber;
					return true;
				}
			}
			return false;
		}
		/// <summary>
		/// return a link to a running notebook
		/// </summary>
		/// <param name="server"></param>
		/// <returns>link or null</returns>
		public string Link(string server)   // localhost?
		{
			if (!notebooks.ContainsKey(Name))
				return null;
			if (server == "localhost")
				return $"{notebooks[Name].Url}{Name}?token={notebooks[Name].Token}"
						  //.Replace("http://", "https://")
						  .Replace("localhost", server)
						  .Replace("127.0.0.1", server)
						  .Replace("127.0.0.2", server)
						  .Replace(":8888", "/jnb/1")
						  .Replace(":8889", "/jnb/2")
						  .Replace(":8890", "/jnb/3");
			return $"{notebooks[Name].Url}{Name}?token={notebooks[Name].Token}"
					  .Replace("http://", "https://")
					  .Replace("localhost", server)
					  .Replace("127.0.0.1", server)
					  .Replace("127.0.0.2", server)
					  .Replace(":8888", "/jnb/1")
					  .Replace(":8889", "/jnb/2")
					  .Replace(":8890", "/jnb/3");
		}
		/// <summary>
		/// removes the internal dictionary and rebuilds it from the OS / Jupyter Notebook
		/// </summary>
		public static void Refresh()
		{
			var sys = new RaiSystem(ListCmd);
			string result = "";
			int rc = sys.Exec(out result);
			if (rc != 0)
				throw new Exception("system call failed in JupyterNotebook:Refresh");
			#region fix flaws => array of objects
			var objects = result.Replace("}\n{", "},{").Replace("\n", "");
			var json = $"[{objects}]";
			#endregion
			JArray array = JArray.Parse(json);
			//notebooks.Clear();
			#region set notebook status including running flag
			NotebookInfo info;
			foreach (var nb in notebooks)
			{
				info = notebooks[nb.Key];
				info.Running = false;
				info.Port = 0;
				info.Pid = 0;
				info.Token = "";
				notebooks[nb.Key] = info;
			}
			foreach (var pInfo in array)
			{
				var psInfo = new NotebookInfo((JObject)pInfo);
				var name = $"{new RaiFile((string)psInfo.NotebookDir).Name}.ipynb";
				info = psInfo;
				info.Name = name;
				info.Running = true;
				info.Pid = psInfo.Pid;
				info.Port = psInfo.Port;
				info.NotebookDir = psInfo.NotebookDir;
				var f = new RaiFile(info.Name);
				f.Path = info.NotebookDir;
				var fInfo = new FileInfo(f.FullName);
				info.Size = fInfo.Length;
				info.Uploaded = fInfo.CreationTimeUtc;
				info.LastModified = fInfo.LastWriteTimeUtc;
				notebooks[info.Name] = info;
			}
			#endregion
		}
		public async Task<ProcessResults> Start()
		{
			var nbDir = new RaiFile("");
			nbDir.Path = new RaiFile(notebooks[Name].NotebookDir).FullName;
			var sys = new RaiSystem(
				 NotebookCmd,
				 NotebookParam
				 .Replace("{NotebookDir}", nbDir.Path)  // 2 replacements, ends with a /
				 .Replace("{Name}", Name));
			var results = await sys.Start();
			return results;
		}
		public void Stop_old()
		{
			#region not running
			NotebookInfo info = null;
			string sOut = "";
			int rc;
			if (!IsRunning())
				return;
			if (notebooks[Name].Pid == 0)
				throw new InvalidOperationException("precondition violated: Pid has invalid value");
			#endregion
			#region kill it as long as it is still running
			for (int i = 0; i < 20 && IsRunning(); i++)
			{
				var sys = new RaiSystem(KillCmd.Replace("{Pid}", this[Name].Pid.ToString()));
				info = this[Name];
				rc = sys.Exec(out sOut);
				if (sOut.Contains("No such process"))
				{
					info.Running = false;
					info.Port = 0;
					info.Pid = 0;
					info.Token = "";
					this[Name] = info;  // should fix IsRunning()
				}
				if (rc != 0)
					Task.Delay(100).Wait();
			}
			#endregion
			#region update dictionary 
			for (int i = 0; i < 10 && !notebooks.TryRemove(Name, out info); i++)
				Task.Delay(100).Wait();
			#endregion
		}
		public void Stop()
		{
			if (notebooks[Name].Port < 8888)
				throw new InvalidOperationException("precondition violated: Port has invalid value");
			#region stop it - it takes a while but it should always work
			var sys = new RaiSystem(StopCmd.Replace("{Port}", this[Name].Port.ToString()));
			string sOut = "";
			int rc = sys.Exec(out sOut);
			#endregion
			#region check result
			NotebookInfo info = this[Name];
			if (sOut.Contains("No such process"))
			{
				info.Running = false;
				info.Port = 0;
				info.Pid = 0;
				info.Token = "";
				this[Name] = info;  // should fix IsRunning()
			}
			if (rc != 0)
				Task.Delay(100).Wait();
			#endregion
			#region update dictionary 
			for (int i = 0; i < 10 && !notebooks.TryRemove(Name, out info); i++)
				Task.Delay(100).Wait();
			#endregion
		}
		public static void StopAll()
		{
			var q = from _ in JupyterNotebook.List(true)
					  where _.Info.Running
					  select _;
			foreach (var nb in q.ToList())
			{
				nb.Stop();
				Task.Delay(100).Wait(); // should be fast - insert a short delay anyway (to allow for graceful shutdown)
			}
		}
		public bool IsRunning()
		{
			if (notebooks.ContainsKey(Name))
				return notebooks[Name].Running;
			Refresh();
			return notebooks.ContainsKey(Name) ? notebooks[Name].Running : false;
		}
		/// <summary>
		/// Finds HelloClass and HelloClass.ipynb; might find more than one
		/// </summary>
		/// <param name="search">i.e. "HelloClass.ipynb"</param>
		/// <param name="refresh"></param>
		/// <returns>the first match found</returns>
		public static JupyterNotebook Find(string search, bool refresh = true)
		{
			var name = new RaiFile(search).Name;
			if (refresh)
				Refresh();
			var q = from _ in notebooks
					  where _.Key.Contains(name) || _.Value.Name.Contains(name) || _.Value.NotebookDir.Contains(name)
					  select _.Value;
			return q.Count() > 0 ? new JupyterNotebook((NotebookInfo)q.First()) : null;
		}
		public static List<JupyterNotebook> List(bool refresh = true)
		{
			if (refresh)
				Refresh();
			var q = from _ in notebooks select new JupyterNotebook(_.Value);
			return q.ToList();
		}
		/// <summary>
		/// Read ipynb files from a directory
		/// </summary>
		/// <param name="codeDir">can contain ~, i.e. ~/zmodRoot/Code/</param>
		/// <note>
		/// by convention, we agree that ipynb files need to be in their own directory because
		/// a) the Jupyter Notebook Server stores checkpoints in this directory
		/// b) the running notebook server instance is identefied by the directory, not by the file
		/// </note>
		public static void InitFromDirectory(string codeDir = null)
		{
			if (codeDir != null)
				NotebookRootDir = codeDir;
			StopAll();
			var CodeDir = new RaiFile(NotebookRootDir);
			var files = Directory.GetFiles(CodeDir.FullName, "*.ipynb", SearchOption.AllDirectories);
			foreach (var file in files)
			{
				if (!file.Contains("checkpoint"))
				{
					var f = new RaiFile(file);
					var obj = new
					{
						name = f.NameWithExtension,
						notebook_dir = f.Path
					};
					new JupyterNotebook(new NotebookInfo(obj)); // inserts a notebook into the static Dictionary notebooks
				}
			}
		}
		public JupyterNotebook(NotebookInfo info)
		{
			// http://localhost:8890/?token=0da788a158d220e91c783e1014b0f7ba21c829ba07974692
			//info.Name = new RaiFile(info.Name).Name;    // remove extension ipynb
			this[info.Name] = info;
			Name = info.Name;
		}
		public JupyterNotebook(JObject jo)
		{
			var info = new NotebookInfo(jo);
			info.Name = new RaiFile(info.Name).Name;    // remove extension ipynb
			this[info.Name] = info;
			Name = info.Name;
		}
	}
}