using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.Caching;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using DokanNet;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;
using FileAccess = DokanNet.FileAccess;
using System.Runtime.InteropServices;
using System.Security.Authentication;
using System.Text.Json;
using System.Threading.Tasks;

namespace Sshfs
{
	internal sealed class SftpFilesystem : SftpClient, IDokanOperations
	{
		/// <summary>
		/// Sets the last-error code for the calling thread.
		/// </summary>
		/// <param name="dwErrorCode">The last-error code for the thread.</param>
		[DllImport("kernel32.dll", SetLastError = true)]
		static extern void SetLastError(uint dwErrorCode);

		#region Constants

		#endregion

		#region Fields

		private readonly MemoryCache _cache = MemoryCache.Default;

		private SshClient _sshClient;
		private string _rootpath;

		private readonly bool _useOfflineAttribute;
		private readonly bool _debugMode;

		private int _userId;
		private string _idCommand = "id";
		private string _dfCommand = "df";
		private HashSet<int> _userGroups;

		private readonly int _attributeCacheTimeout;
		private readonly int _directoryCacheTimeout;

		private readonly string _volumeLabel;

		private HttpClient httpClient;
		private readonly ConcurrentDictionary<string, long> lockedFiles = new ConcurrentDictionary<string, long>();

		#endregion

		#region Constructors

		public SftpFilesystem(ConnectionInfo connectionInfo, string rootpath, string label = null,
			bool useOfflineAttribute = false,
			bool debugMode = false, int attributeCacheTimeout = 5, int directoryCacheTimeout = 60)
			: base(connectionInfo)
		{
			_rootpath = rootpath;
			_directoryCacheTimeout = directoryCacheTimeout;
			_attributeCacheTimeout = attributeCacheTimeout;
			_useOfflineAttribute = useOfflineAttribute;
			_debugMode = debugMode;
			_volumeLabel = label ?? $"{ConnectionInfo.Username} on '{ConnectionInfo.Host}'";
			BufferSize = 1048576; // 1MB
		}

		#endregion

		#region Method overrides

		protected override void OnConnected()
		{
			try
			{
				OnConnectedPrivate();
			}
			catch(Exception)
			{
			}
		}

		private void OnConnectedPrivate()
		{
			base.OnConnected();

			_sshClient = new SshClient(ConnectionInfo);
			var httpMessageHandler = new HttpClientHandler
			{
				SslProtocols = SslProtocols.Tls12
			};
			httpClient = new HttpClient(httpMessageHandler);
			this.Log("Connected %s", _volumeLabel);
			_sshClient.Connect();

			CheckAndroid();

			_userId = GetUserId();
			if(_userId != -1)
				_userGroups = new HashSet<int>(GetUserGroupsIds());


			if(string.IsNullOrWhiteSpace(_rootpath))
			{
				_rootpath = this.WorkingDirectory;
			}
		}

		protected override void OnDisconnected()
		{
			try
			{
				OnDisconnectedPrivate();
			}
			catch(Exception)
			{
			}
		}

		private void OnDisconnectedPrivate()
		{
			base.OnDisconnected();
			this.Log("disconnected %s", _volumeLabel);
		}

		protected override void Dispose(bool disposing)
		{
			try
			{
				DisposePrivate(disposing);
			}
			catch(Exception)
			{
			}
		}

		private void DisposePrivate(bool disposing)
		{
			if(_sshClient != null)
			{
				_sshClient.Dispose();
				_sshClient = null;
			}

			base.Dispose(disposing);
		}

		#endregion

		#region Logging

		[Conditional("DEBUG")]
		private void Log(string format, params object[] arg)
		{
			if(_debugMode)
			{
				Console.WriteLine(format, arg);
			}

			Debug.AutoFlush = false;
			Debug.Write(DateTime.Now.ToLongTimeString() + " ");
			Debug.WriteLine(format, arg);
			Debug.Flush();
		}

		[Conditional("DEBUG")]
		private void LogFSAction(string action, string path, SftpContext context, string format, params object[] arg)
		{
			Debug.Write(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "\t" +
						(context == null ? "[-------]" : context.ToString()) + "\t" + action + "\t" + _volumeLabel +
						"\t" + path + "\t");
			Debug.WriteLine(format, arg);
		}

		[Conditional("DEBUG")]
		private void LogFSActionInit(string action, string path, SftpContext context, string format,
			params object[] arg)
		{
			LogFSAction(action + "^", path, context, format, arg);
		}

		[Conditional("DEBUG")]
		private void LogFSActionSuccess(string action, string path, SftpContext context, string format,
			params object[] arg)
		{
			LogFSAction(action + "$", path, context, format, arg);
		}
		[Conditional("DEBUG")]
		private void LogFSActionError(string action, string path, SftpContext context, string format,
			params object[] arg)
		{
			LogFSAction(action + "!", path, context, format, arg);
		}

		[Conditional("DEBUG")]
		private void LogFSActionOther(string action, string path, SftpContext context, string format,
			params object[] arg)
		{
			LogFSAction(action + "|", path, context, format, arg);
		}

		#endregion

		#region Cache

		private void CacheAddAttr(string path, SftpFileAttributes attributes, DateTimeOffset expiration)
		{
			LogFSActionSuccess("CacheSetAttr", path, null, "Expir:{1} Size:{0}", attributes.Size, expiration);
			_cache.Add(_volumeLabel + "A:" + path, attributes, expiration);
		}

		private void CacheAddDir(string path, Tuple<DateTime, IList<FileInformation>> dir, DateTimeOffset expiration)
		{
			LogFSActionSuccess("CacheSetDir", path, null, "Expir:{1} Count:{0}", dir.Item2.Count, expiration);
			_cache.Add(_volumeLabel + "D:" + path, dir, expiration);
		}

		private void CacheAddDiskInfo(Tuple<long, long, long> info, DateTimeOffset expiration)
		{
			LogFSActionSuccess("CacheSetDInfo", _volumeLabel, null, "Expir:{0}", expiration);
			_cache.Add(_volumeLabel + "I:", info, expiration);
		}

		private SftpFileAttributes CacheGetAttr(string path)
		{
			SftpFileAttributes attributes = _cache.Get(_volumeLabel + "A:" + path) as SftpFileAttributes;
			LogFSActionSuccess("CacheGetAttr", path, null, "Size:{0} Group write:{1} ",
				(attributes == null) ? "miss" : attributes.Size.ToString(),
				attributes == null ? "miss" : attributes.GroupCanWrite.ToString());
			return attributes;
		}

		private Tuple<DateTime, IList<FileInformation>> CacheGetDir(string path)
		{
			Tuple<DateTime, IList<FileInformation>> dir =
				_cache.Get(_volumeLabel + "D:" + path) as Tuple<DateTime, IList<FileInformation>>;
			LogFSActionSuccess("CacheGetDir", path, null, "Count:{0}",
				(dir == null) ? "miss" : dir.Item2.Count.ToString());
			return dir;
		}

		private Tuple<long, long, long> CacheGetDiskInfo()
		{
			Tuple<long, long, long> info = _cache.Get(_volumeLabel + "I:") as Tuple<long, long, long>;
			LogFSActionSuccess("CacheGetDInfo", _volumeLabel, null, "");
			return info;
		}

		private void CacheReset(string path)
		{
			LogFSActionSuccess("CacheReset", path, null, "");
			_cache.Remove(_volumeLabel + "A:" + path);
			_cache.Remove(_volumeLabel + "D:" + path);
		}

