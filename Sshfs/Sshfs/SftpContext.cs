using System;
using System.IO;
using Renci.SshNet;
using Renci.SshNet.Sftp;

namespace Sshfs
{
	internal sealed class SftpContext : IDisposable
	{
		private SftpFileAttributes _attributes;
		private SftpContextStream _stream;

		public bool deleteOnCloseWorkaround = false;

		public SftpContext(SftpFileAttributes attributes)
		{
			_attributes = attributes;
		}

		public SftpContext(SftpFileAttributes attributes, bool aDeleteOnCloseWorkaround)
		{
			_attributes = attributes;
			this.deleteOnCloseWorkaround = aDeleteOnCloseWorkaround;
		}

		public SftpContext(SftpClient client, string path, FileMode mode, FileAccess access,
			SftpFileAttributes attributes)
		{
			//_stream = client.Open(path, mode, access);
			_stream = new SftpContextStream(client.getSftpSession(), path, mode, access, attributes);
			_attributes = attributes;
		}

		public SftpFileAttributes Attributes
		{
			get { return _attributes; }
		}

		public SftpContextStream Stream
		{
			get { return _stream; }
		}

		#region IDisposable Members

		public void Dispose()
		{
			_attributes = null;

			if(_stream != null)
			{
				_stream.Close();
				_stream = null;
			}

			GC.SuppressFinalize(this);
		}

		#endregion

		public void Release()
		{
			_attributes = null;

			if(_stream != null)
			{
				_stream.Close();
				_stream = null;
			}

			GC.SuppressFinalize(this);
		}

		public override string ToString()
		{
			return $"[{this.GetHashCode():x}]";
		}
	}
}