		private void CacheResetParent(string path)
		{
			int index = path.LastIndexOf('/');
			this.CacheReset(index > 0 ? path.Substring(0, index) : "/");
		}

		#endregion

		#region Methods

		private string GetUnixPath(string path)
		{
			return $"{_rootpath}{path.Replace('\\', '/').Replace("//", "/")}";
		}

		private void CheckAndroid()
		{
			using(var cmd = _sshClient.CreateCommand("test -f /system/build.prop", Encoding.UTF8))
			{
				cmd.Execute();
				if(cmd.ExitStatus != 0)
					return;
				_idCommand = "busybox id";
				_dfCommand = "busybox df";
			}
		}

		private IEnumerable<int> GetUserGroupsIds()
		{
			using(var cmd = _sshClient.CreateCommand(_idCommand + " -G ", Encoding.UTF8))
			{
				cmd.Execute();
				return cmd.Result.Split(new[] {' '}, StringSplitOptions.RemoveEmptyEntries).Select(int.Parse);
			}
		}

		private int GetUserId()
		{
			// These commands seems to be POSIX so the only problem would be Windows environment
			using(var cmd = _sshClient.CreateCommand(_idCommand + " -u ", Encoding.UTF8))
			{
				cmd.Execute();
				return cmd.ExitStatus == 0 ? int.Parse(cmd.Result) : -1;
			}
		}

		private bool UserCanRead(SftpFileAttributes attributes)
		{
			return _userId <= 0 || (attributes.OwnerCanRead && attributes.UserId == _userId ||
									(attributes.GroupCanRead && _userGroups.Contains(attributes.GroupId) ||
									 attributes.OthersCanRead));
		}

		private bool UserCanWrite(SftpFileAttributes attributes)
		{
			return _userId <= 0 || (attributes.OwnerCanWrite && attributes.UserId == _userId ||
									(attributes.GroupCanWrite && _userGroups.Contains(attributes.GroupId) ||
									 attributes.OthersCanWrite));
		}

		private bool UserCanExecute(SftpFileAttributes attributes)
		{
			return _userId <= 0 || (attributes.OwnerCanExecute && attributes.UserId == _userId ||
									(attributes.GroupCanExecute && _userGroups.Contains(attributes.GroupId) ||
									 attributes.OthersCanExecute));
		}

		private bool GroupRightsSameAsOwner(SftpFileAttributes attributes)
		{
			return (attributes.GroupCanWrite == attributes.OwnerCanWrite)
				   && (attributes.GroupCanRead == attributes.OwnerCanRead)
				   && (attributes.GroupCanExecute == attributes.OwnerCanExecute);
		}

		public override SftpFileAttributes GetAttributes(string path)
		{
			SftpFileAttributes attributes = base.GetAttributes(path);
			this.ExtendSFtpFileAttributes(path, attributes);
			return attributes;
		}

		private SftpFileAttributes ExtendSFtpFileAttributes(string path, SftpFileAttributes attributes)
		{
			if(!attributes.IsSymbolicLink)
				return attributes;
			SftpFile symTarget;
			try
			{
				symTarget = this.GetSymbolicLinkTarget(path);
			}
			catch(SftpPathNotFoundException)
			{
				//invalid symlink
				attributes.SymbolicLinkTarget = null;
				return attributes;
			}

			attributes.IsSymbolicLinkToDirectory = symTarget.Attributes.IsDirectory;
			attributes.SymbolicLinkTarget = symTarget.FullName;
			if(!attributes.IsSymbolicLinkToDirectory)
				attributes.Size = symTarget.Attributes.Size;

			return attributes;
		}

		#endregion

		#region DokanOperations

		NtStatus IDokanOperations.CreateFile(string fileName, FileAccess access, FileShare share,
			FileMode mode, FileOptions options,
			FileAttributes attributes, IDokanFileInfo info)
		{
			try
			{
				return CreateFilePrivate(fileName, access, mode, options, info);
			}
			catch(Exception)
			{
				return NtStatus.Success;
			}
		}

		private NtStatus CreateFilePrivate(string fileName, FileAccess access, FileMode mode, FileOptions options, IDokanFileInfo info)
		{
			//Split into four methods?
#if DEBUG
			//info.ProcessId
			string processName = Process.GetProcessById(info.ProcessId).ProcessName;
			LogFSActionInit("CreateFile", fileName, (SftpContext)info.Context,
				"ProcessName:{0} Mode:{1} Options:{2} IsDirectory:{3}", processName, mode, options, info.IsDirectory);
#endif

			if(fileName.Contains("symlinkfile"))
			{
			}

			if(info.IsDirectory)
			{
				SftpFileAttributes attributesDir = null;
				try
				{
					attributesDir = this.GetAttributes(this.GetUnixPath(fileName)); //todo load from cache first
				}
				catch(SftpPathNotFoundException)
				{
				}

				if(attributesDir == null || attributesDir.IsDirectory || attributesDir.IsSymbolicLinkToDirectory)
				{
					if(mode == FileMode.Open)
					{
						NtStatus status = OpenDirectory(fileName, info);

						try
						{
							if(status == NtStatus.ObjectNameNotFound)
							{
								GetAttributes(fileName);
								//no expception -> its file
								return NtStatus.NotADirectory;
							}
						}
						catch(SftpPathNotFoundException)
						{
						}

						return status;
					}

					return mode == FileMode.CreateNew ? CreateDirectory(fileName, info) : NtStatus.NotImplemented;
				}
				else
				{
					//its symbolic link behaving like directory?
					return NtStatus.NotImplemented;
				}
			}

			if(fileName.EndsWith("desktop.ini", StringComparison.OrdinalIgnoreCase) ||
			   fileName.EndsWith("autorun.inf", StringComparison.OrdinalIgnoreCase))
				return NtStatus.NoSuchFile;

			LogFSActionInit("OpenFile", fileName, (SftpContext)info.Context, "Mode:{0} Options:{1}", mode, options);

			string path = GetUnixPath(fileName);
			var sftpFileAttributes = this.CacheGetAttr(path);

			if(sftpFileAttributes == null)
			{
				//Log("cache miss");
				try
				{
					sftpFileAttributes = GetAttributes(path);
				}
				catch(SftpPathNotFoundException)
				{
					Debug.WriteLine("File not found");
					sftpFileAttributes = null;
				}

				if(sftpFileAttributes != null)
					CacheAddAttr(path, sftpFileAttributes, DateTimeOffset.UtcNow.AddSeconds(_attributeCacheTimeout));
				else
					LogFSActionOther("OpenFile", fileName, (SftpContext)info.Context, "get attributes failed");
			}

			switch(mode)
			{
				case FileMode.Open:
					if(sftpFileAttributes != null)
					{
						if(((uint)access & 0xe0000027) == 0 || sftpFileAttributes.IsDirectory)
						//check if only wants to read attributes,security info or open directory
						{
							info.IsDirectory = sftpFileAttributes.IsDirectory ||
											   sftpFileAttributes.IsSymbolicLinkToDirectory;

							if(options.HasFlag(FileOptions.DeleteOnClose))
								return NtStatus.Error; //this will result in calling DeleteFile in Windows Explorer

							info.Context = new SftpContext(sftpFileAttributes, false);
							LogFSActionOther("OpenFile", fileName, (SftpContext)info.Context, "Dir open or get attrs");
							return NtStatus.Success;
						}
					}
					else
					{
						LogFSActionError("OpenFile", fileName, (SftpContext)info.Context, "File not found");
						return NtStatus.NoSuchFile;
					}

					break;
				case FileMode.CreateNew:
					if(sftpFileAttributes != null)
						return NtStatus.ObjectNameCollision;

					CacheResetParent(path);
					break;
				case FileMode.Truncate:
					if(sftpFileAttributes == null)
						return NtStatus.NoSuchFile;
					CacheResetParent(path);
					//_cache.Remove(path);
					this.CacheReset(path);
					break;
				default:
					CacheResetParent(path);
					break;
			}

			try
			{
				info.Context = new SftpContext(this, path, mode,
					((ulong)access & 0x40010006) == 0
						? System.IO.FileAccess.Read
						: System.IO.FileAccess.ReadWrite, sftpFileAttributes);
				if(sftpFileAttributes != null)
					SetLastError(183); //ERROR_ALREADY_EXISTS
			}
			catch(SshException) // Don't have access rights or try to read broken symlink
			{
				var ownerpath = path.Substring(0, path.LastIndexOf('/'));
				var sftpPathAttributes = CacheGetAttr(ownerpath);

				if(sftpPathAttributes == null)
				{
					//Log("cache miss");
					try
					{
						sftpFileAttributes = GetAttributes(ownerpath);
					}
					catch(SftpPathNotFoundException)
					{
						Debug.WriteLine("File not found");
						sftpFileAttributes = null;
					}

					if(sftpPathAttributes != null)
						CacheAddAttr(path, sftpFileAttributes,
							DateTimeOffset.UtcNow.AddSeconds(_attributeCacheTimeout));
					else
					{
						//Log("Up directory must be created");
						LogFSActionError("OpenFile", fileName, (SftpContext)info.Context, "Up directory mising:{0}",
							ownerpath);
						return NtStatus.ObjectPathNotFound;
					}
				}

				LogFSActionError("OpenFile", fileName, (SftpContext)info.Context, "Access denied");
				return NtStatus.AccessDenied;
			}

			LogFSActionSuccess("OpenFile", fileName, (SftpContext)info.Context, "Mode:{0}", mode);
			return NtStatus.Success;
		}

		private NtStatus OpenDirectory(string fileName, IDokanFileInfo info)
		{
#if DEBUG
			string processName = Process.GetProcessById(info.ProcessId).ProcessName;
			LogFSActionInit("OpenDir", fileName, (SftpContext) info.Context, "ProcessName:{0}", processName);
#endif
			string path = GetUnixPath(fileName);
			var sftpFileAttributes = CacheGetAttr(path);

			if(sftpFileAttributes == null)
			{
				//Log("cache miss");
				try
				{
					sftpFileAttributes = GetAttributes(path);
				}
				catch(SftpPathNotFoundException)
				{
					Debug.WriteLine("Dir not found");
					sftpFileAttributes = null;
				}

				if(sftpFileAttributes != null)
					CacheAddAttr(path, sftpFileAttributes, DateTimeOffset.UtcNow.AddSeconds(_attributeCacheTimeout));
			}

			if(sftpFileAttributes != null)
			{
				if(!sftpFileAttributes.IsDirectory && !sftpFileAttributes.IsSymbolicLinkToDirectory)
					return NtStatus.NotADirectory;

				if(!UserCanExecute(sftpFileAttributes) || !UserCanRead(sftpFileAttributes))
					return NtStatus.AccessDenied;

				info.IsDirectory = true;
				info.Context = new SftpContext(sftpFileAttributes);

				var dircache = CacheGetDir(path);
				if(dircache != null && dircache.Item1 != sftpFileAttributes.LastWriteTime)
					CacheReset(path);

				LogFSActionSuccess("OpenDir", fileName, (SftpContext) info.Context, "");
				return NtStatus.Success;
			}

			LogFSActionError("OpenDir", fileName, (SftpContext) info.Context, "Path not found");
			return NtStatus.ObjectNameNotFound;
		}

		private NtStatus CreateDirectory(string fileName, IDokanFileInfo info)
		{
			LogFSActionInit("OpenDir", fileName, (SftpContext) info.Context, "");

			string path = GetUnixPath(fileName);
			try
			{
				CreateDirectory(path);
				CacheResetParent(path);
			}
			catch(SftpPermissionDeniedException)
			{
				LogFSActionError("OpenDir", fileName, (SftpContext) info.Context, "Access denied");
				return NtStatus.AccessDenied;
			}
			catch(SshException) // operation should fail with generic error if file already exists
			{
				LogFSActionError("OpenDir", fileName, (SftpContext) info.Context, "Already exists");
				return NtStatus.ObjectNameCollision;
			}

			LogFSActionSuccess("OpenDir", fileName, (SftpContext) info.Context, "");
			return NtStatus.Success;
		}

		void IDokanOperations.Cleanup(string fileName, IDokanFileInfo info)
		{
			try
			{
				CleanupPrivate(fileName, info);
			}
			catch(Exception)
			{
			}
		}

		private void CleanupPrivate(string fileName, IDokanFileInfo info)
		{
			LogFSActionInit("Cleanup", fileName, (SftpContext)info.Context, "");

			bool deleteOnCloseWorkAround = false; //TODO not used probably, can be removed

			if(info.Context != null)
			{
				deleteOnCloseWorkAround = ((SftpContext)info.Context).deleteOnCloseWorkaround;

				(info.Context as SftpContext).Release();

				info.Context = null;
			}

			if(info.DeleteOnClose || deleteOnCloseWorkAround)
			{
				string path = GetUnixPath(fileName);
				if(info.IsDirectory) //can be also symlink file!
				{
					try
					{
						SftpFileAttributes attributes = this.CacheGetAttr(path) ?? this.GetAttributes(path);

						if(attributes == null)
						{
							//should never happen
							throw new SftpPathNotFoundException();
						}

						if(attributes.IsSymbolicLink) //symlink file or dir, can be both
							DeleteFile(path);
						else
							DeleteDirectory(path);
					}
					catch(Exception) //in case we are dealing with symbolic link
					{
						//This may cause an error
					}
				}
				else
				{
					try
					{
						DeleteFile(path);
					}
					catch(SftpPathNotFoundException)
					{
						//not existing file
					}
				}

				CacheReset(path);
				CacheResetParent(path);
			}

			LogFSActionSuccess("Cleanup", fileName, (SftpContext)info.Context, "");
		}

		void IDokanOperations.CloseFile(string fileName, IDokanFileInfo info)
		{
			try
			{
				CloseFile(fileName, info);
			}
			catch(Exception)
			{
			}
		}

		private void CloseFile(string fileName, IDokanFileInfo info)
		{
			LogFSActionInit("CloseFile", fileName, (SftpContext)info.Context, "");

			if(info.Context != null)
			{
				SftpContext context = (SftpContext)info.Context;
				if(context.Stream != null)
				{
					(info.Context as SftpContext).Stream.Flush();
					(info.Context as SftpContext).Stream.Dispose();
				}
			}
			else if(info.Context == null && lockedFiles.ContainsKey(fileName))
			{
				long fileSize;
				lockedFiles.TryRemove(fileName, out fileSize);
				UnlockFileForWriting(fileName.Substring(1));
			}
			/* cache reset for dir close is not good idea, will read it very soon again */
			if(!info.IsDirectory)
				CacheReset(GetUnixPath(fileName));
		}

		NtStatus IDokanOperations.ReadFile(string fileName, byte[] buffer, out int bytesRead, long offset,
			IDokanFileInfo info)
		{
			try
			{
				return ReadFilePrivate(fileName, buffer, out bytesRead, offset, info);
			}
			catch(Exception)
			{
				bytesRead = 0;
				return NtStatus.Success;
			}
		}

		private NtStatus ReadFilePrivate(string fileName, byte[] buffer, out int bytesRead, long offset, IDokanFileInfo info)
		{
			if(fileName.StartsWith(@"\Device\Volume"))
			{
				var v = fileName.Replace(@"\Device\Volume{", string.Empty);
				fileName = v.Substring(v.IndexOf("}") + 1);

			}
			LogFSActionInit("ReadFile", fileName, (SftpContext)info.Context, "BuffLen:{0} Offset:{1}", buffer.Length,
							offset);
			if(info.Context == null)
			{
				//called when file is read as memory memory mapeded file usualy notepad and stuff
				using(SftpFileStream handle = Open(GetUnixPath(fileName), FileMode.Open))
				{
					handle.Seek(offset, offset == 0 ? SeekOrigin.Begin : SeekOrigin.Current);
					bytesRead = handle.Read(buffer, 0, (int)Math.Min(buffer.Length, BufferSize));
				}
				LogFSActionOther("ReadFile", fileName, (SftpContext)info.Context,
					"NOCONTEXT BuffLen:{0} Offset:{1} Read:{2}", buffer.Length, offset, bytesRead);
			}
			else
			{
				SftpContextStream stream = (info.Context as SftpContext).Stream;
				lock(stream)
				{
					stream.Position = offset;
					bytesRead = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, BufferSize));

					LogFSActionOther("ReadFile", fileName, (SftpContext)info.Context,
						"BuffLen:{0} Offset:{1} Read:{2}", buffer.Length, offset, bytesRead);
				}
			}

			LogFSActionSuccess("ReadFile", fileName, (SftpContext)info.Context, "");
#if DEBUG && DEBUGSHADOWCOPY
            try {
                string shadowCopyDir = Environment.CurrentDirectory + "\\debug-shadow";
                string tmpFilePath = shadowCopyDir + "\\" + fileName.Replace("/", "\\");
                FileStream fs = File.OpenRead(tmpFilePath);
                byte[] localDataShadowBuffer = new byte[buffer.Length];
                fs.Seek(offset, SeekOrigin.Begin);
                fs.Close();
                int readedShadow = fs.Read(localDataShadowBuffer, 0, localDataShadowBuffer.Length);
                if (readedShadow != bytesRead)
                {
                    throw new Exception("Length of readed data from "+fileName+" differs");
                }
                if (!localDataShadowBuffer.SequenceEqual(buffer))
                {
                    throw new Exception("Data readed from " + fileName + " differs");
                }
            }
            catch (Exception)
            {

            }
#endif
			return NtStatus.Success;
		}

		NtStatus IDokanOperations.WriteFile(string fileName, byte[] buffer, out int bytesWritten, long offset,
			IDokanFileInfo info)
		{
			return WriteFilePrivate(fileName, buffer, out bytesWritten, offset, info);
		}

		private NtStatus WriteFilePrivate(string fileName, byte[] buffer, out int bytesWritten, long offset, IDokanFileInfo info)
		{
			bytesWritten = 0;
			LogFSActionInit("WriteFile", fileName, (SftpContext)info.Context, "Ofs:{0} Len:{1}", offset,
				buffer.Length);
			try
			{
#if DEBUG && DEBUGSHADOWCOPY
                string shadowCopyDir = Environment.CurrentDirectory + "\\debug-shadow";
                string tmpFilePath = shadowCopyDir + "\\" + fileName.Replace("/","\\");
                if (!Directory.Exists(tmpFilePath))
                {
                    Directory.CreateDirectory(Directory.GetParent(tmpFilePath).FullName);
                }
                FileStream tmpFile = File.OpenWrite(tmpFilePath);
                if (tmpFile.Length < offset + buffer.Length)
                {
                    tmpFile.SetLength(offset + buffer.Length);
                }
                tmpFile.Seek(offset, SeekOrigin.Begin);
                tmpFile.Write(buffer, 0, buffer.Length);
                tmpFile.Close();
#endif
				bool fileWriteFinished;
				if(info.Context == null) // who would guess
				{
					using(SftpFileStream handle = Open(GetUnixPath(fileName), FileMode.Create)) 
					{
						fileWriteFinished = FileWriteFinished(fileName, buffer.Length, offset);
						handle.Write(buffer, 0, (int)Math.Min(buffer.Length, BufferSize));
					}
					bytesWritten = (int)Math.Min(buffer.Length, BufferSize);
					LogFSActionOther("WriteFile", fileName, (SftpContext)info.Context,
						"NOCONTEXT Ofs:{1} Len:{0} Written:{2}", buffer.Length, offset, bytesWritten);
				}
				else
				{
					SftpContextStream stream = (info.Context as SftpContext).Stream;
					lock(stream)
					{
						stream.Position = offset;
						stream.Write(buffer, 0, (int)Math.Min(buffer.Length, BufferSize));
						fileWriteFinished = FileWriteFinished(fileName, (int)Math.Min(buffer.Length, BufferSize), offset);
					}
					stream.Flush();
					bytesWritten = (int)Math.Min(buffer.Length, BufferSize);
					// TODO there are still some apps that don't check disk free space before write
				}
				if(fileWriteFinished)
				{
					long fileSize;
					lockedFiles.TryRemove(fileName, out fileSize);
					UnlockFileForWriting(fileName.Substring(1));
				}
			}
			catch(Exception)
			{
				return NtStatus.Success;
			}

			LogFSActionSuccess("WriteFile", fileName, (SftpContext)info.Context, "Ofs:{1} Len:{0} Written:{2}",
				buffer.Length, offset, bytesWritten);
			return NtStatus.Success;
		}

		private bool FileWriteFinished(string fileName, int bufferLength, long offset)
		{
			var fileWriteFinished = false;
			long fileSize;
			lockedFiles.TryGetValue(fileName, out fileSize);
			if(offset + bufferLength == fileSize)
				fileWriteFinished = true;
			return fileWriteFinished;
		}

		private async void LockFileForWriting(string file)
		{
			while(true)
			{
				try
				{
					var httpResponseMessage = await httpClient.PutAsync($"https://{ConnectionInfo.Host}:8443/fileState/lock",
						new StringContent(JsonSerializer.Serialize(new LockRequest { file = file }), Encoding.UTF8,
							"application/json"));
					if(httpResponseMessage.IsSuccessStatusCode)
						break;
				}
				catch(TaskCanceledException)
				{
				}
			}

		}

		private async void UnlockFileForWriting(string file)
		{
			while(true)
			{
				try
				{
					var httpResponseMessage = await httpClient.PutAsync($"https://{ConnectionInfo.Host}:8443/fileState/unlock",
						new StringContent(JsonSerializer.Serialize(new LockRequest { file = file }), Encoding.UTF8,
							"application/json"));
					if(httpResponseMessage.IsSuccessStatusCode)
						break;
				}
				catch(TaskCanceledException)
				{
				}
			}
		}

		NtStatus IDokanOperations.FlushFileBuffers(string fileName, IDokanFileInfo info)
		{
			try
			{
				return FlushFileBuffersPrivate(fileName, info);
			}
			catch(Exception)
			{
				return NtStatus.Error;
			}
		}

		private NtStatus FlushFileBuffersPrivate(string fileName, IDokanFileInfo info)
		{
			LogFSActionInit("FlushFile", fileName, (SftpContext)info.Context, "");

			(info.Context as SftpContext).Stream.Flush(); //git use this

			CacheReset(GetUnixPath(fileName));

			LogFSActionSuccess("FlushFile", fileName, (SftpContext)info.Context, "");
			return NtStatus.Success;
		}

		NtStatus IDokanOperations.GetFileInformation(string fileName, out FileInformation fileInfo, IDokanFileInfo info)
		{
			try
			{
				return GetFileInformationPrivate(fileName, out fileInfo, info);
			}
			catch(Exception)
			{
				fileInfo = new FileInformation();
				return NtStatus.Success;
			}
		}

		private NtStatus GetFileInformationPrivate(string fileName, out FileInformation fileInfo, IDokanFileInfo info)
		{
			LogFSActionInit("FileInfo", fileName, (SftpContext)info.Context, "");

			var context = info.Context as SftpContext;

			SftpFileAttributes sftpFileAttributes;
			string path = GetUnixPath(fileName);

			if(context != null)
			{
				/*
				 * Attributtes in streams are causing trouble with git. GetInfo returns wrong length if other context is writing.
				 */
				if(context.Stream != null)
				{
					try
					{
						sftpFileAttributes = GetAttributes(path);
					}
					catch(SftpPathNotFoundException)
					{
						Debug.WriteLine("File not found");
						sftpFileAttributes = null;
					}
				}
				else
					sftpFileAttributes = context.Attributes;
			}
			else
			{
				sftpFileAttributes = CacheGetAttr(path);

				if(sftpFileAttributes == null)
				{
					try
					{
						sftpFileAttributes = GetAttributes(path);
					}
					catch(SftpPathNotFoundException)
					{
						Debug.WriteLine("File not found");
						sftpFileAttributes = null;
					}

					if(sftpFileAttributes != null)
						CacheAddAttr(path, sftpFileAttributes,
							DateTimeOffset.UtcNow.AddSeconds(_attributeCacheTimeout));
				}
			}

			if(sftpFileAttributes == null)
			{
				LogFSActionError("FileInfo", fileName, (SftpContext)info.Context, "No such file - unable to get info");
				fileInfo = new FileInformation();
				return NtStatus.NoSuchFile;
			}

			fileInfo = new FileInformation
			{
				FileName = Path.GetFileName(fileName), //String.Empty,
													   // GetInfo info doesn't use it maybe for sorting .
				CreationTime = sftpFileAttributes.LastWriteTime,
				LastAccessTime = sftpFileAttributes.LastAccessTime,
				LastWriteTime = sftpFileAttributes.LastWriteTime,
				Length = sftpFileAttributes.Size
			};
			if(sftpFileAttributes.IsDirectory || sftpFileAttributes.IsSymbolicLinkToDirectory)
			{
				fileInfo.Attributes |= FileAttributes.Directory;
				fileInfo.Length = 0; // Windows directories use length of 0 
			}

			if(fileName.Length != 1 && fileName[fileName.LastIndexOf('\\') + 1] == '.')
			//aditional check if filename isn't \\
			{
				fileInfo.Attributes |= FileAttributes.Hidden;
			}

			/*if (GroupRightsSameAsOwner(sftpFileAttributes))
			{
			    fileInfo.Attributes |= FileAttributes.Archive;
			}*/
			if(_useOfflineAttribute)
				fileInfo.Attributes |= FileAttributes.Offline;

			if(!this.UserCanWrite(sftpFileAttributes))
				fileInfo.Attributes |= FileAttributes.ReadOnly;

			if(fileInfo.Attributes == 0)
				fileInfo.Attributes = FileAttributes.Normal; //can be only alone

			LogFSActionSuccess("FileInfo", fileName, (SftpContext)info.Context, "Length:{0} Attrs:{1}",
				fileInfo.Length, fileInfo.Attributes);

			return NtStatus.Success;
		}

		NtStatus IDokanOperations.FindFilesWithPattern(string fileName, string searchPattern,
			out IList<FileInformation> files, IDokanFileInfo info)
		{
			files = null;
			return NtStatus.NotImplemented;
		}

		NtStatus IDokanOperations.FindFiles(string fileName, out IList<FileInformation> files, IDokanFileInfo info)
		{
			try
			{
				return FindFilesPrivate(fileName, out files, info);
			}
			catch(Exception)
			{
				files = new List<FileInformation>();
				return NtStatus.Error;
			}
		}

		private NtStatus FindFilesPrivate(string fileName, out IList<FileInformation> files, IDokanFileInfo info)
		{
			LogFSActionInit("FindFiles", fileName, (SftpContext)info.Context, "");

			List<SftpFile> sftpFiles;

			try
			{
				sftpFiles = ListDirectory(GetUnixPath(fileName)).ToList();
				//handle = _sshClient.RequestOpenDir(GetUnixPath(fileName));
			}
			catch(SftpPermissionDeniedException)
			{
				files = null;
				return NtStatus.AccessDenied;
			}

			files = new List<FileInformation>();
			((List<FileInformation>)files).AddRange(sftpFiles.Select(
				file =>
				{
					var sftpFileAttributes = this.ExtendSFtpFileAttributes(file.FullName, file.Attributes);

					var fileInformation = new FileInformation
					{
						Attributes = FileAttributes.NotContentIndexed,
						CreationTime = sftpFileAttributes.LastWriteTime,
						FileName = file.Name,
						LastAccessTime = sftpFileAttributes.LastAccessTime,
						LastWriteTime = sftpFileAttributes.LastWriteTime,
						Length = sftpFileAttributes.Size
					};
					if(sftpFileAttributes.IsSymbolicLink)
					{
						/* Also files must be marked as dir to reparse work on files */
						fileInformation.Attributes |= FileAttributes.ReparsePoint | FileAttributes.Directory;
					}

					if(sftpFileAttributes.IsSocket)
						fileInformation.Attributes |=
							FileAttributes.NoScrubData | FileAttributes.System | FileAttributes.Device;
					else if(sftpFileAttributes.IsDirectory || sftpFileAttributes.IsSymbolicLinkToDirectory)
					{
						fileInformation.Attributes |= FileAttributes.Directory;
						fileInformation.Length = 4096; //test
					}
					else
						fileInformation.Attributes |= FileAttributes.Normal;

					if(file.Name[0] == '.')
						fileInformation.Attributes |= FileAttributes.Hidden;

					if(GroupRightsSameAsOwner(sftpFileAttributes))
						fileInformation.Attributes |= FileAttributes.Archive;

					if(!this.UserCanWrite(sftpFileAttributes))
						fileInformation.Attributes |= FileAttributes.ReadOnly;

					if(_useOfflineAttribute)
						fileInformation.Attributes |= FileAttributes.Offline;

					return fileInformation;
				}));

			int timeout = Math.Max(_attributeCacheTimeout + 2, _attributeCacheTimeout + sftpFiles.Count / 10);

			foreach(var file in sftpFiles)
				CacheAddAttr(GetUnixPath($"{fileName}\\{file.Name}"), file.Attributes,
					DateTimeOffset.UtcNow.AddSeconds(timeout));

			try
			{
				CacheAddDir(GetUnixPath(fileName), new Tuple<DateTime, IList<FileInformation>>(
						(info.Context as SftpContext).Attributes.LastWriteTime,
						files),
					DateTimeOffset.UtcNow.AddSeconds(Math.Max(_attributeCacheTimeout,
						Math.Min(files.Count, _directoryCacheTimeout))));
			}
			catch
			{
			}

			LogFSActionSuccess("FindFiles", fileName, (SftpContext)info.Context, "Count:{0}", files.Count);
			return NtStatus.Success;
		}

		NtStatus IDokanOperations.SetFileAttributes(string fileName, FileAttributes attributes, IDokanFileInfo info)
		{
			try
			{
				return SetFileAttributesPrivate(fileName, attributes, info);
			}
			catch(Exception)
			{
				return NtStatus.Success;
			}
		}

		private NtStatus SetFileAttributesPrivate(string fileName, FileAttributes attributes, IDokanFileInfo info)
		{
			LogFSActionError("SetFileAttr", fileName, (SftpContext)info.Context, "Attrs:{0}", attributes);

			//get actual attributes
			string path = GetUnixPath(fileName);
			SftpFileAttributes currentattr;
			try
			{
				currentattr = GetAttributes(path);
			}
			catch(SftpPathNotFoundException)
			{
				Debug.WriteLine("File not found");
				currentattr = null;
			}

			//rules for changes:
			bool rightsupdate = false;
			if(attributes.HasFlag(FileAttributes.Archive) && !GroupRightsSameAsOwner(currentattr))
			{
				LogFSActionSuccess("SetFileAttr", fileName, (SftpContext)info.Context,
					"Setting group rights to owner");
				//Archive goes ON, rights of group same as owner:
				currentattr.GroupCanWrite = currentattr.OwnerCanWrite;
				currentattr.GroupCanExecute = currentattr.OwnerCanExecute;
				currentattr.GroupCanRead = currentattr.OwnerCanRead;
				rightsupdate = true;
			}

			if(!attributes.HasFlag(FileAttributes.Archive) && GroupRightsSameAsOwner(currentattr))
			{
				LogFSActionSuccess("SetFileAttr", fileName, (SftpContext)info.Context,
					"Setting group rights to others");
				//Archive goes OFF, rights of group same as others:
				currentattr.GroupCanWrite = currentattr.OthersCanWrite;
				currentattr.GroupCanExecute = currentattr.OthersCanExecute;
				currentattr.GroupCanRead = currentattr.OthersCanRead;
				rightsupdate = true;
			}

			//apply new settings:
			if(rightsupdate)
			{
				//apply and reset cache
				try
				{
					SetAttributes(GetUnixPath(fileName), currentattr);
				}
				catch(SftpPermissionDeniedException)
				{
					return NtStatus.AccessDenied;
				}

				CacheReset(path);
				CacheResetParent(path); //parent cache need reset also

				//if context exists, update new rights manually is needed
				SftpContext context = (SftpContext)info.Context;
				if(info.Context != null)
				{
					context.Attributes.GroupCanWrite = currentattr.GroupCanWrite;
					context.Attributes.GroupCanExecute = currentattr.GroupCanExecute;
					context.Attributes.GroupCanRead = currentattr.GroupCanRead;
				}
			}

			return NtStatus.Success;
		}

		NtStatus IDokanOperations.SetFileTime(string fileName, DateTime? creationTime, DateTime? lastAccessTime,
			DateTime? lastWriteTime, IDokanFileInfo info)
		{
			try
			{
				return SetFileTimePrivate(fileName, creationTime, lastAccessTime, lastWriteTime, info);
			}
			catch(Exception)
			{
				return NtStatus.Success;
			}
		}

		private NtStatus SetFileTimePrivate(string fileName, DateTime? creationTime, DateTime? lastAccessTime, DateTime? lastWriteTime, IDokanFileInfo info)
		{
			LogFSActionInit("SetFileTime", fileName, (SftpContext)info.Context, "");

			var sftpattributes = (info.Context as SftpContext).Attributes;
			SftpFileAttributes tempAttributes;
			try
			{
				tempAttributes = GetAttributes(GetUnixPath(fileName));
			}
			catch(SftpPathNotFoundException)
			{
				Debug.WriteLine("File not found");
				tempAttributes = null;
			}

			tempAttributes.LastWriteTime = lastWriteTime ?? (creationTime ?? sftpattributes.LastWriteTime);
			tempAttributes.LastAccessTime = lastAccessTime ?? sftpattributes.LastAccessTime;

			SetAttributes(GetUnixPath(fileName), tempAttributes);

			LogFSActionSuccess("SetFileTime", fileName, (SftpContext)info.Context, "");
			return NtStatus.Success;
		}

		NtStatus IDokanOperations.DeleteFile(string fileName, IDokanFileInfo info)
		{
			try
			{
				return DeleteFilePrivate(fileName, info);
			}
			catch(Exception)
			{
				return NtStatus.Error;
			}
		}

		private NtStatus DeleteFilePrivate(string fileName, IDokanFileInfo info)
		{
			LogFSActionInit("DeleteFile", fileName, (SftpContext)info.Context, "");

			string parentPath = GetUnixPath(fileName.Substring(0, fileName.LastIndexOf('\\')));

			var sftpFileAttributes = CacheGetAttr(parentPath);

			if(sftpFileAttributes == null)
			{
				try
				{
					sftpFileAttributes = GetAttributes(parentPath);
				}
				catch(SftpPathNotFoundException)
				{
					Debug.WriteLine("File not found");
					sftpFileAttributes = null;
				}

				if(sftpFileAttributes != null)
					CacheAddAttr(parentPath, sftpFileAttributes,
						DateTimeOffset.UtcNow.AddSeconds(_attributeCacheTimeout));
			}
			/* shoud be tested, but performance...
			if (IsDirectory)
			{
			    return NtStatus.AccessDenied;
			}*/

			LogFSActionSuccess("DeleteFile", fileName, (SftpContext)info.Context, "Success:{0}",
				UserCanWrite(sftpFileAttributes));
			return UserCanWrite(sftpFileAttributes) ? NtStatus.Success : NtStatus.AccessDenied;
		}

		NtStatus IDokanOperations.DeleteDirectory(string fileName, IDokanFileInfo info)
		{
			try
			{
				return DeleteDirectoryPrivate(fileName, info);
			}
			catch(Exception)
			{
				return NtStatus.Error;
			}
		}

		private NtStatus DeleteDirectoryPrivate(string fileName, IDokanFileInfo info)
		{
			LogFSActionSuccess("DeleteDir", fileName, (SftpContext)info.Context, "");

			string parentPath = GetUnixPath(fileName.Substring(0, fileName.LastIndexOf('\\')));

			var sftpFileAttributes = CacheGetAttr(parentPath);

			if(sftpFileAttributes == null)
			{
				try
				{
					sftpFileAttributes = GetAttributes(parentPath);
				}
				catch(SftpPathNotFoundException)
				{
					Debug.WriteLine("File not found");
					sftpFileAttributes = null;
				}

				if(sftpFileAttributes != null)
					CacheAddAttr(parentPath, sftpFileAttributes,
						DateTimeOffset.UtcNow.AddSeconds(_attributeCacheTimeout));
			}

			if(!UserCanWrite(sftpFileAttributes))
			{
				LogFSActionError("DeleteDir", fileName, (SftpContext)info.Context, "Access denied");
				return NtStatus.AccessDenied;
			}

			var fileNameUnix = GetUnixPath(fileName);
			sftpFileAttributes = this.CacheGetAttr(fileNameUnix);
			if(sftpFileAttributes == null)
			{
				try
				{
					sftpFileAttributes = GetAttributes(fileNameUnix);
				}
				catch(SftpPathNotFoundException)
				{
					return NtStatus.NoSuchFile; //not sure if can happen and what to return
				}
			}

			if(sftpFileAttributes.IsSymbolicLink)
			{
				return NtStatus.Success;
			}

			//test content:
			var dircache = CacheGetDir(GetUnixPath(fileName));
			if(dircache != null)
			{
				bool test = dircache.Item2.Count == 0 ||
							dircache.Item2.All(i => i.FileName == "." || i.FileName == "..");
				if(!test)
				{
					LogFSActionError("DeleteDir", fileName, (SftpContext)info.Context, "Dir not empty");
					return NtStatus.DirectoryNotEmpty;
				}

				LogFSActionSuccess("DeleteDir", fileName, (SftpContext)info.Context, "");
				return NtStatus.Success;
			}

			//no cache hit, test live, maybe we will get why:
			var dir = ListDirectory(GetUnixPath(fileName)).ToList();
			if(dir == null)
			{
				LogFSActionError("DeleteDir", fileName, (SftpContext)info.Context, "Open failed, access denied?");
				return NtStatus.AccessDenied;
			}

			bool test2 = dir.Count == 0 || dir.All(i => i.Name == "." || i.Name == "..");
			if(!test2)
			{
				LogFSActionError("DeleteDir", fileName, (SftpContext)info.Context, "Dir not empty");
				return NtStatus.DirectoryNotEmpty;
			}

			LogFSActionSuccess("DeleteDir", fileName, (SftpContext)info.Context, "");
			return NtStatus.Success;
		}

		NtStatus IDokanOperations.MoveFile(string oldName, string newName, bool replace, IDokanFileInfo info)
		{
			try
			{
				return MoveFilePrivate(oldName, newName, replace, info);
			}
			catch(Exception)
			{
				return NtStatus.Error;
			}
		}

		private NtStatus MoveFilePrivate(string oldName, string newName, bool replace, IDokanFileInfo info)
		{
			LogFSActionInit("MoveFile", oldName, (SftpContext)info.Context, "To:{0} Replace:{1}", newName, replace);

			string oldpath = GetUnixPath(oldName);
			string newpath = GetUnixPath(newName);
			SftpFileAttributes sftpFileAttributes;
			try
			{
				sftpFileAttributes = GetAttributes(newpath);
			}
			catch(SftpPathNotFoundException)
			{
				Debug.WriteLine("File not found");
				sftpFileAttributes = null;
			}

			if(sftpFileAttributes == null)
			{
				(info.Context as SftpContext).Release();

				info.Context = null;
				try
				{
					RenameFile(oldpath, newpath, false);
					CacheResetParent(oldpath);
					CacheResetParent(newpath);
					CacheReset(oldpath);
#if DEBUG && DEBUGSHADOWCOPY
                    try
                    {
                        string shadowCopyDir = Environment.CurrentDirectory + "\\debug-shadow";
                        string tmpFilePath = shadowCopyDir + "\\" + oldName.Replace("/", "\\");
                        string tmpFilePath2 = shadowCopyDir + "\\" + newName.Replace("/", "\\");
                        Directory.CreateDirectory(Directory.GetParent(tmpFilePath2).FullName);
                        if (Directory.Exists(tmpFilePath))
                        {
                            Directory.Move(tmpFilePath, tmpFilePath2);
                        }
                        else
                        {
                            File.Move(tmpFilePath, tmpFilePath2);
                        }
                    }
                    catch (Exception e)
                    {

                    }
#endif
				}
				catch(SftpPermissionDeniedException)
				{
					LogFSActionError("MoveFile", oldName, (SftpContext)info.Context, "To:{0} Access denied", newName);
					return NtStatus.AccessDenied;
				}

				LogFSActionSuccess("MoveFile", oldName, (SftpContext)info.Context, "To:{0} Target didnt exists",
					newName);
				return NtStatus.Success;
			}
			else if(replace)
			{
				(info.Context as SftpContext).Release();

				info.Context = null;

				if(sftpFileAttributes.IsDirectory || sftpFileAttributes.IsSymbolicLinkToDirectory)
					return NtStatus.AccessDenied;

				try
				{
					try
					{
						RenameFile(oldpath, newpath, true);
					}
					catch(NotSupportedException)
					{
						if(!info.IsDirectory)
							DeleteFile(newpath);
						RenameFile(oldpath, newpath, false);
					}
					catch(SftpPathNotFoundException)
					{
						return NtStatus.AccessDenied;
					}

					CacheReset(oldpath);
					CacheResetParent(oldpath);
					CacheResetParent(newpath);
#if DEBUG && DEBUGSHADOWCOPY
                    try
                    {
                        string shadowCopyDir = Environment.CurrentDirectory + "\\debug-shadow";
                        string tmpFilePath = shadowCopyDir + "\\" + oldName.Replace("/", "\\");
                        string tmpFilePath2 = shadowCopyDir + "\\" + newName.Replace("/", "\\");
                        Directory.CreateDirectory(Directory.GetParent(tmpFilePath2).FullName);
                        if (Directory.Exists(tmpFilePath))
                        {
                            Directory.Move(tmpFilePath, tmpFilePath2);
                        }
                        else
                        {
                            File.Move(tmpFilePath, tmpFilePath2);
                        }
                    }
                    catch (Exception e)
                    {

                    }
#endif
				}

				catch(SftpPermissionDeniedException)
				{
					LogFSActionError("MoveFile", oldName, (SftpContext)info.Context, "To:{0} Access denied", newName);
					return NtStatus.AccessDenied;
				} // not tested on sftp3

				LogFSActionSuccess("MoveFile", oldName, (SftpContext)info.Context, "To:{0} Target was replaced",
					newName);
				return NtStatus.Success;
			}

			LogFSActionError("MoveFile", oldName, (SftpContext)info.Context, "To:{0} Target already exists", newName);
			return NtStatus.ObjectNameCollision;
		}

		NtStatus IDokanOperations.SetEndOfFile(string fileName, long length, IDokanFileInfo info)
		{
			try
			{
				return SetEndOfFilePrivate(fileName, length, info);
			}
			catch(Exception)
			{
				return NtStatus.Error;
			}
		}

		private NtStatus SetEndOfFilePrivate(string fileName, long length, IDokanFileInfo info)
		{
			//Log("SetEnd");
			LogFSActionInit("SetEndOfFile", fileName, (SftpContext)info.Context, "Length:{0}", length);
			(info.Context as SftpContext).Stream.SetLength(length);
			if(!lockedFiles.ContainsKey(fileName))
			{
				lockedFiles.TryAdd(fileName, length);
				LockFileForWriting(fileName.Substring(1));
			}
			CacheResetParent(GetUnixPath(fileName));
			LogFSActionSuccess("SetEndOfFile", fileName, (SftpContext)info.Context, "Length:{0}", length);
			return NtStatus.Success;
		}

		NtStatus IDokanOperations.SetAllocationSize(string fileName, long length, IDokanFileInfo info)
		{
			try
			{
				return SetAllocationSizePrivate(fileName, length, info);
			} catch(Exception)
			{
				return NtStatus.Error;
			}
		}

		private NtStatus SetAllocationSizePrivate(string fileName, long length, IDokanFileInfo info)
		{
			//Log("SetSize");
			LogFSActionInit("SetAllocSize", fileName, (SftpContext)info.Context, "Length:{0}", length);
			(info.Context as SftpContext).Stream.SetLength(length);
			if(!lockedFiles.ContainsKey(fileName))
			{
				lockedFiles.TryAdd(fileName, length);
				LockFileForWriting(fileName.Substring(1));
			}
			CacheResetParent(GetUnixPath(fileName));
			LogFSActionSuccess("SetAllocSize", fileName, (SftpContext)info.Context, "Length:{0}", length);
			return NtStatus.Success;
		}

		NtStatus IDokanOperations.LockFile(string fileName, long offset, long length, IDokanFileInfo info)
		{
			LogFSActionError("LockFile", fileName, (SftpContext) info.Context, "NI");
			return NtStatus.Success;
		}

		NtStatus IDokanOperations.UnlockFile(string fileName, long offset, long length, IDokanFileInfo info)
		{
			LogFSActionError("UnlockFile", fileName, (SftpContext) info.Context, "NI");
			return NtStatus.Success;
		}

		NtStatus IDokanOperations.GetDiskFreeSpace(out long free, out long total, out long used, IDokanFileInfo info)
		{
			try
			{
				return GetDiskFreeSpacePrivate(out free, out total, out used, info);
			}
			catch(Exception)
			{
				total = 0x1900000000; //100 GiB
				used = 0xc80000000; // 50 Gib
				free = 0xc80000000;
				return NtStatus.Error;
			}
		}

		private NtStatus GetDiskFreeSpacePrivate(out long free, out long total, out long used, IDokanFileInfo info)
		{
			LogFSActionInit("GetDiskFreeSpace", this._volumeLabel, (SftpContext)info.Context, "");

			Log("GetDiskFreeSpace");

			var diskSpaceInfo = CacheGetDiskInfo();

			bool dfCheck = false;

			if(diskSpaceInfo != null)
			{
				free = diskSpaceInfo.Item1;
				total = diskSpaceInfo.Item2;
				used = diskSpaceInfo.Item3;
			}
			else
			{
				total = 0x1900000000; //100 GiB
				used = 0xc80000000; // 50 Gib
				free = 0xc80000000;
				try
				{
					var information = GetStatus(_rootpath);
					total = (long)(information.TotalBlocks * information.BlockSize);
					free = (long)(information.FreeBlocks * information.BlockSize);
					used = (long)(information.AvailableBlocks * information.BlockSize);
				}
				catch(NotSupportedException)
				{
					dfCheck = true;
				}
				catch(SshException)
				{
					dfCheck = true;
				}

				if(dfCheck)
				{
					// POSIX standard df
					using(var cmd = _sshClient.CreateCommand(string.Format(_dfCommand + " -Pk  {0}", _rootpath),
						Encoding.UTF8))
					{
						cmd.Execute();
						if(cmd.ExitStatus == 0)
						{
							var values = cmd.Result.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
							total = long.Parse(values[values.Length - 5]) << 10;
							used = long.Parse(values[values.Length - 4]) << 10;
							free = long.Parse(values[values.Length - 3]) << 10; //<======maybe to cache all this
						}
					}
				}

				CacheAddDiskInfo(new Tuple<long, long, long>(free, total, used),
					DateTimeOffset.UtcNow.AddMinutes(3));
			}

			LogFSActionSuccess("GetDiskFreeSpace", this._volumeLabel, (SftpContext)info.Context,
				"Free:{0} Total:{1} Used:{2}", free, total, used);
			return NtStatus.Success;
		}

		NtStatus IDokanOperations.GetVolumeInformation(out string volumeLabel, out FileSystemFeatures features,
			out string filesystemName, out uint maximumComponentLength, IDokanFileInfo info)
		{
			LogFSActionInit("GetVolumeInformation", this._volumeLabel, (SftpContext) info.Context, "");

			volumeLabel = _volumeLabel;

			filesystemName = "SSHFS";

			features = FileSystemFeatures.CasePreservedNames | FileSystemFeatures.CaseSensitiveSearch |
			           FileSystemFeatures.SupportsRemoteStorage | FileSystemFeatures.UnicodeOnDisk |
			           FileSystemFeatures.SequentialWriteOnce;
			maximumComponentLength = 256;
			LogFSActionSuccess("GetVolumeInformation", this._volumeLabel, (SftpContext) info.Context,
				"FS:{0} Features:{1}", filesystemName, features);
			return NtStatus.Success;
		}

		NtStatus IDokanOperations.GetFileSecurity(string filename, out FileSystemSecurity security,
			AccessControlSections sections, IDokanFileInfo info)
		{
			LogFSActionInit("GetFileSecurity", filename, (SftpContext) info.Context, "Sections:{0}", sections);

			var sftpattributes = (info.Context as SftpContext).Attributes;
			var rights = FileSystemRights.ReadPermissions | FileSystemRights.ReadExtendedAttributes |
			             FileSystemRights.ReadAttributes | FileSystemRights.Synchronize;

			if(UserCanRead(sftpattributes))
				rights |= FileSystemRights.ReadData;

			if(UserCanWrite(sftpattributes))
				rights |= FileSystemRights.Write;

			if(UserCanExecute(sftpattributes) && info.IsDirectory)
				rights |= FileSystemRights.Traverse;

			security = info.IsDirectory ? new DirectorySecurity() as FileSystemSecurity : new FileSecurity();
			security.AddAccessRule(new FileSystemAccessRule("Everyone", rights, AccessControlType.Allow));
			security.AddAccessRule(new FileSystemAccessRule("Everyone", FileSystemRights.FullControl ^ rights,
				AccessControlType.Deny));
			//not sure this works at all, needs testing
			// if (sections.HasFlag(AccessControlSections.Owner))
			//security.SetOwner(new NTAccount("None"));
			// if (sections.HasFlag(AccessControlSections.Group))
			security.SetGroup(new NTAccount("None"));

			LogFSActionSuccess("GetFileSecurity", filename, (SftpContext) info.Context, "Sections:{0} Rights:{1}",
				sections, rights);
			return NtStatus.Success;
		}

		NtStatus IDokanOperations.SetFileSecurity(string filename, FileSystemSecurity security,
			AccessControlSections sections, IDokanFileInfo info)
		{
			LogFSActionError("SetFileSecurity", filename, (SftpContext) info.Context, "NI");
			return NtStatus.AccessDenied;
		}

		NtStatus IDokanOperations.Unmounted(IDokanFileInfo info)
		{
			LogFSActionError("Unmounted", this._volumeLabel, (SftpContext) info.Context, "NI");
			return NtStatus.Success;
		}

		NtStatus IDokanOperations.Mounted(IDokanFileInfo info)
		{
			LogFSActionError("Mounted", this._volumeLabel, (SftpContext) info.Context, "NI");
			return NtStatus.Success;
		}

		NtStatus IDokanOperations.FindStreams(string fileName, out IList<FileInformation> streams, IDokanFileInfo info)
		{
			//Alternate Data Streams are NFTS-only feature, no need to handle
			streams = new FileInformation[0];
			return NtStatus.NotImplemented;
		}

		#endregion

		#region Events

		public event EventHandler<EventArgs> Disconnected
		{
			add { Session.Disconnected += value; }
			remove { Session.Disconnected -= value; }
		}

		#endregion
	}
}